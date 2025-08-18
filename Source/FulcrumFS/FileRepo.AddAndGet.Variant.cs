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

        var fileDir = GetFileDirectory(fileId);

        try
        {
            return fileDir.GetChildFiles()
                .Where(f => VariantId.IsNormalized(f.NameWithoutExtension) && FileExtension.IsNormalized(f.Extension))
                .Select(f => f.NameWithoutExtension)
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
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

        return FindDataFile(fileId, variantId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
    }

    /// <inheritdoc cref="AddVariantAsync(FileId, string, string?, FileProcessor, CancellationToken)"/>
    public Task<IAbsoluteFilePath> AddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessor processor,
        CancellationToken cancellationToken = default)
    {
        return AddVariantAsync(fileId, variantId, null, new FileProcessPipeline([processor]), cancellationToken);
    }

    /// <summary>
    /// Adds a new file variant to an existing file in the repository, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="sourceVariantId">The ID of the source variant, or <see langword="null"/> to use the main file as the source.</param>
    /// <param name="processor">The file processor to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="RepoFileVariantAlreadyExistsException">The variant already exists or is currently being processed by another operation.</exception>
    public Task<IAbsoluteFilePath> AddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessor processor,
        CancellationToken cancellationToken = default)
    {
        return AddVariantAsync(fileId, variantId, sourceVariantId, new FileProcessPipeline([processor]), cancellationToken);
    }

    /// <inheritdoc cref="AddVariantAsync(FileId, string, string?, FileProcessPipeline, CancellationToken)"/>
    public Task<IAbsoluteFilePath> AddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        return AddVariantAsync(fileId, variantId, null, pipeline, cancellationToken);
    }

    /// <summary>
    /// Adds a new file variant to an existing file in the repository, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="sourceVariantId">The ID of the source variant, or <see langword="null"/> to use the main file as the source.</param>
    /// <param name="pipeline">The processing pipeline to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="RepoFileVariantAlreadyExistsException">The variant already exists or is currently being processed by another operation.</exception>
    public async Task<IAbsoluteFilePath> AddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        var addResult = await TryAddVariantAsync(fileId, variantId, sourceVariantId, pipeline, cancellationToken).ConfigureAwait(false);

        if (addResult is null)
        {
            string message = $"File ID '{fileId}' variant '{variantId}' already exists or is currently being processed by another operation.";
            throw new RepoFileVariantAlreadyExistsException(message);
        }

        return addResult;
    }

    /// <inheritdoc cref="TryAddVariantAsync(FileId, string, string?, FileProcessor, CancellationToken)"/>
    public Task<IAbsoluteFilePath?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessor processor,
        CancellationToken cancellationToken = default)
    {
        return TryAddVariantAsync(fileId, variantId, null, new FileProcessPipeline([processor]), cancellationToken);
    }

    /// <summary>
    /// Tries to add a new file variant to an existing file in the repository, processing it through the specified file pipeline. Returns <see langword="null"/>
    /// if the variant already exists or is currently being processed by another operation.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="sourceVariantId">The ID of the source variant, or <see langword="null"/> to use the main file as the source.</param>
    /// <param name="processor">The file processor to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    public Task<IAbsoluteFilePath?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessor processor,
        CancellationToken cancellationToken = default)
    {
        return TryAddVariantAsync(fileId, variantId, sourceVariantId, new FileProcessPipeline([processor]), cancellationToken);
    }

    /// <inheritdoc cref="TryAddVariantAsync(FileId, string, string?, FileProcessPipeline, CancellationToken)"/>
    public Task<IAbsoluteFilePath?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        return TryAddVariantAsync(fileId, variantId, null, pipeline, cancellationToken);
    }

    /// <summary>
    /// Tries to add a new file variant to an existing file in the repository, processing it through the specified file pipeline. Returns <see langword="null"/>
    /// if the variant already exists or is currently being processed by another operation.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="sourceVariantId">The ID of the source variant, or <see langword="null"/> to use the main file as the source.</param>
    /// <param name="pipeline">The processing pipeline to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    public async Task<IAbsoluteFilePath?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        IAbsoluteFilePath variantFile = FindDataFile(fileId, variantId);

        if (variantFile is not null)
            return variantFile;

        var sourceFile = FindDataFile(fileId, sourceVariantId);

        if (sourceFile is null)
        {
            if (sourceVariantId is null)
                throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");

            throw new RepoFileNotFoundException($"File ID '{fileId}' or its source variant '{sourceVariantId}' was not found.");
        }

        var sourceFileStream = sourceFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        await using (sourceFileStream.ConfigureAwait(false))
        {
            bool fileLockEntered = false;

            try
            {
                using (await _fileSync.LockAsync((fileId, variantId), 0, cancellationToken).ConfigureAwait(false))
                {
                    fileLockEntered = true;

                    if (FindDataFile(fileId, variantId) is not null)
                        return null;

                    string extension = sourceFile.Extension;
                    return await AddAsyncCore(fileId, variantId, sourceFileStream, extension, false, pipeline, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException) when (!fileLockEntered)
            {
                return null;
            }
        }
    }

    /// <inheritdoc cref="GetOrAddVariantAsync(FileId, string, string?, FileProcessor, CancellationToken)"/>
    public Task<IAbsoluteFilePath> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessor processor,
        CancellationToken cancellationToken = default)
    {
        return GetOrAddVariantAsync(fileId, variantId, null, new FileProcessPipeline([processor]), cancellationToken);
    }

    /// <summary>
    /// Adds a new file variant to an existing file in the repository if it does not already exist, processing it through the specified file processor. If a
    /// variant with the same ID already exists, it returns the existing variant without reprocessing.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="sourceVariantId">The ID of the source variant, or <see langword="null"/> to use the main file as the source.</param>
    /// <param name="processor">The file processor to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    public Task<IAbsoluteFilePath> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessor processor,
        CancellationToken cancellationToken = default)
    {
        return GetOrAddVariantAsync(fileId, variantId, sourceVariantId, new FileProcessPipeline([processor]), cancellationToken);
    }

    /// <inheritdoc cref="GetOrAddVariantAsync(FileId, string, string?, FileProcessPipeline, CancellationToken)"/>
    public Task<IAbsoluteFilePath> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        return GetOrAddVariantAsync(fileId, variantId, null, pipeline, cancellationToken);
    }

    /// <summary>
    /// Adds a new file variant to an existing file in the repository if it does not already exist, processing it through the specified file pipeline. If a
    /// variant with the same ID already exists, it returns the existing variant without reprocessing.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the variant to be added. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="sourceVariantId">The ID of the source variant, or <see langword="null"/> to use the main file as the source.</param>
    /// <param name="pipeline">The processing pipeline to use for the variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    public async Task<IAbsoluteFilePath> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        IAbsoluteFilePath variantFile = FindDataFile(fileId, variantId);

        if (variantFile is not null)
            return variantFile;

        var sourceFile = FindDataFile(fileId, sourceVariantId);

        if (sourceFile is null)
        {
            if (sourceVariantId is null)
                throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");

            throw new RepoFileNotFoundException($"File ID '{fileId}' or its source variant '{sourceVariantId}' was not found.");
        }

        var sourceFileStream = sourceFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        await using (sourceFileStream.ConfigureAwait(false))
        using (await _fileSync.LockAsync((fileId, variantId), cancellationToken).ConfigureAwait(false))
        {
            variantFile = FindDataFile(fileId, variantId);

            if (variantFile is not null)
                return variantFile;

            string extension = sourceFile.Extension;
            return await AddAsyncCore(fileId, variantId, sourceFileStream, extension, false, pipeline, cancellationToken).ConfigureAwait(false);
        }
    }
}
