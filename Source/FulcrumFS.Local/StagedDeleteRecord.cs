namespace FulcrumFS;

/// <summary>
/// The durable record of the deletions of a staged <see cref="FileRepoStagedTransaction"/>, written when the transaction is staged and held open until it has
/// committed or rolled back. Not thread-safe: the owning transaction serializes every call through its own lock.
/// </summary>
/// <remarks>
/// <para>
/// On a repository whose format has delete journals, the record is one journal listing every deletion, committed by a single rename. A version 1 repository
/// must not contain journals, so there the record is the indeterminate marker of each deleted file, written, flushed and held exactly as an ordinary
/// <see cref="FileRepoTransaction"/> does, and each deletion is committed or rolled back the way an ordinary transaction does it. The guarantees are the same
/// either way; the markers cost a flush and a file handle per file.</para>
/// <para>
/// Holding the record open is what keeps the cleaner from resolving its files while the transaction can still commit or roll back. Releasing it without
/// finishing leaves them indeterminate for the cleaner to resolve through the resolution callback.</para>
/// </remarks>
internal abstract class StagedDeleteRecord : IAsyncDisposable
{
    /// <summary>
    /// Creates a record over a staged delete journal that lists the specified files.
    /// </summary>
    public static StagedDeleteRecord FromJournal(FileRepo repository, RepoFileSystem.DeleteJournal journal, IReadOnlyCollection<FileId> fileIds) =>
        new JournalRecord(repository, journal, fileIds);

    /// <summary>
    /// Opens the indeterminate marker of each of the specified files, flushed to disk, and returns the record holding them. The files are not checked again:
    /// a file that is gone or has been deleted since its deletion was recorded is handled by the commit, as in an ordinary transaction. If a marker cannot be
    /// written, the markers written so far are rolled back and the exception is propagated.
    /// </summary>
    public static async Task<StagedDeleteRecord> CreateMarkersAsync(FileRepo repository, IReadOnlyCollection<FileId> fileIds)
    {
        var record = new MarkerRecord(repository);

        try
        {
            foreach (var fileId in fileIds)
                record.Add(fileId, await repository.TxnOpenDeleteMarkerAsync(fileId).ConfigureAwait(false));
        }
        catch
        {
            // Nothing has been staged, so the transaction can be staged again or rolled back: undo what was written. A marker that cannot be cleared is left
            // for the cleaner, which resolves it through the callback like any indeterminate file.
            await record.DiscardAsync(new ExceptionListCapture(null)).ConfigureAwait(false);
            throw;
        }

        return record;
    }

    /// <summary>
    /// Commits the deletions in a single step, if the record has one, before anything else of the transaction is committed. A failure is thrown; nothing has
    /// been committed then, and calling the method again resumes where it failed.
    /// </summary>
    public abstract void Commit();

    /// <summary>
    /// Applies the committed deletions according to the repository's <see cref="FileRepoOptions.DeleteMode"/> and releases the record. Failures are captured
    /// by <paramref name="elc"/> and the affected files are left for the cleaner.
    /// </summary>
    public abstract Task ApplyAsync(ExceptionListCapture elc);

    /// <summary>
    /// Rolls back the deletions and releases the record. Failures are captured by <paramref name="elc"/> and the affected files are left for the cleaner.
    /// </summary>
    public abstract Task DiscardAsync(ExceptionListCapture elc);

    /// <summary>
    /// Releases whatever the record still holds without finishing it, which leaves its files indeterminate for the cleaner. Failures are ignored.
    /// </summary>
    public abstract ValueTask DisposeAsync();

    private sealed class JournalRecord(FileRepo repository, RepoFileSystem.DeleteJournal journal, IReadOnlyCollection<FileId> fileIds) : StagedDeleteRecord
    {
        private bool _isCommitted;

        // One atomic rename however many files the journal lists, flushed to disk. A journal that is already gone was resolved by the cleaner, which only
        // happens if the handle was lost, so its deletions are left to that resolution.
        public override void Commit() => _isCommitted = journal.Commit();

        public override async Task ApplyAsync(ExceptionListCapture elc)
        {
            // In immediate mode the listed files are deleted now and the journal is removed once all of them are gone; a deletion that cannot complete keeps
            // the journal, the durable record of the deletion, for the cleaner to finish. In the deferred modes (both defer main files) the committed journal
            // stays as the delete record of its files, and the cleaner applies the delete delay to it.
            if (_isCommitted && repository.Options.DeleteMode is DeleteMode.Immediate)
            {
                bool allDeleted = true;

                foreach (var fileId in fileIds)
                {
                    // A deletion that threw did not complete either, so it keeps the journal as well.
                    if (!await elc.TryRunAsync(DeleteFileAsync(fileId)).ConfigureAwait(false))
                        allDeleted = false;
                }

                if (allDeleted)
                    elc.TryRun(journal.DeleteCommitted);

                async Task DeleteFileAsync(FileId fileId)
                {
                    if (!await repository.TxnCommitStagedDeleteAsync(fileId).ConfigureAwait(false))
                        allDeleted = false;
                }
            }

            await elc.TryRunAsync(journal.DisposeAsync()).ConfigureAwait(false);
        }

        public override async Task DiscardAsync(ExceptionListCapture elc) => await elc.TryRunAsync(journal.DiscardAsync()).ConfigureAwait(false);

        public override async ValueTask DisposeAsync()
        {
            try { await journal.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }

    private sealed class MarkerRecord(FileRepo repository) : StagedDeleteRecord
    {
        // The shared handle on each deleted file's indeterminate marker, in the order the deletions were recorded. An entry is taken out before its deletion
        // is finished, so whatever is left is still held.
        private readonly Queue<(FileId FileId, IAsyncDisposable Marker)> _markers = [];

        public void Add(FileId fileId, IAsyncDisposable marker) => _markers.Enqueue((fileId, marker));

        // Each deletion commits on its own, as in an ordinary transaction, so there is no single step that could fail before the others.
        public override void Commit() { }

        public override async Task ApplyAsync(ExceptionListCapture elc)
        {
            while (_markers.TryDequeue(out var entry))
                await elc.TryRunAsync(CommitDeleteAsync(entry.FileId, entry.Marker)).ConfigureAwait(false);

            async Task CommitDeleteAsync(FileId fileId, IAsyncDisposable marker)
            {
                await marker.DisposeAsync().ConfigureAwait(false);
                await repository.TxnCommitDeleteAsync(fileId).ConfigureAwait(false);
            }
        }

        public override async Task DiscardAsync(ExceptionListCapture elc)
        {
            while (_markers.TryDequeue(out var entry))
                await elc.TryRunAsync(RollbackDeleteAsync(entry.FileId, entry.Marker)).ConfigureAwait(false);

            async Task RollbackDeleteAsync(FileId fileId, IAsyncDisposable marker)
            {
                await marker.DisposeAsync().ConfigureAwait(false);
                await repository.TxnRollbackDeleteAsync(fileId).ConfigureAwait(false);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            while (_markers.TryDequeue(out var entry))
            {
                try { await entry.Marker.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }
}
