using System.Diagnostics.CodeAnalysis;
using FulcrumFS.Utilities;

namespace FulcrumFS;

/// <content>
/// Contains the implementations of clean functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Cleans up the repository by removing files deleted longer ago than <see cref="DeleteDelay"/>.
    /// </summary>
    public async Task CleanAsync(CancellationToken cancellationToken = default) =>
        await CleanAsync(resolveIndeterminateCallback: (Func<FileId, Task<IndeterminateResolution>>?)null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Cleans up the repository by removing files deleted longer ago than <see cref="DeleteDelay"/> and updates the state of any indeterminate files added
    /// longer ago than <see cref="IndeterminateDelay"/> using the provided callback function.
    /// </summary>
    public async Task CleanAsync(Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        Func<FileId, Task<IndeterminateResolution>>? asyncCallbackWrapper = null;

        if (resolveIndeterminateCallback is not null)
            asyncCallbackWrapper = fileId => Task.FromResult(resolveIndeterminateCallback(fileId));

        await CleanAsync(asyncCallbackWrapper, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up the repository by removing files deleted longer ago than <see cref="DeleteDelay"/> and updates the state of any indeterminate files added
    /// longer ago than <see cref="IndeterminateDelay"/> using the provided callback function.
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

        var elc = new ExceptionListCapture(ex => ex is not (ArgumentException or ObjectDisposedException or TimeoutException));
        var deletedFileIds = new HashSet<Guid>();
        var indeterminateMarkers = new List<IAbsoluteFilePath>();

        foreach (var markerInfo in _cleanupDirectory.GetChildFilesInfo())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var marker = markerInfo.Path;

            if (marker.Extension == IndeterminateMarkerExtension)
            {
                if (markerInfo.CreationTimeUtc + IndeterminateDelay > DateTime.UtcNow)
                    continue;

                indeterminateMarkers.Add(marker);
            }
            else if (marker.Extension == DeleteMarkerExtension)
            {
                if (markerInfo.CreationTimeUtc + DeleteDelay > DateTime.UtcNow)
                    continue;

                if (!TryGetFileIdStringAndVariant(marker.NameWithoutExtension, out var deleteFileId, out string variantId))
                    continue;

                if (variantId is null)
                {
                    await elc.TryRunAsync(DeleteDataFileGroupAsync(deleteFileId, immediateDelete: true)).ConfigureAwait(false);
                    deletedFileIds.Add(deleteFileId);
                }
                else
                {
                    await elc.TryRunAsync(DeleteFileVariantAsync(deleteFileId, variantId, immediateDelete: true)).ConfigureAwait(false);
                }
            }
        }

        foreach (var indeterminateMarker in indeterminateMarkers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!FileId.TryParse(indeterminateMarker.NameWithoutExtension, out var indeterminateFileId))
                continue;

            var dataFileGroupDir = GetDataFileGroupDirectory(indeterminateFileId);
            var dataFileGroupDirState = dataFileGroupDir.State;

            if (dataFileGroupDirState is EntryState.ParentExists || deletedFileIds.Contains(indeterminateFileId))
            {
                // Parent dir of the group exists but the group itself does not, so we can delete the indeterminate marker since the file group is gone.
                // Ignore errors, we don't want to create an indeterminate marker again if it is gone.

                indeterminateMarker.TryDelete(out _);
                continue;
            }

            // TODO: Handle other entry states to prevent unnecessary resolution callback invocations that inevitably fail later when the resolution is
            // attempted?

            if (resolveIndeterminateCallback is null)
                continue;

            var resolution = await resolveIndeterminateCallback.Invoke(indeterminateFileId).ConfigureAwait(false);

            if (resolution is IndeterminateResolution.Keep)
                await elc.TryRunAsync(DeleteIndeterminateMarkerAsync(indeterminateFileId)).ConfigureAwait(false);
            else if (resolution is IndeterminateResolution.Delete)
                await elc.TryRunAsync(DeleteDataFileGroupAsync(indeterminateFileId, immediateDelete: true)).ConfigureAwait(false);
            else
                throw new ArgumentOutOfRangeException(nameof(resolveIndeterminateCallback), "The provided callback returned an invalid resolution value.");
        }

        if (elc.HasExceptions)
            throw elc.ResultException;

        static bool TryGetFileIdStringAndVariant(string entryName, [MaybeNullWhen(false)] out FileId fileId, out string? variantId)
        {
            int dashIndex = entryName.IndexOf('-');
            string fileIdString;

            if (dashIndex < 0)
            {
                fileIdString = entryName;
                variantId = null;
            }
            else
            {
                fileIdString = entryName[..dashIndex];
                variantId = entryName[(dashIndex + 1)..];
            }

            if (FileId.TryParse(fileIdString, out fileId))
                return true;

            fileIdString = null;
            variantId = null;

            return false;
        }
    }
}
