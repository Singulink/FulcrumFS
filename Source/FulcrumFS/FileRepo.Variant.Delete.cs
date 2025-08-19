namespace FulcrumFS;

/// <content>
/// Contains the implementations of delete variant functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Deletes the specified variant of a file.
    /// </summary>
    public Task DeleteVariantAsync(FileId fileId, string variantId) => DeleteVariantAsync(fileId, variantId, immediateDelete: false);

    private async Task DeleteVariantAsync(FileId fileId, string variantId, bool immediateDelete)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        var file = FindDataFile(fileId, variantId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
        var deleteMarker = GetDeleteMarker(fileId, variantId);

        if (immediateDelete || Options.DeleteDelay <= TimeSpan.Zero)
        {
            if (!file.TryDelete(out var ex))
                await LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex, markerRequired: true).ConfigureAwait(false);
        }
        else
        {
            const string message = "File variant has been marked for deletion.";
            await LogToMarkerAsync(deleteMarker, "DELETE", message, markerRequired: true).ConfigureAwait(false);
        }
    }
}
