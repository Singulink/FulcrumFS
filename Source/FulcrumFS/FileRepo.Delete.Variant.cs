using System.ComponentModel;

namespace FulcrumFS;

/// <content>
/// Contains the implementations of delete variant functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Asynchronously deletes a specified variant of a file.
    /// </summary>
    public Task DeleteFileVariantAsync(FileId fileId, string variantId) => DeleteFileVariantAsync(fileId, variantId, immediateDelete: false);

    private async Task DeleteFileVariantAsync(FileId fileId, string variantId, bool immediateDelete)
    {
        variantId = NormalizeVariantId(variantId);
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

        var file = FindDataFile(fileId, variantId);
        var deleteMarker = GetDeleteMarker(fileId, variantId);

        if (file is null)
            throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");

        if (immediateDelete || DeleteDelay <= TimeSpan.Zero)
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
