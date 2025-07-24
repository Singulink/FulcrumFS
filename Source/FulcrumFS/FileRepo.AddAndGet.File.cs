namespace FulcrumFS;

/// <content>
/// Contains the implementations of file add functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Opens a file stream for an existing file in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async Task<Stream> OpenAsync(FileId fileId)
    {
        var file = await GetAsync(fileId).ConfigureAwait(false);
        return file.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    /// <summary>
    /// Gets an existing file from the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async Task<IAbsoluteFilePath> GetAsync(FileId fileId)
    {
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);
        return FindDataFile(fileId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
    }

    private async Task<(FileId FileId, IAbsoluteFilePath File)> AddAsyncImpl(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken)
    {
        extension = NormalizeExtension(extension);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

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
