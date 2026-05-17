namespace FulcrumFS;

/// <content>
/// Contains the implementations of file fetch functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Gets information about an existing file and all of its variants in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The specified file ID does not exist in the repository.</exception>
    public async ValueTask<RepoFileGroupInfo> GetGroupAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var fileDir = GetFileDirectory(_filesDirectory, fileId);

        IAbsoluteFilePath? mainFile = null;
        var variants = new List<RepoFileInfo>();

        try
        {
            foreach (var file in fileDir.GetChildFiles())
            {
                if (!FileExtension.IsValidAndNormalized(file.Extension))
                    continue;

                string name = file.NameWithoutExtension;

                if (name == FileRepoPaths.MainFileName)
                    mainFile = file;
                else if (VariantId.IsValidAndNormalized(name))
                    variants.Add(new RepoFileInfo(fileId, name, file));
            }
        }
        catch (DirectoryNotFoundException)
        {
            throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
        }

        if (mainFile is null)
            throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");

        return new RepoFileGroupInfo(new RepoFileInfo(fileId, variantId: null, mainFile), variants);
    }

    /// <summary>
    /// Gets information about an existing file in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async ValueTask<RepoFileInfo> GetAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var file = FindDataFile(_filesDirectory, fileId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
        return new RepoFileInfo(fileId, variantId: null, file);
    }

    /// <summary>
    /// Opens a file stream for an existing file in the repository using the recommended sharing options.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async ValueTask<FileStream> OpenAsync(FileId fileId)
    {
        var info = await GetAsync(fileId).ConfigureAwait(false);
        return info.Open();
    }

    /// <summary>
    /// Gets information about an existing file variant in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async ValueTask<RepoFileInfo> GetVariantAsync(FileId fileId, string variantId)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        var file = FindDataFile(_filesDirectory, fileId, variantId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
        return new RepoFileInfo(fileId, variantId, file);
    }

    /// <summary>
    /// Opens a file stream for an existing file variant in the repository using the recommended sharing options.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async ValueTask<FileStream> OpenVariantAsync(FileId fileId, string variantId)
    {
        var info = await GetVariantAsync(fileId, variantId).ConfigureAwait(false);
        return info.Open();
    }
}
