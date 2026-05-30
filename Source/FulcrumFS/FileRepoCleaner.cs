using Microsoft.Extensions.Options;

namespace FulcrumFS;

/// <summary>
/// Cleans up a file repository: enumerates marker files in the repository's cleanup directory, removes files whose delete markers have aged past the supplied
/// delete delay, and resolves indeterminate files via an optional caller-supplied callback.
/// </summary>
/// <remarks>
/// A <see cref="FileRepoCleaner"/> does not require an active <c>FileRepo</c> and can be used to clean a repository from a separate process or scheduled task.
/// The repository must already be initialized; the cleaner does not create or repair repository structure. Cross-process safety is provided by file-system
/// primitives: indeterminate markers held open by active transactions are detected via an exclusive-share probe, and concurrent clean operations against the
/// same repository are serialized by an exclusive lock file inside the cleanup directory.
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

            var elc = new ExceptionListCapture(ex => ex is IOException);

            foreach (var markerInfo in _fs.CleanupDirectory.GetChildFilesInfo())
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
            }

            foreach (var indeterminateFile in indeterminateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileDir = _fs.GetFileDirectory(indeterminateFile.Id);
                var fileDirState = fileDir.State;

                if (fileDirState is EntryState.ParentExists || deletedFiles.Contains(indeterminateFile.Id))
                {
                    // Parent dir of the file container dir exists but the file dir itself does not, so we can delete the indeterminate marker since the file dir is
                    // gone. Ignore errors, we don't want to recreate an indeterminate marker if it is gone.

                    indeterminateFile.Marker.TryDelete(out _);
                    continue;
                }

                if (resolveIndeterminateCallback is null)
                    continue;

                var resolution = await resolveIndeterminateCallback.Invoke(indeterminateFile.Id).ConfigureAwait(false);

                if (resolution is IndeterminateResolution.Keep)
                {
                    await elc.TryRunAsync(_fs.TryClearIndeterminateStateAsync(indeterminateFile.Id)).ConfigureAwait(false);
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

            elc.ThrowIfHasExceptions();
        }
    }

    private async Task<bool> DeleteFileAsync(FileId fileId, FileStream hintStream, bool immediate)
    {
        var fileDir = _fs.GetFileDirectory(fileId);
        var indeterminateMarker = _fs.GetIndeterminateMarker(fileId);

        if (immediate)
        {
            var elc = new ExceptionListCapture(ex => ex is IOException);

            // Initial alias sweep: delete every alias marker before recursively removing the file group directory. Any failure aborts and keeps the delete
            // marker for the next clean run to retry.
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
                await _fs.WriteMarkerEntryAsync(hintStream, "DELETE ATTEMPT FAILED", elc.ResultException).ConfigureAwait(false);
                return false;
            }

            if ((!fileDir.TryDelete(recursive: true, out var ex) && fileDir.State is EntryState.Exists) || !indeterminateMarker.TryDelete(out ex))
            {
                await _fs.WriteMarkerEntryAsync(hintStream, "DELETE ATTEMPT FAILED", ex).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        else
        {
            // Deferred mode (only reached from the indeterminate-resolution path; the cleanup-hint loop always calls with immediate=true). The caller already
            // logged a "DELETE" entry to the hint when it acquired exclusive ownership, so no additional log entry is needed here.
            indeterminateMarker.TryDelete(out _);
            return false;
        }
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
