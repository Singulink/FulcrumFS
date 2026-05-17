using System.Diagnostics;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of main file add functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <inheritdoc cref="AddVariantAsync(FileId, string, string?, FileProcessingPipeline, CancellationToken)"/>
    public Task<RepoFileInfo> AddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessingPipeline pipeline,
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
    /// <exception cref="InvalidOperationException">The variant already exists or is currently being processed by another operation.</exception>
    public async Task<RepoFileInfo> AddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        var addResult = await TryAddVariantAsync(fileId, variantId, sourceVariantId, pipeline, cancellationToken).ConfigureAwait(false);

        if (addResult is null)
        {
            string message = $"File ID '{fileId}' variant '{variantId}' already exists or is currently being processed by another operation.";
            throw new InvalidOperationException(message);
        }

        return addResult;
    }

    /// <inheritdoc cref="GetOrAddVariantAsync(FileId, string, string?, FileProcessingPipeline, CancellationToken)"/>
    public Task<RepoFileInfo> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessingPipeline pipeline,
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
    public async Task<RepoFileInfo> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        IAbsoluteFilePath variantFile = FindDataFile(_filesDirectory, fileId, variantId);

        if (variantFile is not null)
            return new RepoFileInfo(fileId, variantId, variantFile);

        using (await _fileSync.LockAsync((fileId, variantId), cancellationToken).ConfigureAwait(false))
        {
            variantFile = FindDataFile(_filesDirectory, fileId, variantId);

            if (variantFile is not null)
                return new RepoFileInfo(fileId, variantId, variantFile);

            var sourceFile = FindDataFile(_filesDirectory, fileId, sourceVariantId);

            if (sourceFile is null)
            {
                if (sourceVariantId is null)
                    throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");

                throw new RepoFileNotFoundException($"File ID '{fileId}' or its source variant '{sourceVariantId}' was not found.");
            }

            var sourceFileStream = sourceFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            await using (sourceFileStream.ConfigureAwait(false))
            {
                var dataFile = await AddVariantAsyncCore(
                    fileId, variantId, sourceFileStream, sourceFile.Extension, sourceInRepo: true, leaveOpen: false, pipeline, cancellationToken)
                    .ConfigureAwait(false);

                return new RepoFileInfo(fileId, variantId, dataFile);
            }
        }
    }

    /// <inheritdoc cref="TryAddVariantAsync(FileId, string, string?, FileProcessingPipeline, CancellationToken)"/>
    public Task<RepoFileInfo?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        FileProcessingPipeline pipeline,
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
    public async Task<RepoFileInfo?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        IAbsoluteFilePath variantFile = FindDataFile(_filesDirectory, fileId, variantId);

        if (variantFile is not null)
            return new RepoFileInfo(fileId, variantId, variantFile);

        var sourceFile = FindDataFile(_filesDirectory, fileId, sourceVariantId);

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

                    if (FindDataFile(_filesDirectory, fileId, variantId) is not null)
                        return null;

                    var dataFile = await AddVariantAsyncCore(
                        fileId, variantId, sourceFileStream, sourceFile.Extension, sourceInRepo: true, leaveOpen: false, pipeline, cancellationToken)
                        .ConfigureAwait(false);

                    return new RepoFileInfo(fileId, variantId, dataFile);
                }
            }
            catch (TimeoutException) when (!fileLockEntered)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Adds a new variant file to an existing file in the repository. Variants are not transactional - no indeterminate marker is created.
    /// </summary>
    private async Task<IAbsoluteFilePath> AddVariantAsyncCore(
        FileId fileId,
        string variantId,
        Stream stream,
        string extension,
        bool sourceInRepo,
        bool leaveOpen,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var (context, resultFile) = await ProcessSourceToWorkFileAsync(
            fileId, variantId, stream, extension, sourceInRepo, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);

        await using (context.ConfigureAwait(false))
        {
            var dataFile = GetDataFile(_filesDirectory, fileId, context.Extension, variantId);
            var dataFileDir = dataFile.ParentDirectory;

            Debug.Assert(dataFile.State is EntryState.ParentExists, "Data file was in an unexpected state.");

            try
            {
                resultFile.MoveTo(dataFile, overwrite: false);
            }
            catch (DirectoryNotFoundException)
            {
                // If the file directory does not exist (but its parent does) then it means that a race occurred and the file dir was deleted while we were
                // processing the variant. In that case we act as though adding the variant was successful and the delete happened afterwards by returning
                // success.

                if (dataFileDir.State is not EntryState.ParentExists)
                    throw;
            }

            return dataFile;
        }
    }

    /// <summary>
    /// Adds a new main file to the repository. Creates an indeterminate marker for the file and keeps an exclusive handle on it open so that an active
    /// transaction can be detected by cleanup operations. The returned marker handle must be disposed by the caller when the transaction commits or rolls back.
    /// </summary>
    private async Task<(IAbsoluteFilePath DataFile, IAsyncDisposable IndeterminateMarker)> AddAsyncCore(
        FileId fileId,
        Stream stream,
        string extension,
        bool sourceInRepo,
        bool leaveOpen,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var (context, resultFile) = await ProcessSourceToWorkFileAsync(
            fileId, variantId: null, stream, extension, sourceInRepo, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);

        await using (context.ConfigureAwait(false))
        {
            var dataFile = GetDataFile(_filesDirectory, fileId, context.Extension);
            var dataFileDir = dataFile.ParentDirectory;
            var indeterminateMarker = GetIndeterminateMarker(_cleanupDirectory, fileId);

            const string message = "A transaction has tentatively added this file.";
            var indeterminateMarkerHandle = await CreateExclusiveMarkerAsync(indeterminateMarker, "TRANSACTION PENDING ADD", message, Options.MarkerFileLogging).ConfigureAwait(false);

            try
            {
                Debug.Assert(dataFileDir.State is EntryState.ParentExists or EntryState.ParentDoesNotExist, "Data file group dir is in an unexpected state.");
                dataFileDir.Create();

                resultFile.MoveTo(dataFile, overwrite: false);
            }
            catch
            {
                // Release the exclusive lock on the marker so we can delete it as part of cleanup.

                await indeterminateMarkerHandle.DisposeAsync().ConfigureAwait(false);

                // Attempt to clean up the failed add operation before we throw.

                if (!dataFileDir.TryDelete(out var ex) || !indeterminateMarker.TryDelete(out ex))
                {
                    // If cleanup fails, log to the indeterminate marker file. Leave the file as indeterminate to be on the safe side instead of marking for
                    // deletion in case something went horribly wrong and a conflict occurred somehow, just let indeterminate resolution handle it later.
                    await LogToOptionalMarkerAsync(indeterminateMarker, "ABORTED ADD CLEANUP FAILED", ex, Options.MarkerFileLogging).ConfigureAwait(false);
                }

                throw;
            }

            return (dataFile, indeterminateMarkerHandle);
        }
    }

    /// <summary>
    /// Runs the processing pipeline for an add operation and returns the resulting work file (always located inside the temp working directory). The caller
    /// is responsible for disposing the returned context, which will also delete the temp working directory. On failure, the context is disposed before the
    /// exception propagates.
    /// </summary>
    private async Task<(FileProcessingContext Context, IAbsoluteFilePath ResultFile)> ProcessSourceToWorkFileAsync(
        FileId fileId,
        string? variantId,
        Stream stream,
        string extension,
        bool sourceInRepo,
        bool leaveOpen,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var tempWorkingDir = _tempDirectory.CombineDirectory(GetFileIdAndVariantString(fileId, variantId), PathOptions.None);
        var context = new FileProcessingContext(fileId, variantId, tempWorkingDir, stream, extension, leaveOpen, cancellationToken);

        try
        {
            await pipeline.ExecuteAsync(context, sourceInRepo).ConfigureAwait(false);

            var resultFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);

            // If the result file is not in the temp working directory then we need to copy it there first.

            if (!context.IsTempWorkingFile(resultFile))
            {
                var workFile = context.GetNewWorkFile(context.Extension);
                workFile.ParentDirectory.Create();
                await resultFile.CopyToAsync(workFile, cancellationToken).ConfigureAwait(false);

                resultFile = workFile;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return (context, resultFile);
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
