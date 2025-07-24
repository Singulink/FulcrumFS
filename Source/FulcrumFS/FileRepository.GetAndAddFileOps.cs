using System.Diagnostics;
using FulcrumFS.Utilities;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of methods that get or add files to the repository.
/// </content>
partial class FileRepository
{
    private async Task<(Guid FileId, IAbsoluteFilePath File)> AddAsyncImpl(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        extension = NormalizeExtension(extension);

        Guid fileId;
        string fileIdString;
        IAbsoluteDirectoryPath dataFileDirectory;
        IAbsoluteFilePath cleanupDeleteFile;

        while (true)
        {
            fileId = SecureGuid.Create();

            if (fileId == Guid.Empty)
                continue;

            fileIdString = GetFileIdString(fileId);

            using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
            {
                dataFileDirectory = GetDataFileDirectory(fileIdString);
                cleanupDeleteFile = GetCleanupDeleteFile(fileIdString, null);

                if (dataFileDirectory.Exists || cleanupDeleteFile.Exists)
                    continue;

                lock (_processingFileIds)
                {
                    if (!_processingFileIds.Add(fileId))
                        continue;
                }

                break;
            }
        }

        try
        {
            var result = await AddAsyncImpl(fileId, null, stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
            return (fileId, result);
        }
        finally
        {
            lock (_processingFileIds)
            {
                _processingFileIds.Remove(fileId);
            }
        }
    }

    private async Task<IAbsoluteFilePath> AddAsyncImpl(
        Guid fileId,
        string? variantId,
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken)
    {
        ValidateFileId(fileId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        string fileIdString = GetFileIdString(fileId);
        string tempDirName = variantId is null ? fileIdString : GetVariantEntryName(fileIdString, variantId);

        var tempWorkingDir = _tempDirectory.CombineDirectory(tempDirName, PathOptions.None);
        FileProcessContext context = null;

        try
        {
            context = new FileProcessContext(fileId, tempWorkingDir, stream, extension, leaveOpen, cancellationToken);

            for (int i = 0; i < pipeline.Processors.Length; i++)
            {
                var processor = pipeline.Processors[i];
                var result = await processor.ProcessAsyncInternal(context, cancellationToken).ConfigureAwait(false);

                bool isNextProcessorLast = i >= pipeline.Processors.Length - 2;
                await context.AdvanceToNextStepAsync(result, isNextProcessorLast).ConfigureAwait(false);
            }

            var resultFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
            var dataFile = GetDataFile(fileIdString, context.Extension, variantId);

            // If the result file not in the temp directory then we need to copy it there first.

            if (!resultFile.PathDisplay.StartsWith(tempWorkingDir.PathDisplay, StringComparison.Ordinal))
            {
                var workFile = context.GetNewWorkFile(context.Extension);
                await resultFile.CopyToAsync(workFile, cancellationToken).ConfigureAwait(false);

                resultFile = workFile;
                cancellationToken.ThrowIfCancellationRequested();
            }

            IAbsoluteFilePath indeterminateFile = null;

            if (variantId is null)
            {
                // Create an indeterminate file to indicate that the file is not yet committed.
                indeterminateFile = GetCleanupIndeterminateFile(fileIdString);
                await indeterminateFile.CreateAsync(overwrite: false).ConfigureAwait(false);
            }

            if (variantId is null)
            {
                Debug.Assert(!_dataDirectory.Exists, "Data directory should not exist.");
                _dataDirectory.Create();
            }

            try
            {
                resultFile.MoveTo(dataFile, overwrite: false);
            }
            catch (DirectoryNotFoundException) when (variantId is not null)
            {
                // If the data directory does not exist then it means that file ID was deleted while we were processing the variant.
                // We can just pretend that adding the variant won the race and the delete happened afterwards by returning success in this case.
                if (!_dataDirectory.Exists)
                {
                    // Make sure we didn't lose the repo directory before we pretend this succeeded.
                    await EnsureInitializedAsync(forceLockStreamCheck: true).ConfigureAwait(false);
                    return dataFile;
                }
            }
            catch when (variantId is null)
            {
                if (_dataDirectory.TryDelete(out _))
                    indeterminateFile?.TryDelete(out _);

                throw;
            }

            return dataFile;
        }
        finally
        {
            if (context is not null)
                await context.DisposeAsync().ConfigureAwait(false);

            tempWorkingDir.TryDelete(recursive: true, out _);
        }
    }
}
