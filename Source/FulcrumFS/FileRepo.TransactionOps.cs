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

    internal async Task<(RepoFileInfo Result, IAsyncDisposable IndeterminateMarker)> TxnAddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessingPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        extension = FileExtension.Normalize(extension);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        FileId fileId;

        while (true)
        {
            fileId = Options.FileIdMode is FileIdMode.Secure ? FileId.CreateSecure() : FileId.CreateSequential();

            using (await _fileSync.LockAsync((fileId, null), cancellationToken).ConfigureAwait(false))
            {
                var fileDir = GetFileDirectory(_filesDirectory, fileId);
                var fileDirState = fileDir.State;

                var deleteMarker = GetDeleteMarker(_cleanupDirectory, fileId, null);
                var deleteMarkerState = deleteMarker.State;

                if (fileDirState is EntryState.Exists || deleteMarkerState is EntryState.Exists)
                    continue;

                if (deleteMarkerState is not EntryState.ParentExists)
                    throw new IOException("An error occurred while accessing the repository: Cleanup directory does not exist.");

                lock (_processingFiles)
                {
                    if (!_processingFiles.Add(fileId))
                        continue;
                }

                break;
            }
        }

        try
        {
            var (dataFile, indeterminateMarkerHandle) = await AddAsyncCore(
                fileId, stream, extension, sourceInRepo: false, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);

            return (new RepoFileInfo(fileId, variantId: null, dataFile), indeterminateMarkerHandle);
        }
        finally
        {
            lock (_processingFiles)
                _processingFiles.Remove(fileId);
        }
    }

    internal async Task<IAsyncDisposable> TxnDeleteAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
        {
            lock (_processingFiles)
            {
                if (_processingFiles.Contains(fileId))
                    throw new InvalidOperationException($"File ID '{fileId}' is currently being processed and cannot be deleted.");
            }

            var fileDir = GetFileDirectory(_filesDirectory, fileId);
            var deleteMarker = GetDeleteMarker(_cleanupDirectory, fileId, null);

            switch (fileDir.State, deleteMarker.State)
            {
                case (EntryState.ParentExists, _) or (_, EntryState.Exists):
                    throw new RepoFileNotFoundException($"File ID '{fileId}' could not be found in the repository or has already been marked for deletion.");
                case (EntryState.ParentDoesNotExist, _) or (_, EntryState.ParentDoesNotExist):
                    throw new IOException("Repository directories are not accessible.");
            }

            // Write an indeterminate marker to mark the file as potentially deleted and keep it open with a non-exclusive (shared) handle. Multiple
            // concurrent transactions are allowed to share the marker, but cleanup operations probing it with an exclusive (FileShare.None) handle will be
            // blocked while any transaction is still holding it.

            var indeterminateMarker = GetIndeterminateMarker(_cleanupDirectory, fileId);
            const string message = "A transaction tentatively has marked this file for deletion.";
            return await OpenOrCreateSharedMarkerAsync(indeterminateMarker, "TRANSACTION PENDING DELETE", message, Options.MarkerFileLogging).ConfigureAwait(false);
        }
    }

    internal async ValueTask TxnCommitAddAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
            await ClearIndeterminateStateAsync(_cleanupDirectory, fileId, Options.MarkerFileLogging).ConfigureAwait(false);
    }

    internal async ValueTask TxnCommitDeleteAsync(FileId fileId) =>
        await DeleteAsync(fileId, immediateDelete: Options.DeleteMode is DeleteMode.Immediate).ConfigureAwait(false);

    internal async ValueTask TxnRollbackAddAsync(FileId fileId) =>
        await DeleteAsync(fileId, immediateDelete: true).ConfigureAwait(false);

    internal async ValueTask TxnRollbackDeleteAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
            await ClearIndeterminateStateAsync(_cleanupDirectory, fileId, Options.MarkerFileLogging).ConfigureAwait(false);
    }

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
