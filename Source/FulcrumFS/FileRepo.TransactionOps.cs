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

    internal async Task<(RepoFileGroupInfo Result, IAsyncDisposable IndeterminateMarker)> TxnAddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        extension = FileExtension.Normalize(extension);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        KeyLock<(FileId FileId, string? VariantId)> fileLock = default;

        try
        {
            FileId fileId;

            while (true)
            {
                fileId = Options.FileIdMode is FileIdMode.Secure ? FileId.CreateSecure() : FileId.CreateSequential();

                // Try to acquire the per-id lock immediately. If another in-flight TxnAddAsync already owns this id (an astronomically rare collision in
                // Secure mode, impossible-within-session in Sequential mode), mint a new id and retry rather than waiting. The lock is held for the entire
                // duration of AddTransactionalAsyncCore so that this in-process reservation is honored end-to-end - critical as a defense-in-depth guard
                // against ever processing two unrelated files into the same file ID.
                try
                {
                    fileLock = await _fileSync.LockAsync((fileId, null), TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                var fileDir = _fs.GetFileDirectory(fileId);
                var fileDirState = fileDir.State;

                var deleteMarker = _fs.GetDeleteMarker(fileId, null);
                var deleteMarkerState = deleteMarker.State;

                if (fileDirState is EntryState.Exists || deleteMarkerState is EntryState.Exists)
                {
                    fileLock.Dispose();
                    continue;
                }

                if (deleteMarkerState is not EntryState.ParentExists)
                    throw new IOException("An error occurred while accessing the repository: Cleanup directory does not exist.");

                break;
            }

            return await AddTransactionalAsyncCore(fileId, stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!fileLock.IsDefault)
                fileLock.Dispose();
        }
    }

    internal async Task<IAsyncDisposable> TxnDeleteAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        // No _fileSync lock is needed here. A caller can only possess a FileId after TxnAddAsync has returned, which means BatchMoveTransactionalAsync has
        // already moved the file into _filesDirectory and the in-process add reservation lock has been released. Concurrent TxnDeleteAsync calls on the
        // same id are supported by design via the shared indeterminate marker (multiple holders, FileShare allowed).

        var fileDir = _fs.GetFileDirectory(fileId);
        var deleteMarker = _fs.GetDeleteMarker(fileId, null);

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

        var indeterminateMarker = _fs.GetIndeterminateMarker(fileId);
        const string message = "A transaction tentatively has marked this file for deletion.";
        return await _fs.OpenOrCreateSharedMarkerAsync(indeterminateMarker, "TRANSACTION PENDING DELETE", message).ConfigureAwait(false);
    }

    internal async ValueTask TxnCommitAddAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
            await _fs.TryClearIndeterminateStateAsync(fileId).ConfigureAwait(false);
    }

    internal async ValueTask TxnCommitDeleteAsync(FileId fileId) =>
        await DeleteAsync(fileId, immediate: Options.DeleteMode is DeleteMode.Immediate).ConfigureAwait(false);

    internal async ValueTask TxnRollbackAddAsync(FileId fileId) =>
        await DeleteAsync(fileId, immediate: true).ConfigureAwait(false);

    internal async ValueTask TxnRollbackDeleteAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
            await _fs.TryClearIndeterminateStateAsync(fileId).ConfigureAwait(false);
    }

    internal async Task OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation operation, Exception ex)
    {
        if (TransactionCompletionFailed is not { } handler)
            return;

        var failure = new RepoTransactionCompletionFailureInfo(operation, ex);

        if (handler.HasSingleTarget)
        {
            await handler.Invoke(failure).ConfigureAwait(false);
            return;
        }

        foreach (var singleHandler in handler.GetInvocationList().Cast<RepoTransactionCompletionFailedHandler>())
            await singleHandler.Invoke(failure).ConfigureAwait(false);
    }
}
