using System.Diagnostics;
using System.Linq.Expressions;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of methods that get or add variants to the repository.
/// </content>
partial class FileRepo
{
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
        var startingLockStream = _lockStream; // Store in case we need to check it later.

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
            var dataFileGroupDir = dataFile.ParentDirectory;

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

                Debug.Assert(!dataFileGroupDir.Exists, "Data file group directory should not exist.");
                dataFileGroupDir.Create();
            }
            else
            {
                Debug.Assert(dataFileGroupDir.Exists, "Data file group directory should exist.");
            }

            RetryMove:

            try
            {
                resultFile.MoveTo(dataFile, overwrite: false);
            }
            catch (DirectoryNotFoundException) when (variantId is not null)
            {
                // If the data directory does not exist then it means that a race occurred and the file ID was deleted while we were processing the variant.
                // We can keep thing simple and act as though adding the variant was successful and the delete happened afterwards by returning success, there
                // is no need to throw an exception or report an error here.

                if (!dataFileGroupDir.Exists)
                {
                    // Ensure we didn't lose access to the repo directory/volume before we pretend this succeeded.
                    await EnsureInitializedAsync(forceHealthCheck: true).ConfigureAwait(false);

                    if (_lockStream != startingLockStream)
                    {
                        startingLockStream = _lockStream;
                        goto RetryMove;
                    }

                    return dataFile;
                }
            }
            catch when (variantId is null && indeterminateFile is not null)
            {
                if (dataFileGroupDir.TryDelete(out var ex))
                    indeterminateFile.TryDelete(out ex);

                if (ex is not null)
                    await WriteCleanupRecordAsync(indeterminateFile, "CLEANUP AFTER FAILED MOVE ERROR", ex.ToString(), ignoreErrors: true).ConfigureAwait(false);

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
