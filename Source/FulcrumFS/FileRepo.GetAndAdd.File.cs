using System.Diagnostics;
using FulcrumFS.Utilities;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of methods that get or add files to the repository.
/// </content>
partial class FileRepo
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
                dataFileDirectory = GetDataFileGroupDirectory(fileIdString);
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
}
