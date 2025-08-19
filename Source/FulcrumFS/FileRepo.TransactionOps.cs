namespace FulcrumFS;

/// <content>
/// Contains the implementations of transaction-related functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Begins a new transaction for the file repository, allowing changes to be committed or rolled back.
    /// </summary>
    public async ValueTask<FileRepoTransaction> BeginTransactionAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return new FileRepoTransaction(this);
    }

    internal async Task<AddFileResult> TxnAddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        extension = FileExtension.Normalize(extension);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        FileId fileId;

        while (true)
        {
            fileId = FileId.CreateSequential();

            using (await _fileSync.LockAsync((fileId, null), cancellationToken).ConfigureAwait(false))
            {
                var fileDir = GetFileDirectory(fileId);
                var fileDirState = fileDir.State;

                var deleteMarker = GetDeleteMarker(fileId, null);
                var deleteMarkerState = deleteMarker.State;

                if (fileDirState is EntryState.Exists || deleteMarkerState is EntryState.Exists)
                    continue;

                if (deleteMarkerState is not EntryState.ParentExists)
                    throw new IOException("An error occurred while accessing the repository: Cleanup directory does not exist.");

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
            var result = await AddCommonAsyncCore(fileId, null, stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
            return new(fileId, result);
        }
        finally
        {
            lock (_processingFileIds)
                _processingFileIds.Remove(fileId);
        }
    }

    internal async Task TxnDeleteAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
        {
            lock (_processingFileIds)
            {
                if (_processingFileIds.Contains(fileId))
                    throw new InvalidOperationException($"File ID '{fileId}' is currently being processed and cannot be deleted.");
            }

            var fileDir = GetFileDirectory(fileId);
            var deleteMarker = GetDeleteMarker(fileId, null);

            switch (fileDir.State, deleteMarker.State)
            {
                case (EntryState.ParentExists, _) or (_, EntryState.Exists):
                    throw new RepoFileNotFoundException($"File ID '{fileId}' could not be found in the repository or has already been marked for deletion.");
                case (EntryState.ParentDoesNotExist, _) or (_, EntryState.ParentDoesNotExist):
                    throw new IOException("Repository directories are not accessible.");
            }

            // Write an indeterminate marker file to mark the file as potentially deleted.

            var indeterminateMarker = GetIndeterminateMarker(fileId);
            const string message = "A transaction tentatively has marked this file for deletion.";
            await LogToMarkerAsync(indeterminateMarker, "TRANSACTION PENDING DELETE", message, markerRequired: true).ConfigureAwait(false);
        }
    }

    internal async Task TxnCommitAddAsync(FileId fileId) =>
        await DeleteIndeterminateMarkerAsync(fileId).ConfigureAwait(false);

    internal async Task TxnCommitDeleteAsync(FileId fileId) =>
        await DeleteAsync(fileId, immediateDelete: false).ConfigureAwait(false);

    internal async Task TxnRollbackAddAsync(FileId fileId) =>
        await DeleteAsync(fileId, immediateDelete: true).ConfigureAwait(false);

    internal async Task TxnRollbackDeleteAsync(FileId fileId) =>
        await DeleteIndeterminateMarkerAsync(fileId).ConfigureAwait(false);

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
}
