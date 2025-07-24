namespace FulcrumFS;

/// <content>
/// Contains the implementations of file add functionality for the file repository.
/// </content>
partial class FileRepo
{
    private async Task<(FileId FileId, IAbsoluteFilePath File)> AddAsyncImpl(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        extension = NormalizeExtension(extension);

        FileId fileId;

        while (true)
        {
            fileId = FileId.CreateRandom();

            using (await _fileSync.LockAsync((fileId, null), cancellationToken).ConfigureAwait(false))
            {
                var dataFileGroupDir = GetDataFileGroupDirectory(fileId);
                var dataFileGroupDirState = dataFileGroupDir.State;

                var deleteMarker = GetDeleteMarker(fileId, null);
                var deleteMarkerState = deleteMarker.State;

                if (dataFileGroupDirState is EntryState.Exists || deleteMarkerState is EntryState.Exists)
                    continue;

                if (deleteMarkerState is not EntryState.ParentExists)
                    throw new IOException("An error occurred while accessing the repository (cleanup directory does not exist).");

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
            var result = await AddAsyncCore(fileId, null, stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
            return (fileId, result);
        }
        finally
        {
            lock (_processingFileIds)
                _processingFileIds.Remove(fileId);
        }
    }
}
