namespace FulcrumFS;

/// <content>
/// Contains the implementations of delete file functionality for the file repository.
/// </content>
partial class FileRepo
{
    private async Task DeleteDataFileGroupAsync(FileId fileId, bool immediateDelete)
    {
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

        var deleteMarker = GetDeleteMarker(fileId, null);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
        {
            if (immediateDelete || DeleteDelay <= TimeSpan.Zero)
            {
                var dataFileGroupDir = GetDataFileGroupDirectory(fileId);
                var indeterminateMarker = GetIndeterminateMarker(fileId);

                // If this fails then the file end up in indeterminate or deleted state and will get cleaned up later, so there is no need to throw.

                if (!dataFileGroupDir.TryDelete(recursive: true, out var ex) || !indeterminateMarker.TryDelete(out ex) || !deleteMarker.TryDelete(out ex))
                    await LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex.ToString(), ignoreErrors: true).ConfigureAwait(false);

                return;
            }

            await deleteMarker.CreateAsync(overwrite: false).ConfigureAwait(false);
        }
    }
}
