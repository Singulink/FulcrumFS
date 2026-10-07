namespace FulcrumFS;

/// <content>
/// Contains the implementations of transaction-related functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// The number of deletions from which the deferred commit of an ordinary transaction records them in one committed delete journal instead of a delete
    /// marker per file. A single deletion keeps its marker: with marker logging off a marker is an empty file, while a journal listing one file still needs a
    /// data block.
    /// </summary>
    internal const int JournalCommitMinimumDeletions = 2;

    /// <summary>
    /// Begins a new transaction for the file repository, allowing changes to be committed or rolled back.
    /// </summary>
    /// <exception cref="InvalidOperationException">The repository was opened read-only.</exception>
    public async ValueTask<FileRepoTransaction> BeginTransactionAsync()
    {
        ThrowIfReadOnly();
        await EnsureInitializedAsync().ConfigureAwait(false);
        return new FileRepoTransaction(this);
    }

    /// <summary>
    /// Begins a new staged transaction for the file repository: a transaction that records all of its deletions in a single journal file, for application
    /// transactions that delete many files. It is committed in two calls, <see cref="FileRepoStagedTransaction.StageAsync(CancellationToken)"/> before the
    /// application commits its database and <see cref="FileRepoStage.CommitAsync(CancellationToken)"/> after.
    /// </summary>
    /// <remarks>
    /// See <see cref="FileRepoStagedTransaction"/> for when to use a staged transaction instead of one started with <see cref="BeginTransactionAsync"/>, and
    /// for how it records its deletions on a version 1 repository, which has no journals.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The repository was opened read-only.</exception>
    public async ValueTask<FileRepoStagedTransaction> BeginStagedTransactionAsync()
    {
        ThrowIfReadOnly();
        await EnsureInitializedAsync().ConfigureAwait(false);
        return new FileRepoStagedTransaction(this);
    }

    internal async Task<(RepoFileGroupInfo Result, IAsyncDisposable IndeterminateMarker)> TxnAddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        Func<ProgressValue, ValueTask>? progressCallback,
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
                fileId = Options.FileIdMode is FileIdMode.Secure ? FileId.CreateSecure() : _idGenerator.CreateSequential();

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

            return await AddTransactionalAsyncCore(fileId, stream, extension, leaveOpen, pipeline, progressCallback, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!fileLock.IsDefault)
                fileLock.Dispose();
        }
    }

    /// <summary>
    /// Marks a file as tentatively deleted by a transaction and returns the shared handle on its indeterminate marker. When the file's group does not exist,
    /// throws <see cref="RepoFileNotFoundException"/> if <paramref name="throwIfNotFound"/> is <see langword="true"/>; otherwise returns
    /// <see langword="null"/> without creating a marker. A file whose deletion another transaction has committed is still deleted again (see
    /// <see cref="TxnCheckDeleteAsync(FileId, bool)"/>).
    /// </summary>
    internal async Task<IAsyncDisposable?> TxnDeleteAsync(FileId fileId, bool throwIfNotFound)
    {
        // No _fileSync lock is needed here. A caller can only possess a FileId after TxnAddAsync has returned, which means BatchMoveTransactionalAsync has
        // already moved the file into _filesDirectory and the in-process add reservation lock has been released. Concurrent TxnDeleteAsync calls on the
        // same id are supported by design via the shared indeterminate marker (multiple holders, FileShare allowed).

        if (!await TxnCheckDeleteAsync(fileId, throwIfNotFound).ConfigureAwait(false))
            return null;

        return await TxnOpenDeleteMarkerAsync(fileId).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a file as tentatively deleted by a transaction without checking that it exists, and returns the shared handle on its indeterminate marker. Used
    /// by <see cref="TxnDeleteAsync(FileId, bool)"/> once the file has been checked, and by a staged transaction on a repository without delete journals for
    /// the deletions it checked when it recorded them.
    /// </summary>
    internal async Task<IAsyncDisposable> TxnOpenDeleteMarkerAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        // Write an indeterminate marker to mark the file as potentially deleted and keep it open with a non-exclusive (shared) handle. Multiple
        // concurrent transactions are allowed to share the marker, but cleanup operations probing it with an exclusive (FileShare.None) handle will be
        // blocked while any transaction is still holding it.
        //
        // The marker is flushed to disk before returning: once the application commits its deletion, a power loss before the repository commit would
        // otherwise lose the only record that the file should go.

        var indeterminateMarker = _fs.GetIndeterminateMarker(fileId);
        const string message = "A transaction tentatively has marked this file for deletion.";
        return await _fs.OpenOrCreateSharedMarkerAsync(indeterminateMarker, "TRANSACTION PENDING DELETE", message, flushToDisk: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that a transaction can delete a file: its file group exists. Writes nothing. When the group does not exist, throws
    /// <see cref="RepoFileNotFoundException"/> if <paramref name="throwIfNotFound"/> is <see langword="true"/>; otherwise returns <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The records of earlier deletions are not consulted, neither the file's delete marker nor delete journals, so a file whose deletion another transaction
    /// has committed and that a cleanup pass has not removed yet can be deleted again. The deletions converge in every delete mode: a deferred commit records
    /// the deletion again beside the earlier record (in a delete marker, appending to one that is already there, or in a journal of its own), an immediate
    /// commit deletes the group and any delete marker, and the cleaner counts a group that is already gone as deleted.
    /// </remarks>
    internal async ValueTask<bool> TxnCheckDeleteAsync(FileId fileId, bool throwIfNotFound)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        // The deletion is recorded in the cleanup directory, so without it the repository is inaccessible rather than the file missing. A missing shard
        // directory only means no file was ever stored under that shard, so it is "not found" unless the files directory itself is gone.
        if (_fs.CleanupDirectory.State is not EntryState.Exists)
            throw new IOException("Repository directories are not accessible.");

        switch (_fs.GetFileDirectory(fileId).State)
        {
            case EntryState.ParentDoesNotExist when _fs.FilesDirectory.State is not EntryState.Exists:
                throw new IOException("Repository directories are not accessible.");
            case EntryState.ParentExists or EntryState.ParentDoesNotExist:
                if (throwIfNotFound)
                    throw new RepoFileNotFoundException($"File ID '{fileId}' was not found in the repository.");

                return false;
        }

        return true;
    }

    /// <summary>
    /// Records the deletions of a staged transaction durably and returns the record, which holds them open until the transaction has finished: a new staged
    /// delete journal, flushed to disk, on a repository whose format has delete journals; otherwise the files' indeterminate markers, each flushed to disk.
    /// </summary>
    internal async Task<StagedDeleteRecord> TxnStageDeletesAsync(IReadOnlyCollection<FileId> fileIds)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        // A version 1 repository must not contain journals: a FulcrumFS 1.0.x cleaner may run against it, and it would leave the files of a committed journal
        // undeleted. The decision stays valid until the record is finished because per-file markers are valid in every version and nothing downgrades a
        // repository.
        if (_formatVersion < RepoInfoFile.DeleteJournalsVersion)
            return await StagedDeleteRecord.CreateMarkersAsync(this, fileIds).ConfigureAwait(false);

        // Journal IDs come from the repository's sequential generator in every file ID mode, so journals sort by the time they were staged.
        var journal = await _fs.CreateStagedJournalAsync(_idGenerator.CreateSequential().Guid, fileIds).ConfigureAwait(false);
        return StagedDeleteRecord.FromJournal(this, journal, fileIds);
    }

    /// <summary>
    /// Physically deletes a file listed in a committed delete journal, in immediate delete mode. Returns <see langword="false"/> if the deletion could not
    /// complete; the failure is logged to the file's delete marker and the journal must be kept as the record of the deletion.
    /// </summary>
    internal ValueTask<bool> TxnCommitStagedDeleteAsync(FileId fileId) => DeleteAsync(fileId, immediate: true);

    internal async ValueTask TxnCommitAddAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
            await _fs.TryClearIndeterminateStateAsync(fileId).ConfigureAwait(false);
    }

    internal async ValueTask TxnCommitDeleteAsync(FileId fileId) =>
        await DeleteAsync(fileId, immediate: Options.DeleteMode is DeleteMode.Immediate).ConfigureAwait(false);

    /// <summary>
    /// Records the deletions an ordinary transaction is committing in one committed delete journal, flushed to disk, and returns <see langword="true"/>; or
    /// returns <see langword="false"/> without writing anything when each deletion is committed with <see cref="TxnCommitDeleteAsync(FileId)"/> instead: in
    /// immediate delete mode, on a repository whose format has no delete journals, and for fewer than <see cref="JournalCommitMinimumDeletions"/> deletions.
    /// A failure to write the journal is thrown; the deletions must then be committed one by one so that they are still recorded. Once the journal is
    /// written, each deletion is finished with <see cref="TxnFinishJournaledDeleteAsync(FileId)"/>.
    /// </summary>
    internal async ValueTask<bool> TxnCommitDeletesToJournalAsync(IReadOnlyCollection<FileId> fileIds)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        // Both deferred modes defer main files, which is all a transaction deletes. A version 1 repository must not contain journals (see
        // TxnStageDeletesAsync).
        if (Options.DeleteMode is DeleteMode.Immediate || _formatVersion < RepoInfoFile.DeleteJournalsVersion || fileIds.Count < JournalCommitMinimumDeletions)
            return false;

        // Journal IDs come from the repository's sequential generator in every file ID mode, so journals sort by the time they were written.
        await _fs.CreateCommittedJournalAsync(_idGenerator.CreateSequential().Guid, fileIds).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Finishes the deletion of a file that <see cref="TxnCommitDeletesToJournalAsync(IReadOnlyCollection{FileId})"/> recorded in a committed journal by
    /// removing its indeterminate marker, without flushing: a power loss can only bring the marker back together with the journal, which deletes the file,
    /// and the cleaner removes the marker of a file whose group is gone.
    /// </summary>
    internal async ValueTask TxnFinishJournaledDeleteAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using (await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false))
            await _fs.TryClearIndeterminateStateAsync(fileId).ConfigureAwait(false);
    }

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
