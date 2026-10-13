using Microsoft.Extensions.Options;

namespace FulcrumFS;

/// <summary>
/// Cleans up a file repository: enumerates marker files and delete journals in the repository's cleanup directory, removes files whose delete markers or
/// committed delete journals have aged past the supplied delete delay, and resolves indeterminate files (including the files listed in staged delete journals
/// that no transaction holds) via an optional caller-supplied callback.
/// </summary>
/// <remarks>
/// A <see cref="FileRepoCleaner"/> does not require an active <c>FileRepo</c> and can be used to clean a repository from a separate process or scheduled task.
/// The repository must already be initialized; the cleaner does not create or repair repository structure. Cross-process safety is provided by file-system
/// primitives: indeterminate markers and staged delete journals held open by active transactions are detected via an exclusive-share probe, and concurrent
/// clean operations against the same repository are serialized by an exclusive lock file inside the cleanup directory.
/// </remarks>
public class FileRepoCleaner : IFileRepoCleaner
{
    /// <summary>
    /// Gets the configuration options for the cleaner. The returned instance is frozen and cannot be modified.
    /// </summary>
    public FileRepoCleaningOptions Options { get; }

    private readonly RepoFileSystem _fs;
    private readonly IAbsoluteFilePath _infoFile;
    private readonly IAbsoluteFilePath _cleanLockFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaner"/> class with the specified options.
    /// </summary>
    public FileRepoCleaner(FileRepoCleaningOptions options)
    {
        if (options.BaseDirectory is null)
            throw new ArgumentException("Base directory must be set in options.", nameof(options));

        options.Freeze();
        Options = options;

        _fs = new RepoFileSystem(options.BaseDirectory, options.MarkerFileLogging);
        _infoFile = options.BaseDirectory.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None);
        _cleanLockFile = _fs.CleanupDirectory.CombineFile(FileRepoPaths.CleanupLockFileName, PathOptions.None);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaner"/> class with the specified options.
    /// </summary>
    public FileRepoCleaner(IOptions<FileRepoCleaningOptions> options) : this(options.Value) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaner"/> class with the specified base directory and optional configuration action.
    /// </summary>
    /// <param name="baseDirectory">The base directory of the file repository to be cleaned.</param>
    /// <param name="configure">An optional action that configures additional options.</param>
    public FileRepoCleaner(IAbsoluteDirectoryPath baseDirectory, Action<FileRepoCleaningOptions>? configure = null)
        : this(BuildOptions(baseDirectory, configure)) { }

    private static FileRepoCleaningOptions BuildOptions(IAbsoluteDirectoryPath baseDirectory, Action<FileRepoCleaningOptions>? configure)
    {
        var options = new FileRepoCleaningOptions(baseDirectory);
        configure?.Invoke(options);
        return options;
    }

    /// <inheritdoc/>
    public Task CleanAsync(TimeSpan deleteDelay, CancellationToken cancellationToken = default) =>
        CleanAsync(deleteDelay, (Func<FileId, Task<IndeterminateResolution>>?)null, cancellationToken);

    /// <inheritdoc/>
    public Task CleanAsync(TimeSpan deleteDelay, Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        Func<FileId, Task<IndeterminateResolution>>? asyncCallbackWrapper = null;

        if (resolveIndeterminateCallback is not null)
            asyncCallbackWrapper = fileId => Task.FromResult(resolveIndeterminateCallback(fileId));

        return CleanAsync(deleteDelay, asyncCallbackWrapper, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task CleanAsync(TimeSpan deleteDelay, Func<FileId, Task<IndeterminateResolution>>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deleteDelay, TimeSpan.Zero, nameof(deleteDelay));

        FileStream cleanLockStream;

        try
        {
            cleanLockStream = _cleanLockFile.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            // The optimistic open failed. Diagnose what went wrong by inspecting the directory state. Order of checks: missing base dir > missing repo marker >
            // missing cleanup dir > genuine lock contention.

            if (Options.BaseDirectory.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The file repository base directory '{Options.BaseDirectory.PathDisplay}' was not found.", ex);
            }

            if (_infoFile.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The directory '{Options.BaseDirectory.PathDisplay}' is not an initialized FulcrumFS repository.", ex);
            }

            if (_fs.CleanupDirectory.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The FulcrumFS repository at '{Options.BaseDirectory.PathDisplay}' is in an incomplete state (the cleanup directory is missing).", ex);
            }

            throw new InvalidOperationException("Another clean operation is already in progress.", ex);
        }

        using (cleanLockStream)
        {
            // Now that we hold the clean lock, verify the repository advertises a clean-compat version this library understands. This is checked AFTER lock
            // acquisition so the more specific "not a repository" / "incomplete state" diagnostics in the catch block above take precedence when applicable.
            RepoInfoFile.VerifyCleanCompatSupported(_infoFile);

            var deletedFiles = new HashSet<FileId>();
            var indeterminateFiles = new List<(FileId Id, IAbsoluteFilePath Marker)>();
            var stagedJournals = new List<(IAbsoluteFilePath Journal, IReadOnlyList<FileId> FileIds)>();
            var damagedJournals = new List<IAbsoluteFilePath>();

            var elc = new ExceptionListCapture(ex => ex is IOException);

            // The listing is taken in full before anything is processed. The pass deletes entries of this directory as it goes (a delete hint or a committed
            // journal takes the indeterminate markers of its files with it), and on Unix enumerating an entry deleted after the directory was read throws.
            // Entries that disappear after the listing are skipped below like any other marker that is gone.
            var markerInfos = _fs.CleanupDirectory.GetChildFilesInfo().ToList();

            foreach (var markerInfo in markerInfos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var marker = markerInfo.Path;

                if (marker.Extension is FileRepoPaths.IndeterminateMarkerExtension)
                {
                    if (!FileId.TryParseUnsafe(marker.NameWithoutExtension, out var fileId))
                        continue;

                    // Probe whether the marker is currently held by an active transaction by attempting to open it with exclusive access. If the open fails with
                    // an IOException then a transaction is holding the marker open and we must skip it. The probe is released immediately - actions on the marker
                    // below are best-effort and tolerate the marker being re-acquired by a new transaction in the meantime.

                    try
                    {
                        using var probe = marker.OpenStream(FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    indeterminateFiles.Add((fileId, marker));
                }
                else if (marker.Extension is FileRepoPaths.DeleteMarkerExtension)
                {
                    if (markerInfo.CreationTimeUtc + deleteDelay > DateTime.UtcNow)
                        continue;

                    if (!RepoFileSystem.TryParseFileIdAndVariant(marker.NameWithoutExtension, out var deleteFileId, out string? variantId))
                        continue;

                    // Take over exclusive (FileShare.None) ownership of the hint for the duration of the delete attempt. Unlike the probe pattern used for
                    // indeterminate markers, a delete hint is a persistent retry token that may be re-acquired by future cleaner passes or by a foreground
                    // takeover (Phase 6b reentrancy) - so the cleaner must hold actual ownership through processing, not just verify nobody currently holds it.
                    // If acquisition fails with an IOException, either the file has vanished (a previous attempt finished) or another actor holds it: skip.

                    FileStream hintStream;

                    try
                    {
                        hintStream = RepoFileSystem.TakeoverExclusiveMarker(marker);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    bool deleteSucceeded = false;

                    await using (hintStream.ConfigureAwait(false))
                    {
                        // TryRunAsync wraps the call so unforeseen exceptions are captured by `elc` (operational failures are handled inside the methods,
                        // which log to the held hint stream and return false). The local async wrappers exist solely to surface the bool return through
                        // TryRunAsync's void-returning contract.

                        if (variantId is null)
                        {
                            await elc.TryRunAsync(RunFileDeleteAsync()).ConfigureAwait(false);

                            if (deleteSucceeded)
                                deletedFiles.Add(deleteFileId);

                            async Task RunFileDeleteAsync() =>
                                deleteSucceeded = await DeleteFileAsync(deleteFileId, hintStream, immediate: true).ConfigureAwait(false);
                        }
                        else
                        {
                            await elc.TryRunAsync(RunVariantDeleteAsync()).ConfigureAwait(false);

                            async Task RunVariantDeleteAsync() =>
                                deleteSucceeded = await DeleteVariantAsync(deleteFileId, variantId, hintStream).ConfigureAwait(false);
                        }
                    }

                    // Brief window after dispose before TryDelete where another actor could re-acquire the hint. That's harmless: by this point the
                    // delete marker and data files are gone, so any party that grabs the hint sees "nothing to do" and TryDeletes it itself - idempotent.
                    if (deleteSucceeded)
                        marker.TryDelete(out _);
                }
                else if (marker.Extension is FileRepoPaths.StagedJournalExtension)
                {
                    if (!RepoFileSystem.IsJournalName(marker.NameWithoutExtension))
                        continue;

                    // Probe whether the staged transaction that wrote the journal still holds it, exactly as for indeterminate markers, and read it through
                    // the probe. A held journal is skipped: its transaction can still commit or roll back.
                    IReadOnlyList<FileId>? journalFileIds;

                    try
                    {
                        using var probe = marker.OpenStream(FileMode.Open, FileAccess.Read, FileShare.None);
                        journalFileIds = RepoFileSystem.TryReadJournal(probe, out var fileIds) ? fileIds : null;
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    if (journalFileIds is null)
                    {
                        // A torn staged journal was never fully written and flushed, so staging never returned and the application never committed these
                        // deletions: discard it and leave the files alone.
                        marker.TryDelete(out _);
                        continue;
                    }

                    // Nobody holds a complete journal, so whether its transaction committed is unknown, as for an indeterminate file. Only the resolution
                    // callback can decide; without one the journal is left for a pass that has one.
                    if (resolveIndeterminateCallback is not null)
                        stagedJournals.Add((marker, journalFileIds));
                }
                else if (marker.Extension is FileRepoPaths.CommittedJournalExtension)
                {
                    // A committed journal is the delete record of every file it lists, so the delete delay applies to it as to a delete marker.
                    if (markerInfo.CreationTimeUtc + deleteDelay > DateTime.UtcNow || !RepoFileSystem.IsJournalName(marker.NameWithoutExtension))
                        continue;

                    // Take over exclusive ownership of the journal while applying it, as for a delete hint, so that another cleaner, or the transaction that
                    // committed it and may still be applying it, does not process it at the same time. If acquisition fails, another actor holds it or it is
                    // already gone: skip.

                    FileStream journalStream;

                    try
                    {
                        journalStream = RepoFileSystem.TakeoverExclusiveMarker(marker, FileAccess.Read);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    bool journalApplied = false;

                    await using (journalStream.ConfigureAwait(false))
                    {
                        await elc.TryRunAsync(ApplyJournalAsync()).ConfigureAwait(false);

                        async Task ApplyJournalAsync()
                        {
                            if (!RepoFileSystem.TryReadJournal(journalStream, out var fileIds))
                            {
                                // The journal was complete and flushed before it was committed, so damage means failing storage or outside interference
                                // rather than a crash. Nothing is deleted on its say-so: it is left in place and reported once the pass has finished.
                                damagedJournals.Add(marker);
                                return;
                            }

                            journalApplied = await DeleteJournalFilesAsync(fileIds, deletedFiles, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (journalApplied)
                        marker.TryDelete(out _);
                }
            }

            foreach (var indeterminateFile in indeterminateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileDir = _fs.GetFileDirectory(indeterminateFile.Id);
                var fileDirState = fileDir.State;

                if (fileDirState is EntryState.ParentExists || deletedFiles.Contains(indeterminateFile.Id))
                {
                    // Parent dir of the file container dir exists but the file dir itself does not, so we can delete the indeterminate marker since the file dir is
                    // gone.

                    await elc.TryRunAsync(RemoveResolvedIndeterminateMarkerAsync(indeterminateFile.Marker)).ConfigureAwait(false);
                    continue;
                }

                if (resolveIndeterminateCallback is null)
                    continue;

                var resolution = await resolveIndeterminateCallback.Invoke(indeterminateFile.Id).ConfigureAwait(false);

                if (resolution is IndeterminateResolution.Keep)
                {
                    await elc.TryRunAsync(RemoveResolvedIndeterminateMarkerAsync(indeterminateFile.Marker)).ConfigureAwait(false);
                }
                else if (resolution is IndeterminateResolution.Delete)
                {
                    // If deleteDelay is positive, write a delete marker (starting the grace clock) and remove the indeterminate marker. The next clean run after
                    // the delay will physically delete the file. If deleteDelay is zero, delete immediately.
                    //
                    // Acquire exclusive ownership of the delete hint via CreateOrTakeoverExclusiveMarker so a concurrent foreground takeover or second cleaner
                    // instance is forced to back off through the delete attempt, mirroring the cleanup-hint loop above.

                    bool immediate = deleteDelay <= TimeSpan.Zero;
                    var deleteHint = _fs.GetDeleteMarker(indeterminateFile.Id, null);
                    IAsyncDisposable hintHandle;

                    try
                    {
                        hintHandle = await _fs.CreateOrTakeoverExclusiveMarkerAsync(
                            deleteHint, "DELETE", "File has been marked for deletion via indeterminate resolution.").ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Another actor currently holds the hint; let them finish and re-resolve next pass.
                        continue;
                    }

                    bool deleteSucceeded = false;

                    await using (hintHandle.ConfigureAwait(false))
                    {
                        await elc.TryRunAsync(RunIndeterminateDeleteAsync()).ConfigureAwait(false);

                        async Task RunIndeterminateDeleteAsync() =>
                            deleteSucceeded = await DeleteFileAsync(indeterminateFile.Id, (FileStream)hintHandle, immediate).ConfigureAwait(false);
                    }

                    if (deleteSucceeded)
                    {
                        deletedFiles.Add(indeterminateFile.Id);
                        deleteHint.TryDelete(out _);
                    }
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(resolveIndeterminateCallback), "The provided callback returned an invalid resolution value.");
                }
            }

            foreach (var stagedJournal in stagedJournals)
            {
                // Resolve every listed file whose group still exists through the callback. The journal is removed once all of them are resolved; if any
                // could not be, it is kept and the next pass resolves them again, which is idempotent for the ones already handled.
                bool allResolved = true;

                foreach (var fileId in stagedJournal.FileIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (deletedFiles.Contains(fileId) || IsFileGroupGone(fileId))
                        continue;

                    var resolution = await resolveIndeterminateCallback!.Invoke(fileId).ConfigureAwait(false);

                    if (resolution is IndeterminateResolution.Keep)
                        continue;

                    if (resolution is not IndeterminateResolution.Delete)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(resolveIndeterminateCallback), "The provided callback returned an invalid resolution value.");
                    }

                    bool resolved = false;
                    await elc.TryRunAsync(RunStagedDeleteAsync()).ConfigureAwait(false);

                    if (!resolved)
                        allResolved = false;

                    async Task RunStagedDeleteAsync() =>
                        resolved = await ResolveStagedDeleteAsync(fileId, deleteDelay, deletedFiles).ConfigureAwait(false);
                }

                if (allResolved)
                    stagedJournal.Journal.TryDelete(out _);
            }

            if (damagedJournals.Count > 0)
            {
                string journals = string.Join(", ", damagedJournals.Select(j => $"'{j.PathDisplay}'"));

                elc.Add(new IOException(
                    $"Committed delete journals are damaged and were left in place without deleting the files they list: {journals}. A committed journal " +
                    "is complete and flushed to disk before it is committed, so the damage indicates failing storage or outside interference."));
            }

            elc.ThrowIfHasExceptions();
        }
    }

    /// <summary>
    /// Physically deletes every file group listed in a committed delete journal; a group that is already gone counts as deleted. Returns
    /// <see langword="false"/> if any deletion could not complete, in which case the journal must be kept for the next pass.
    /// </summary>
    private async Task<bool> DeleteJournalFilesAsync(IReadOnlyList<FileId> fileIds, HashSet<FileId> deletedFiles, CancellationToken cancellationToken)
    {
        bool allDeleted = true;

        foreach (var fileId in fileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryDeleteFileGroup(fileId, out var ex))
            {
                deletedFiles.Add(fileId);
                continue;
            }

            allDeleted = false;

            // Nothing can be logged to the journal without breaking its format, so the failure is logged to the file's delete marker, as an immediate
            // deletion in the foreground does. The journal stays the record of the deletion, so a failure to log is only lost diagnostics.
            try
            {
                await _fs.LogToMarkerAsync(_fs.GetDeleteMarker(fileId, null), "DELETE ATTEMPT FAILED", ex).ConfigureAwait(false);
            }
            catch (IOException) { }
        }

        return allDeleted;
    }

    /// <summary>
    /// Carries out a <see cref="IndeterminateResolution.Delete"/> resolution for a file listed in a staged delete journal that nobody holds: with no delete
    /// delay the file group is deleted now, otherwise the file's delete marker is written and flushed to disk, which starts the delay. Returns
    /// <see langword="false"/> if another actor holds the delete marker or the deletion could not complete.
    /// </summary>
    private async Task<bool> ResolveStagedDeleteAsync(FileId fileId, TimeSpan deleteDelay, HashSet<FileId> deletedFiles)
    {
        // Acquire exclusive ownership of the delete hint, as the indeterminate resolution does, so a concurrent foreground takeover or second cleaner
        // instance is forced to back off through the delete attempt.
        var deleteHint = _fs.GetDeleteMarker(fileId, null);
        IAsyncDisposable hintHandle;

        try
        {
            hintHandle = await _fs.CreateOrTakeoverExclusiveMarkerAsync(
                deleteHint, "DELETE", "File has been marked for deletion via staged delete journal resolution.").ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }

        bool deleteSucceeded;

        await using (hintHandle.ConfigureAwait(false))
        {
            var hintStream = (FileStream)hintHandle;

            if (deleteDelay > TimeSpan.Zero)
            {
                // Once the journal is removed the delete marker is the only record of the deletion the application decided on, so it is flushed to disk
                // first. Unlike the indeterminate resolution, the file's indeterminate marker is left alone: it belongs to another transaction, if any.
                DurableIO.FlushFile(hintStream);
                DurableIO.FlushDirectory(_fs.CleanupDirectory);
                return true;
            }

            deleteSucceeded = await DeleteFileAsync(fileId, hintStream, immediate: true).ConfigureAwait(false);
        }

        if (deleteSucceeded)
        {
            deletedFiles.Add(fileId);
            deleteHint.TryDelete(out _);
        }

        return deleteSucceeded;
    }

    /// <summary>
    /// Removes an indeterminate marker that this pass found unheld and has resolved, unless a transaction has taken it since. A transaction that deletes the
    /// file in the meantime (between the probe and now, which spans the callback) has its deletion pending through the marker, which is then its only record:
    /// the marker is left to that transaction, which removes it when it finishes, or to the next pass if it lets go of the marker without finishing.
    /// </summary>
    /// <remarks>
    /// The marker is opened exclusively and removed when it is closed, so that no transaction can take it between the check and the removal: one that tries
    /// in the meantime finds it in use, waits, and then creates a marker of its own. Removing the marker by its path while holding it would not do on Windows:
    /// on a file system without POSIX deletion semantics the marker would be left pending deletion until the handle is closed, and an open in between would
    /// fail with access denied, which a deletion does not wait for. A failure other than the marker being in use or gone is logged to the marker, which stays
    /// for the next pass.
    /// </remarks>
    private async Task RemoveResolvedIndeterminateMarkerAsync(IAbsoluteFilePath marker)
    {
        FileStream stream;

        try
        {
            stream = marker.OpenStream(FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (IOException ex) when (RepoFileSystem.IsInUse(ex))
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _fs.LogToOptionalMarkerAsync(marker, "DELETE MARKER ATTEMPT FAILED", ex).ConfigureAwait(false);
            return;
        }

        await stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <see langword="true"/> if a file's group directory is gone: it is missing (or its shard directory is) while the files directory exists.
    /// </summary>
    private bool IsFileGroupGone(FileId fileId) => _fs.GetFileDirectory(fileId).State switch
    {
        EntryState.ParentExists => true,
        EntryState.ParentDoesNotExist => _fs.FilesDirectory.State is EntryState.Exists,
        _ => false,
    };

    private async Task<bool> DeleteFileAsync(FileId fileId, FileStream hintStream, bool immediate)
    {
        if (immediate)
        {
            // Any failure keeps the delete marker for the next clean run to retry.
            if (!TryDeleteFileGroup(fileId, out var ex))
            {
                await _fs.WriteMarkerEntryAsync(hintStream, "DELETE ATTEMPT FAILED", ex).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        else
        {
            // Deferred mode (only reached from the indeterminate-resolution path; the cleanup-hint loop always calls with immediate=true). The caller already
            // logged a "DELETE" entry to the hint when it acquired exclusive ownership, so no additional log entry is needed here. Once the indeterminate
            // marker is gone the hint is the only record of the deletion the application decided on, so it is flushed to disk first.
            DurableIO.FlushFile(hintStream);
            DurableIO.FlushDirectory(_fs.CleanupDirectory);
            _fs.GetIndeterminateMarker(fileId).TryDelete(out _);
            return false;
        }
    }

    /// <summary>
    /// Physically deletes a file group and then the file's indeterminate marker, if any. A group that is already gone counts as deleted.
    /// </summary>
    private bool TryDeleteFileGroup(FileId fileId, [NotNullWhen(false)] out Exception? exception)
    {
        var fileDir = _fs.GetFileDirectory(fileId);
        var elc = new ExceptionListCapture(ex => ex is IOException);

        // Initial alias sweep: delete every alias marker before recursively removing the file group directory. Any failure aborts the deletion.
        try
        {
            foreach (var marker in fileDir.GetChildFiles("*" + FileRepoPaths.AliasMarkerExtension))
                elc.TryRun(() => marker.Delete());
        }
        catch (DirectoryNotFoundException)
        {
            // File group directory is already gone; nothing to sweep.
        }

        if (elc.HasExceptions)
        {
            exception = elc.ResultException;
            return false;
        }

        if (!fileDir.TryDelete(recursive: true, out exception) && fileDir.State is EntryState.Exists)
            return false;

        return _fs.GetIndeterminateMarker(fileId).TryDelete(out exception);
    }

    private async ValueTask<bool> DeleteVariantAsync(FileId fileId, string variantId, FileStream hintStream)
    {
        var inGroupDeleteMarker = _fs.GetInGroupDeleteMarker(fileId, variantId);

        // Commit gate: a cleanup hint without a committed in-group delete marker means the foreground crashed pre-commit (the operation never happened) or the
        // retirement already fully completed and the delete marker was torn down. Either way there is nothing to retire - let the stray hint be swept without
        // touching any data. This is the low-overhead "dirty vs. clean" discriminator: absence of the delete marker == not retired.
        if (inGroupDeleteMarker.State is not EntryState.Exists)
            return true;

        var inv = _fs.BuildGroupInventory(fileId);
        var elc = new ExceptionListCapture(ex => ex is IOException);

        // Opportunistically remove any orphan rebase markers in this group while we have the inventory open. Orphans have no source data, no chosen data, and
        // no surviving aliases - they are pure residue from a rebase whose final marker-delete step was lost. Cleaners do not raise corruption events; the
        // foreground rollforward surfaces the OrphanRebaseMarker event on observation.
        foreach (var orphan in inv.OrphanRebaseMarkers)
            orphan.Marker.TryDelete(out _);

        if (inv.DataFiles.TryGetValue(variantId, out var data))
        {
            // Retired real variant. If it is the source of a rebase - indicated by an in-flight rebase marker (crash mid-materialization) or by the presence of
            // surviving, non-retired alias dependents (crash after commit but before the foreground materialized) - finish/perform that rebase before removing
            // the source data. Materialization copies FROM the source, so the source must stay intact until the chosen survivor is fully materialized.
            //
            // In the normal (non-crash) flow the foreground already materialized and re-pointed, leaving no rebase marker and no surviving dependents pointing
            // at this source, so no rebase work happens here and we proceed straight to residue deletion.
            string? chosen = null;

            foreach (var rm in inv.ResumableRebases)
            {
                if (rm.Plan.SourceVariantId == variantId)
                {
                    chosen = rm.Plan.ChosenVariantId;
                    break;
                }
            }

            var survivors = new List<RepoFileSystem.GroupAliasEntry>();

            foreach (var a in inv.Aliases)
            {
                if (a.SourceVariantId == variantId && !inv.Retired.Contains(a.VariantId))
                    survivors.Add(a);
            }

            if (chosen is null && survivors.Count > 0)
            {
                // No pinned chosen yet - recompute it deterministically. This matches what the foreground would have selected (earliest by CreationTimeUtc with
                // VariantId as the tiebreaker), so foreground and cleaner always promote the same survivor.
                survivors.Sort(static (x, y) =>
                {
                    int c = x.CreationTimeUtc.CompareTo(y.CreationTimeUtc);
                    return c is not 0 ? c : string.CompareOrdinal(x.VariantId, y.VariantId);
                });

                chosen = survivors[0].VariantId;
            }

            if (chosen is not null)
            {
                var repoint = new List<string>();

                foreach (var s in survivors)
                {
                    if (s.VariantId != chosen)
                        repoint.Add(s.VariantId);
                }

                var plan = new RepoFileSystem.SubtreeRebasePlan(variantId, data.Extension, chosen, repoint);
                await elc.TryRunAsync(_fs.RebaseSubtreeAsync(fileId, plan)).ConfigureAwait(false);
            }

            if (!elc.HasExceptions)
                elc.TryRun(() => data.Path.Delete());
        }
        else
        {
            // Retired alias variant (or its data is already gone from a prior pass). Delete the alias marker if it is still present. Note this only ever
            // matches a retired dependent - surviving dependents are never given a delete marker, so the cleaner never deletes a survivor's marker.
            foreach (var a in inv.Aliases)
            {
                if (a.VariantId == variantId)
                {
                    elc.TryRun(() => a.Marker.Delete());
                    break;
                }
            }
        }

        if (elc.HasExceptions)
        {
            await _fs.WriteMarkerEntryAsync(hintStream, "DELETE ATTEMPT FAILED", elc.ResultException).ConfigureAwait(false);
            return false;
        }

        // Residue gone - tear down the in-group delete marker (it only existed to gate fetches while the data file lingered). The cleanup-dir hint is deleted
        // by the main loop after the held stream is disposed.
        inGroupDeleteMarker.TryDelete(out _);
        return true;
    }
}
