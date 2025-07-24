namespace FulcrumFS;

/// <content>
/// Contains the implementations of delete file functionality for the file repository.
/// </content>
partial class FileRepo
{
    private async Task DeleteDataFileGroupAsync(FileId fileId, bool immediateDelete)
    {
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);
        using var fileLock = await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false);

        var dataFileGroupDir = GetDataFileGroupDirectory(fileId);
        var deleteMarker = GetDeleteMarker(fileId, null);
        var indeterminateMarker = GetIndeterminateMarker(fileId);

        if (immediateDelete || Options.DeleteDelay <= TimeSpan.Zero)
        {
            if (!dataFileGroupDir.TryDelete(recursive: true, out var ex) || !indeterminateMarker.TryDelete(out ex) || !deleteMarker.TryDelete(out ex))
                await LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex, markerRequired: true).ConfigureAwait(false);

            return;
        }
        else
        {
            const string message = "File has been marked for deletion.";
            await LogToMarkerAsync(deleteMarker, "DELETE", message, markerRequired: true).ConfigureAwait(false);
            indeterminateMarker.TryDelete(out _);
        }
    }
}
