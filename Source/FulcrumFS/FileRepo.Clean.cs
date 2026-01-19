namespace FulcrumFS;

/// <content>
/// Contains the implementations of clean functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Cleans up the repository by removing files deleted longer ago than <see cref="FileRepoOptions.DeleteDelay"/>.
    /// </summary>
    public async Task CleanAsync(CancellationToken cancellationToken = default) =>
        await CleanAsync(resolveIndeterminateCallback: (Func<FileId, Task<IndeterminateResolution>>?)null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Cleans up the repository by removing files deleted longer ago than <see cref="FileRepoOptions.DeleteDelay"/> and updates the state of indeterminate
    /// files using the provided synchronous callback function.
    /// </summary>
    public async Task CleanAsync(Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        Func<FileId, Task<IndeterminateResolution>>? asyncCallbackWrapper = null;

        if (resolveIndeterminateCallback is not null)
            asyncCallbackWrapper = fileId => Task.FromResult(resolveIndeterminateCallback(fileId));

        await CleanAsync(asyncCallbackWrapper, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up the repository by removing files deleted longer ago than <see cref="FileRepoOptions.DeleteDelay"/> and updates the state of indeterminate
    /// files using the provided asynchronous callback function.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    public async Task CleanAsync(Func<FileId, Task<IndeterminateResolution>>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        IDisposable AcquireCleanSyncLock()
        {
            try
            {
                return _cleanSync.Lock(TimeSpan.Zero, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("Another clean operation is already in progress.");
            }
        }

        using var cleanLock = AcquireCleanSyncLock();

        await EnsureInitializedAsync(forceHealthCheck: true, cancellationToken).ConfigureAwait(false);

        var deletedFiles = new HashSet<FileId>();
        var indeterminateFiles = new List<(FileId Id, IAbsoluteFilePath Marker)>();

        var elc = new ExceptionListCapture(ex => ex is not (ArgumentException or ObjectDisposedException or TimeoutException));

        foreach (var markerInfo in _cleanupDirectory.GetChildFilesInfo())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var marker = markerInfo.Path;

            if (marker.Extension is FileRepoPaths.IndeterminateMarkerExtension)
            {
                if (FileId.TryParseUnsafe(marker.NameWithoutExtension, out var fileId) && !IsFilePendingTxnOutcome(fileId))
                    indeterminateFiles.Add((fileId, markerInfo.Path));
            }
            else if (marker.Extension is FileRepoPaths.DeleteMarkerExtension)
            {
                if (markerInfo.CreationTimeUtc + Options.DeleteDelay > DateTime.UtcNow)
                    continue;

                if (!TryParseFileIdAndVariant(marker.NameWithoutExtension, out var deleteFileId, out string variantId))
                    continue;

                if (variantId is null)
                {
                    await elc.TryRunAsync(DeleteAsync(deleteFileId, immediateDelete: true)).ConfigureAwait(false);
                    deletedFiles.Add(deleteFileId);
                }
                else
                {
                    await elc.TryRunAsync(DeleteVariantAsync(deleteFileId, variantId, immediateDelete: true)).ConfigureAwait(false);
                }
            }
        }

        foreach (var indeterminateFile in indeterminateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileDir = GetFileDirectory(indeterminateFile.Id);
            var fileDirState = fileDir.State;

            if (fileDirState is EntryState.ParentExists || deletedFiles.Contains(indeterminateFile.Id))
            {
                // Parent dir of the file container dir exists but the file dir itself does not, so we can delete the indeterminate marker since the file dir is
                // gone. Ignore errors, we don't want to recreate an indeterminate marker if it is gone.

                indeterminateFile.Marker.TryDelete(out _);
                continue;
            }

            // TODO: Handle other entry states to prevent unnecessary resolution callback invocations that will likely fail later when the resolution is
            // attempted?

            if (resolveIndeterminateCallback is null)
                continue;

            var resolution = await resolveIndeterminateCallback.Invoke(indeterminateFile.Id).ConfigureAwait(false);

            if (resolution is IndeterminateResolution.Keep)
                await elc.TryRunAsync(DeleteIndeterminateMarkerAsync(indeterminateFile.Id)).ConfigureAwait(false);
            else if (resolution is IndeterminateResolution.Delete)
                await elc.TryRunAsync(DeleteAsync(indeterminateFile.Id, immediateDelete: true)).ConfigureAwait(false);
            else
                throw new ArgumentOutOfRangeException(nameof(resolveIndeterminateCallback), "The provided callback returned an invalid resolution value.");
        }

        elc.ThrowIfHasExceptions();
    }

    private async Task DeleteIndeterminateMarkerAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        var indeterminateMarker = GetIndeterminateMarker(fileId);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
        {
            try
            {
                indeterminateMarker.Delete(ignoreNotFound: true);
            }
            catch (IOException ex) when (ex.GetType() != typeof(FileNotFoundException))
            {
                // If we can't delete the indeterminate marker, log the error but continue.
                // This only needs to be a "best effort" - file will stay in indeterminate state and another attempt to delete the marker will be made later.

                await LogToMarkerAsync(indeterminateMarker, "DELETE MARKER ATTEMPT FAILED", ex, markerRequired: false).ConfigureAwait(false);
            }
        }
    }
}
