using System.Globalization;
using System.Text;

namespace FulcrumFS;

/// <content>
/// Delete journals: the single file in the cleanup directory in which a staged transaction records the files it deletes. Contains the on-disk format, its
/// strict parser, and the handle a staged transaction holds on its journal.
/// </content>
partial class RepoFileSystem
{
    // Journal format (UTF-8, LF line endings):
    //
    //   FULCRUMFS DELETE JOURNAL 1
    //   {file ID, 36 characters, lowercase D format}     (N lines, N >= 1)
    //   END {N as decimal} {CRC-32 of the N ID lines including their line feeds, as 8 lowercase hex digits}
    //
    // Parsing is strict so that a journal torn by a crash or damaged on disk (truncated, a zeroed block, a changed character) is never taken for a valid one.

    // 36 characters of the ID plus the line feed.
    private const int JournalIdLineLength = 37;

    private static ReadOnlySpan<byte> JournalHeaderLine => "FULCRUMFS DELETE JOURNAL 1\n"u8;

    private static ReadOnlySpan<byte> JournalEndLinePrefix => "END "u8;

    /// <summary>
    /// Builds the absolute path of a staged delete journal: <c>cleanup/{journalId}.staged</c>. Its committed path differs only in the extension.
    /// </summary>
    public IAbsoluteFilePath GetStagedJournal(Guid journalId) =>
        CleanupDirectory.CombineFile(journalId.ToString("D") + FileRepoPaths.StagedJournalExtension, PathOptions.None);

    /// <summary>
    /// Returns <see langword="true"/> if a cleanup directory entry name (without its extension) is a journal ID: a GUID in lowercase <c>D</c> format.
    /// </summary>
    public static bool IsJournalName(string nameWithoutExtension) =>
        Guid.TryParseExact(nameWithoutExtension, "D", out var journalId) && journalId.ToString("D") == nameWithoutExtension;

    /// <summary>
    /// Creates a staged delete journal listing the specified files, flushes it and the cleanup directory to disk, and returns the handle that keeps it open.
    /// The journal must not already exist. If writing or flushing fails, the partially written journal is deleted and the exception is propagated.
    /// </summary>
    public async Task<DeleteJournal> CreateStagedJournalAsync(Guid journalId, IReadOnlyCollection<FileId> fileIds)
    {
        var stagedFile = GetStagedJournal(journalId);
        byte[] content = FormatJournal(fileIds);

        // Any open handle keeps out the cleaner's exclusive (FileShare.None) probe, which is how it tells that the transaction is still active. Sharing
        // deletion is what Windows requires to rename the journal while the handle stays open; sharing reading lets it be inspected.
        var stream = stagedFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

        try
        {
            await stream.WriteAsync(content).ConfigureAwait(false);

            // The application commits its database once staging returns, after which the journal is the only record of these deletions.
            DurableIO.FlushFile(stream);
            DurableIO.FlushDirectory(CleanupDirectory);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            stagedFile.TryDelete(out _);
            throw;
        }

        return new DeleteJournal(stream, stagedFile, stagedFile.WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None));
    }

    /// <summary>
    /// Creates a committed delete journal listing the specified files, for deletions that commit without being staged first, and flushes it to disk. The
    /// journal is written and flushed under its staged name and then renamed, so that its committed name never holds an incomplete journal, which the cleaner
    /// would take for damage; a staged journal that a crash leaves behind is resolved like that of a staged transaction that ended before committing. If
    /// writing, flushing or renaming fails, the journal is deleted and the exception is propagated.
    /// </summary>
    public async Task CreateCommittedJournalAsync(Guid journalId, IReadOnlyCollection<FileId> fileIds)
    {
        var stagedFile = GetStagedJournal(journalId);
        var committedFile = stagedFile.WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None);
        byte[] content = FormatJournal(fileIds);

        // The open handle keeps the cleaner from resolving the staged journal or applying the committed one before it is durable. Sharing deletion is what
        // Windows requires to rename it while the handle stays open.
        var stream = stagedFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);

        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content).ConfigureAwait(false);

                // The content is flushed before the rename: a file system may make a rename durable before the data of a file it has not flushed, which
                // would leave an empty or partial journal under the committed name. The rename is flushed as a staged transaction's commit flushes it, so a
                // power loss cannot turn the committed journal back into a staged one that leaves its files to the resolution callback.
                DurableIO.FlushFile(stream);
                stagedFile.MoveTo(committedFile);
                DurableIO.FlushFile(stream);
                DurableIO.FlushDirectory(CleanupDirectory);
            }
        }
        catch
        {
            stagedFile.TryDelete(out _);
            committedFile.TryDelete(out _);
            throw;
        }
    }

    /// <summary>
    /// Reads a delete journal and validates it strictly. Returns <see langword="false"/> if the content is torn or damaged. The journal is opened with
    /// sharing that does not block a transaction holding it from renaming or deleting it; an I/O failure, including a journal held exclusively by a cleaner,
    /// is thrown.
    /// </summary>
    public static bool TryReadJournal(IAbsoluteFilePath journal, [NotNullWhen(true)] out IReadOnlyList<FileId>? fileIds)
    {
        using var stream = journal.OpenStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return TryReadJournal(stream, out fileIds);
    }

    /// <summary>
    /// Reads a delete journal from the start of an open stream and validates it strictly. Returns <see langword="false"/> if the content is torn or damaged;
    /// an I/O failure is thrown.
    /// </summary>
    public static bool TryReadJournal(FileStream stream, [NotNullWhen(true)] out IReadOnlyList<FileId>? fileIds)
    {
        long length = stream.Length;

        if (length > Array.MaxLength)
        {
            fileIds = null;
            return false;
        }

        byte[] content = new byte[length];
        stream.Position = 0;
        stream.ReadExactly(content);

        return TryParseJournal(content, out fileIds);
    }

    /// <summary>
    /// Formats the content of a delete journal listing the specified files, in their enumeration order.
    /// </summary>
    internal static byte[] FormatJournal(IReadOnlyCollection<FileId> fileIds)
    {
        if (fileIds.Count is 0)
            throw new ArgumentException("A delete journal must list at least one file.", nameof(fileIds));

        byte[] idLines = new byte[JournalIdLineLength * fileIds.Count];
        int position = 0;

        foreach (var fileId in fileIds)
        {
            Encoding.ASCII.GetBytes(fileId.ToString(), idLines.AsSpan(position, JournalIdLineLength - 1));
            idLines[position + JournalIdLineLength - 1] = (byte)'\n';
            position += JournalIdLineLength;
        }

        byte[] endLine = Encoding.ASCII.GetBytes(GetJournalEndLine(fileIds.Count, Crc32.Compute(idLines)));
        byte[] content = new byte[JournalHeaderLine.Length + idLines.Length + endLine.Length];

        JournalHeaderLine.CopyTo(content);
        idLines.CopyTo(content, JournalHeaderLine.Length);
        endLine.CopyTo(content, JournalHeaderLine.Length + idLines.Length);

        return content;
    }

    /// <summary>
    /// Parses and strictly validates the content of a delete journal. Returns <see langword="false"/> for anything other than exactly the header line, one
    /// or more ID lines and an END line whose count and checksum match them, with nothing after its line feed. An empty content is torn.
    /// </summary>
    internal static bool TryParseJournal(ReadOnlySpan<byte> content, [NotNullWhen(true)] out IReadOnlyList<FileId>? fileIds)
    {
        fileIds = null;

        if (!content.StartsWith(JournalHeaderLine))
            return false;

        var body = content[JournalHeaderLine.Length..];
        var ids = new List<FileId>();
        int idLinesLength = 0;

        // Every ID line has the same length and no file ID starts with "END ", so the ID lines run up to the first line that does. Anything else is torn.
        while (!body[idLinesLength..].StartsWith(JournalEndLinePrefix))
        {
            var line = body[idLinesLength..];

            if (line.Length < JournalIdLineLength || line[JournalIdLineLength - 1] is not (byte)'\n')
                return false;

            if (!TryParseJournalFileId(line[..(JournalIdLineLength - 1)], out var fileId))
                return false;

            ids.Add(fileId);
            idLinesLength += JournalIdLineLength;
        }

        if (ids.Count is 0)
            return false;

        // Comparing the rest with the exact expected END line checks the count, the checksum and the final line feed, and that nothing follows it, which
        // together fix the total length at the header plus 37 bytes per ID plus the END line.
        byte[] expectedEndLine = Encoding.ASCII.GetBytes(GetJournalEndLine(ids.Count, Crc32.Compute(body[..idLinesLength])));

        if (!body[idLinesLength..].SequenceEqual(expectedEndLine))
            return false;

        fileIds = ids;
        return true;
    }

    private static bool TryParseJournalFileId(ReadOnlySpan<byte> idChars, [NotNullWhen(true)] out FileId? fileId)
    {
        // A non-ASCII byte decodes to '?', which fails to parse. Journals are written in lowercase, so an uppercase ID is damage even though it would name the
        // same file; TryParseUnsafe rejects it.
        return FileId.TryParseUnsafe(Encoding.ASCII.GetString(idChars), out fileId);
    }

    private static string GetJournalEndLine(int count, uint crc) => string.Create(CultureInfo.InvariantCulture, $"END {count} {crc:x8}\n");

    /// <summary>
    /// The open handle a staged transaction holds on its delete journal from staging until its commit or rollback has finished. While it is open the cleaner
    /// skips the staged journal, so the journal's files are never resolved while their transaction can still commit.
    /// </summary>
    public sealed class DeleteJournal : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private State _state;

        internal DeleteJournal(FileStream stream, IAbsoluteFilePath stagedFile, IAbsoluteFilePath committedFile)
        {
            _stream = stream;
            StagedFile = stagedFile;
            CommittedFile = committedFile;
        }

        private enum State
        {
            Staged,
            Renamed,
            Committed,
            Gone,
        }

        /// <summary>
        /// Gets the path of the journal while it is staged.
        /// </summary>
        public IAbsoluteFilePath StagedFile { get; }

        /// <summary>
        /// Gets the path of the journal once it is committed.
        /// </summary>
        public IAbsoluteFilePath CommittedFile { get; }

        /// <summary>
        /// Commits the journal by renaming it from staged to committed, one atomic operation however many files it lists, and flushes the rename to disk.
        /// Returns <see langword="false"/> if the staged journal is gone. A failure is thrown, and calling the method again resumes where it failed (a
        /// completed rename is not repeated).
        /// </summary>
        public bool Commit()
        {
            if (_state is State.Staged)
            {
                try
                {
                    // Renamed through its path while the handle stays open, which the handle's FileShare.Delete permits on Windows.
                    StagedFile.MoveTo(CommittedFile);
                    _state = State.Renamed;
                }
                catch (FileNotFoundException)
                {
                    // Only the cleaner removes a staged journal, and only one that nobody holds, so this means the handle was lost and the cleaner has already
                    // resolved the listed files.
                    _state = State.Gone;
                }
            }

            if (_state is State.Renamed)
            {
                // A rename lost to a power loss would turn the journal back into a staged one that nobody holds, leaving every listed file to the
                // resolution callback even though the application committed their deletion.
                DurableIO.FlushFile(_stream);
                DurableIO.FlushDirectory(CommittedFile.ParentDirectory);
                _state = State.Committed;
            }

            return _state is State.Committed;
        }

        /// <summary>
        /// Deletes the committed journal, once every file it lists has been deleted.
        /// </summary>
        public void DeleteCommitted() => CommittedFile.Delete();

        /// <summary>
        /// Rolls back a staged journal: deletes it and closes the handle. The journal is deleted before the handle is closed so the cleaner never finds it
        /// unheld; if the deletion fails, the closed journal is left for the cleaner to resolve through the resolution callback.
        /// </summary>
        public async ValueTask DiscardAsync()
        {
            try
            {
                if (_state is State.Staged)
                    StagedFile.Delete();
            }
            finally
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Closes the handle, leaving the journal on disk in whatever state it is in.
        /// </summary>
        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}
