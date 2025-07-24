using System.Diagnostics;
using System.Threading;

namespace FulcrumFS;

/// <content>
/// Contains the implementations of variant add functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Retrieves a list of variant IDs for the specified file.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The specified file ID does not exist in the repository.</exception>
    public async Task<IList<string>> GetVariantIds(FileId fileId)
    {
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

        var fileGroupDir = GetDataFileGroupDirectory(fileId);

        try
        {
            return fileGroupDir.GetChildFiles()
                .Select(f => f.NameWithoutExtension)
                .Where(n => n is not MainFileName)
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
        }
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

    /// <summary>
    /// Gets an existing file variant from the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async Task<IAbsoluteFilePath> GetVariantAsync(FileId fileId, string variantId)
    {
        variantId = NormalizeVariantId(variantId);
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

        return FindDataFile(fileId, variantId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
    }

    /// <summary>
    /// Adds a new file variant to an existing file in the repository if it does not already exist, processing it through the specified pipeline. If a variant
    /// with the same ID already exists, it returns the existing variant without reprocessing.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="pipeline">The processing pipeline to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    public async Task<IAbsoluteFilePath> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        variantId = NormalizeVariantId(variantId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        IAbsoluteFilePath variantFile = FindDataFile(fileId, variantId);

        if (variantFile is not null)
            return variantFile;

        IAbsoluteFilePath mainFile;
        Stream mainFileStream;

        using (await _fileSync.LockAsync((fileId, null), cancellationToken).ConfigureAwait(false))
        {
            lock (_processingFileIds)
            {
                if (_processingFileIds.Contains(fileId))
                    throw new InvalidOperationException($"File ID '{fileId}' is currently being processed.");
            }

            mainFile = FindDataFile(fileId);

            if (mainFile is null)
                throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");

            mainFileStream = mainFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }

        await using (mainFileStream.ConfigureAwait(false))
        using (await _fileSync.LockAsync((fileId, variantId), cancellationToken).ConfigureAwait(false))
        {
            variantFile = FindDataFile(fileId, variantId);

            if (variantFile is not null)
                return variantFile;

            string extension = mainFile.Extension;
            return await AddAsyncCore(fileId, variantId, mainFileStream, extension, false, pipeline, cancellationToken).ConfigureAwait(false);
        }
    }
}
