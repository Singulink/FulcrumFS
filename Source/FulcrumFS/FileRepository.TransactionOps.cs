using System.Linq.Expressions;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of methods that delete files from the repository.
/// </content>
partial class FileRepository
{
    internal async Task<AddFileResult> TxnAddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        var result = await AddAsyncImpl(stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
        return new AddFileResult(result.FileId, result.File);
    }

    internal async Task TxnDeleteAsync(Guid fileId)
    {
        ValidateFileId(fileId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        string fileIdString = GetFileIdString(fileId);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
        {
            lock (_processingFileIds)
            {
                if (_processingFileIds.Contains(fileId))
                    throw new InvalidOperationException($"File ID '{fileId}' is currently being processed and cannot be deleted.");
            }

            var dataFileDir = GetDataFileDirectory(fileIdString);

            if (!dataFileDir.Exists)
                throw new RepositoryFileNotFoundException($"File ID '{fileId}' does not exist in the repository.");

            var deleteFile = GetCleanupDeleteFile(fileIdString, null);

            if (deleteFile.Exists)
                throw new RepositoryFileNotFoundException($"File ID '{fileId}' is already marked for deletion.");

            // Write an indeterminate file to indicate that the file is being deleted.
            // File may exist if it was added in this transaction, or it may not exist if it was added in a previous transaction.

            var indeterminateFile = GetCleanupIndeterminateFile(fileIdString);
            const string message = "A transaction has tentatively marked this file for deletion.";

            await WriteCleanupRecordAsync(indeterminateFile, "TX PENDING DELETE", message, ignoreErrors: false).ConfigureAwait(false);
        }
    }

    internal async Task TxnCommitAddAsync(Guid fileId) =>
        await DeleteIndeterminateCleanupFileAsync(fileId).ConfigureAwait(false);

    internal async Task TxnCommitDeleteAsync(Guid fileId, bool addedInCurrentTransaction) =>
        await DeleteDataFileDirAsync(fileId, forceTryImmediateDelete: addedInCurrentTransaction).ConfigureAwait(false);

    internal async Task TxnRollbackAddAsync(Guid fileId) =>
        await DeleteDataFileDirAsync(fileId, forceTryImmediateDelete: true).ConfigureAwait(false);

    internal async Task TxnRollbackDeleteAsync(Guid fileId) =>
        await DeleteIndeterminateCleanupFileAsync(fileId).ConfigureAwait(false);

    internal async Task OnTxnCommitErrorAsync(Exception ex)
    {
        if (CommitFailed is { } handler)
            await handler.Invoke(ex).ConfigureAwait(false);
    }

    internal async Task OnTxnRollbackErrorAsync(Exception ex)
    {
        if (RollbackFailed is { } handler)
            await handler.Invoke(ex).ConfigureAwait(false);
    }

    private async Task DeleteDataFileDirAsync(Guid fileId, bool forceTryImmediateDelete)
    {
        ValidateFileId(fileId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        string fileIdString = GetFileIdString(fileId);
        var deleteFile = GetCleanupDeleteFile(fileIdString, null);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
        {
            Exception ex = null;

            // Try to delete the file data dir immediately if it was added in the same transaction or if the delete delay is zero

            if (forceTryImmediateDelete || DeleteFileDelay <= TimeSpan.Zero)
            {
                var dataFileDir = GetDataFileDirectory(fileIdString);
                var indeterminateFile = GetCleanupIndeterminateFile(fileIdString);

                if (dataFileDir.TryDelete(recursive: true, out ex) && indeterminateFile.TryDelete(out ex) && deleteFile.TryDelete(out ex))
                    return;
            }

            // Either delete did not succeed or there is a delete delay set, so try to write a delete cleanup record.
            // If this fails then the file will stay in indeterminate state.

            if (ex is not null)
                await WriteCleanupRecordAsync(deleteFile, fileIdString, ex.ToString(), ignoreErrors: true).ConfigureAwait(false);
        }
    }

    private async Task DeleteIndeterminateCleanupFileAsync(Guid fileId)
    {
        ValidateFileId(fileId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        string fileIdString = GetFileIdString(fileId);
        var indeterminateFile = GetCleanupIndeterminateFile(fileIdString);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
        {
            // Delete the indeterminate file

            try
            {
                indeterminateFile.Delete(ignoreNotFound: false);
            }
            catch (IOException ex) when (ex.GetType() != typeof(FileNotFoundException))
            {
                // If we can't delete the indeterminate file, log the error but continue.
                // File will stay in indeterminate state.

                await WriteCleanupRecordAsync(indeterminateFile, "REMOVE INDETERMINATE FAILED", ex.ToString(), ignoreErrors: true).ConfigureAwait(false);
            }
        }
    }
}
