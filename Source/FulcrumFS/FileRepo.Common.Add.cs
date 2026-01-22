using System.Diagnostics;

namespace FulcrumFS;

/// <content>
/// Contains the implementations of add functionality common to files and variants for the file repository.
/// </content>
partial class FileRepo
{
    private async Task<IAbsoluteFilePath> AddCommonAsyncCore(
        FileId fileId,
        string? variantId,
        Stream stream,
        string extension,
        bool sourceInRepo,
        bool leaveOpen,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var tempWorkingDir = _tempDirectory.CombineDirectory(GetFileIdAndVariantString(fileId, variantId), PathOptions.None);
        FileProcessingContext context = null;

        try
        {
            context = new FileProcessingContext(fileId, variantId, tempWorkingDir, stream, extension, leaveOpen, cancellationToken);
            await pipeline.ExecuteAsync(context, sourceInRepo).ConfigureAwait(false);

            var resultFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
            var dataFile = GetDataFile(fileId, context.Extension, variantId);
            var dataFileDir = dataFile.ParentDirectory;

            // If the result file is not in the temp directory then we need to copy it there first.

            if (resultFile.PathDisplay.Length <= tempWorkingDir.PathDisplay.Length ||
                !resultFile.PathDisplay.StartsWith(tempWorkingDir.PathDisplay, StringComparison.Ordinal) ||
                resultFile.PathDisplay[tempWorkingDir.PathDisplay.Length] != resultFile.PathFormat.Separator)
            {
                var workFile = context.GetNewWorkFile(context.Extension);
                workFile.ParentDirectory.Create();
                await resultFile.CopyToAsync(workFile, cancellationToken).ConfigureAwait(false);

                resultFile = workFile;
                cancellationToken.ThrowIfCancellationRequested();
            }

            IAbsoluteFilePath indeterminateMarker = null;

            if (variantId is null)
            {
                // Create an indeterminate marker to indicate that the file is not yet committed.
                indeterminateMarker = GetIndeterminateMarker(fileId);

                const string message = "A transaction has tentatively added this file.";
                await LogToMarkerAsync(indeterminateMarker, "TRANSACTION PENDING ADD", message, markerRequired: true).ConfigureAwait(false);

                Debug.Assert(dataFileDir.State is EntryState.ParentExists or EntryState.ParentDoesNotExist, "Data file group dir is in an unexpected state.");
                dataFileDir.Create();
            }
            else
            {
                Debug.Assert(dataFile.State is EntryState.ParentExists, "Data file was in an unexpected state.");
            }

            try
            {
                resultFile.MoveTo(dataFile, overwrite: false);
            }
            catch (DirectoryNotFoundException) when (variantId is not null)
            {
                // If the file directory does not exist (but its parent does) then it means that a race occurred and the file dir was deleted while
                // we were processing the variant. In that case we act as though adding the variant was successful and the delete happened
                // afterwards by returning success.

                if (dataFileDir.State is not EntryState.ParentExists)
                    throw;
            }
            catch when (variantId is null && indeterminateMarker is not null)
            {
                // Attempt to clean up the failed add operation before we throw.

                if (!dataFileDir.TryDelete(out var ex) || !indeterminateMarker.TryDelete(out ex))
                {
                    // If cleanup fails, log to the indeterminate marker file. Leave the file as indeterminate to be on the safe side instead of marking for
                    // deletion in case something went horribly wrong and a conflict occurred somehow, just let indeterminate resolution handle it later.
                    await LogToMarkerAsync(indeterminateMarker, "ABORTED ADD CLEANUP FAILED", ex, markerRequired: false).ConfigureAwait(false);
                }

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
