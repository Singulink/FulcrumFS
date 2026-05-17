namespace FulcrumFS;

/// <content>
/// Contains the implementations of delete file functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Deletes the specified variant of a file.
    /// </summary>
    public ValueTask DeleteVariantAsync(FileId fileId, string variantId) => DeleteVariantAsync(fileId, variantId, immediateDelete: Options.DeleteMode is DeleteMode.Immediate);

    private async ValueTask DeleteVariantAsync(FileId fileId, string variantId, bool immediateDelete)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        var file = FindDataFile(_filesDirectory, fileId, variantId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
        var deleteMarker = GetDeleteMarker(_cleanupDirectory, fileId, variantId);

        if (immediateDelete)
        {
            if (!file.TryDelete(out var ex))
                await LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex, Options.MarkerFileLogging).ConfigureAwait(false);
        }
        else
        {
            const string message = "File variant has been marked for deletion.";
            await LogToMarkerAsync(deleteMarker, "DELETE", message, Options.MarkerFileLogging).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a file and its variants from the repository.
    /// </summary>
    private async ValueTask DeleteAsync(FileId fileId, bool immediateDelete)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var fileLock = await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false);

        var fileDir = GetFileDirectory(_filesDirectory, fileId);
        var deleteMarker = GetDeleteMarker(_cleanupDirectory, fileId, null);
        var indeterminateMarker = GetIndeterminateMarker(_cleanupDirectory, fileId);

        if (immediateDelete)
        {
            if (!fileDir.TryDelete(recursive: true, out var ex) || !indeterminateMarker.TryDelete(out ex) || !deleteMarker.TryDelete(out ex))
                await LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex, Options.MarkerFileLogging).ConfigureAwait(false);

            return;
        }
        else
        {
            const string message = "File has been marked for deletion.";
            await LogToMarkerAsync(deleteMarker, "DELETE", message, Options.MarkerFileLogging).ConfigureAwait(false);
            indeterminateMarker.TryDelete(out _);
        }
    }
}
