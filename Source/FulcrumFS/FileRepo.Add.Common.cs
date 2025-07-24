using System.Diagnostics;

namespace FulcrumFS;

/// <content>
/// Contains the implementations of common file and variant add functionality for the file repository.
/// </content>
partial class FileRepo
{
    private async Task<IAbsoluteFilePath> AddAsyncCore(
        FileId fileId,
        string? variantId,
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var tempWorkingDir = _tempDirectory.CombineDirectory(GetEntryName(fileId, variantId), PathOptions.None);
        FileProcessContext context = null;

        try
        {
            context = new FileProcessContext(fileId, variantId, tempWorkingDir, stream, extension, leaveOpen, cancellationToken);
            await pipeline.ExecuteAsync(context).ConfigureAwait(false);

            var resultFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
            var dataFile = GetDataFile(fileId, context.Extension, variantId);
            var dataFileGroupDir = dataFile.ParentDirectory;

            // If the result file not in the temp directory then we need to copy it there first.

            if (!resultFile.PathDisplay.StartsWith(tempWorkingDir.PathDisplay, StringComparison.Ordinal))
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

                Debug.Assert(dataFileGroupDir.State is EntryState.ParentExists or EntryState.ParentDoesNotExist, "Data file group dir is in an unexpected state.");
                dataFileGroupDir.Create();
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
                // If the data file group directory does not exist (but its parent does) then it means that a race occurred and the file group was deleted while
                // we were processing the variant. We can keep thing simple and act as though adding the variant was successful and the delete happened
                // afterwards by returning success. There is no need to throw an exception or report an error here.

                if (dataFileGroupDir.State is not EntryState.ParentExists)
                    throw;
            }
            catch when (variantId is null && indeterminateMarker is not null)
            {
                if (dataFileGroupDir.TryDelete(out var ex))
                    indeterminateMarker.TryDelete(out ex);

                if (ex is not null)
                    await LogToMarkerAsync(indeterminateMarker, "ABORTED ADD CLEANUP FAILED", ex, markerRequired: false).ConfigureAwait(false);

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
