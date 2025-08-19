namespace FulcrumFS;

/// <content>
/// Contains the implementations of variant add functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Returns a list of variant IDs for the specified file.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The specified file ID does not exist in the repository.</exception>
    public async Task<IList<string>> GetVariantIdsAsync(FileId fileId)
    {
        var variants = await GetVariantsAsyncCore(fileId).ConfigureAwait(false);
        return variants.Select(f => f.NameWithoutExtension).ToList();
    }

    /// <summary>
    /// Returns a list of variant file paths for the specified file.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The specified file ID does not exist in the repository.</exception>
    public async Task<IList<IAbsoluteFilePath>> GetVariantsAsync(FileId fileId)
    {
        var variants = await GetVariantsAsyncCore(fileId).ConfigureAwait(false);
        return variants.ToList();
    }

    /// <summary>
    /// Gets an existing file variant from the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async Task<IAbsoluteFilePath> GetVariantAsync(FileId fileId, string variantId)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        return FindDataFile(fileId, variantId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
    }

    /// <summary>
    /// Opens a file stream for an existing file variant in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async Task<FileStream> OpenVariantAsync(FileId fileId, string variantId)
    {
        var variantFile = await GetVariantAsync(fileId, variantId).ConfigureAwait(false);
        return variantFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    private async ValueTask<IEnumerable<IAbsoluteFilePath>> GetVariantsAsyncCore(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var fileDir = GetFileDirectory(fileId);

        try
        {
            return fileDir.GetChildFiles().Where(f => VariantId.IsNormalized(f.NameWithoutExtension) && FileExtension.IsNormalized(f.Extension));
        }
        catch (DirectoryNotFoundException)
        {
            throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
        }
    }
}
