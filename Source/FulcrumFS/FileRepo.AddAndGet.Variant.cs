using System.Diagnostics;

namespace FulcrumFS;

/// <content>
/// Contains the implementations of variant add functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Adds a new file variant to an existing file in the repository, processing it through the specified pipeline. If a variant with the same ID already
    /// exists, it returns the existing variant without reprocessing.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="pipeline">The processing pipeline to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    public async Task<AddVariantResult> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        var result = await GetOrAddVariantAsyncImpl(fileId, variantId, pipeline, openStream: false, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.AddResult is null)
            throw new UnreachableException("Unexpected Error: Variant result is null.");

        return result.AddResult;
    }

    /// <summary>
    /// Opens a file variant stream for an existing file in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async Task<FileStream> OpenVariantAsync(FileId fileId, string variantId)
    {
        var result = await GetOrAddVariantAsyncImpl(fileId, variantId, null, openStream: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);

        if (result.Stream is null)
            throw new RepoFileNotFoundException($"Variant '{variantId}' for file ID '{fileId}' was not found.");

        return result.Stream;
    }

    /// <summary>
    /// Gets an existing file variant from the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async Task<IAbsoluteFilePath> GetVariantAsync(FileId fileId, string variantId)
    {
        var result = await GetOrAddVariantAsyncImpl(fileId, variantId, null, openStream: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);

        if (result.AddResult is null)
            throw new RepoFileNotFoundException($"Variant '{variantId}' for file ID '{fileId}' was not found.");

        return result.AddResult.File;
    }

    private async Task<(AddVariantResult? AddResult, FileStream? Stream)> GetOrAddVariantAsyncImpl(
        FileId fileId,
        string variantId,
        FileProcessPipeline? pipeline,
        bool openStream,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        variantId = NormalizeVariantId(variantId) ?? throw new ArgumentException("Variant ID cannot be empty.", nameof(variantId));

        IAbsoluteFilePath mainFile;
        Stream mainFileStream;

        IAbsoluteFilePath variantFile;
        FileStream? OpenStream() => openStream ? variantFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;

        using (await _fileSync.LockAsync((fileId, null), cancellationToken).ConfigureAwait(false))
        {
            variantFile = FindDataFile(fileId, variantId);

            if (variantFile is not null)
                return (new(variantFile, true), OpenStream());

            lock (_processingFileIds)
            {
                if (_processingFileIds.Contains(fileId))
                    throw new InvalidOperationException($"File ID '{fileId}' is currently being processed.");
            }

            mainFile = FindDataFile(fileId);

            if (mainFile is null)
                throw new RepoFileNotFoundException($"File ID '{fileId}' does not exist.");

            mainFileStream = mainFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }

        await using (mainFileStream.ConfigureAwait(false))
        using (await _fileSync.LockAsync((fileId, variantId), cancellationToken).ConfigureAwait(false))
        {
            variantFile = FindDataFile(fileId, variantId);

            if (variantFile is not null)
                return (new(variantFile, true), OpenStream());

            if (pipeline is null)
                return (null, null);

            string extension = mainFile.Extension;
            variantFile = await AddAsyncCore(fileId, variantId, mainFileStream, extension, false, pipeline, cancellationToken).ConfigureAwait(false);

            return (new(variantFile, false), OpenStream());
        }
    }
}
