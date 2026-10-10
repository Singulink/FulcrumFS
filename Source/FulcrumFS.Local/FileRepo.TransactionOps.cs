using System.Diagnostics;

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
    /// How long the deletion of a file waits for the file's indeterminate marker while the marker is in use, which normally means that the transaction that
    /// added the file has not finished, before it gives up with a <see cref="RepoFileBusyException"/>.
    /// </summary>
    /// <remarks>
    /// The condition clears itself, normally within milliseconds: another transaction learns of the file when the application of the adding one has
    /// committed its database, and the repository commit that releases the marker follows at once. So the deletion waits instead of failing, with a delay
    /// that starts at <see cref="MarkerInUseFirstRetryDelay"/> and doubles (10, 20, 40, 80 and 160 ms, then what remains of the 500 ms): an attempt costs
    /// one failed open, and the wait is normally over after the first one or two. The wait is short because it cannot always end: a caller that deletes,
    /// from a second transaction, a file that its own open transaction added waits for itself, and should hear so soon. The length of the wait is also
    /// stated in the documentation of <see cref="FileRepoTransaction.DeleteAsync(FileId, CancellationToken)"/> and in the article on deleting files, which a
    /// change of it has to follow.
    /// </remarks>
    internal static readonly TimeSpan MarkerInUseMaxWait = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The delay before the second attempt to open an indeterminate marker that is in use; it doubles with every further attempt. See
    /// <see cref="MarkerInUseMaxWait"/>.
    /// </summary>
    internal static readonly TimeSpan MarkerInUseFirstRetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The number of deletions that transactions have pending through each file's indeterminate marker, for the files that have any: the handles returned by
    /// <see cref="TxnDeleteAsync(FileId, bool, CancellationToken)"/> and <see cref="TxnOpenDeleteMarkerAsync(FileId, CancellationToken)"/> that have not been
    /// released yet.
    /// </summary>
    /// <remarks>
    /// Between the database commit of a transaction's application and its repository commit, the marker is the only durable record of the deletion, and
    /// every transaction that deletes the file has its own handle on the one marker. The count is what keeps the rollback of one of those deletions from
    /// removing the marker that the others still rely on. Only this instance has transactions on the repository, because it holds the repository lock, so
    /// counting them in memory is enough. A deletion is counted while the file's lock is held, and the rollback checks the count and removes the marker under
    /// the same lock; <see cref="_pendingDeletesSync"/> only guards the dictionary, which the locks of different files do not.
    /// </remarks>
    private readonly Dictionary<FileId, int> _pendingDeletes = [];
    private readonly Lock _pendingDeletesSync = new();

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
    /// Marks a file as tentatively deleted by a transaction and returns the transaction's shared handle on its indeterminate marker, which is counted as a
    /// pending deletion of the file until it is released. When the file's group does not exist, throws <see cref="RepoFileNotFoundException"/> if
    /// <paramref name="throwIfNotFound"/> is <see langword="true"/>; otherwise returns <see langword="null"/> without creating a marker. A file whose
    /// deletion another transaction has committed is still deleted again (see <see cref="TxnCheckDeleteAsync(FileId, bool)"/>).
    /// </summary>
    /// <remarks>
    /// While the marker is in use, the call waits as described on <see cref="MarkerInUseMaxWait"/>, and the file is checked again before every attempt. The
    /// call therefore behaves as if it had been made when the marker was released: it finds a file whose add has committed in the meantime, and no file if
    /// the add was rolled back.
    /// </remarks>
    /// <exception cref="RepoFileBusyException">The marker was still in use when the wait was over. Nothing has been recorded.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the call waited for the marker. Nothing has been recorded.</exception>
    internal Task<IAsyncDisposable?> TxnDeleteAsync(FileId fileId, bool throwIfNotFound, CancellationToken cancellationToken) =>
        OpenDeleteMarkerAsync(fileId, checkFile: true, throwIfNotFound, cancellationToken);

    /// <summary>
    /// Marks a file as tentatively deleted by a transaction without checking that it exists, and returns the transaction's shared handle on its indeterminate
    /// marker, which is counted as a pending deletion of the file until it is released. Used by a staged transaction on a repository without delete journals
    /// for the deletions it checked when it recorded them. While the marker is in use, the call waits as described on <see cref="MarkerInUseMaxWait"/>.
    /// </summary>
    /// <exception cref="RepoFileBusyException">The marker was still in use when the wait was over. Nothing has been recorded.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the call waited for the marker. Nothing has been recorded.</exception>
    internal async Task<IAsyncDisposable> TxnOpenDeleteMarkerAsync(FileId fileId, CancellationToken cancellationToken)
    {
        var marker = await OpenDeleteMarkerAsync(fileId, checkFile: false, throwIfNotFound: false, cancellationToken).ConfigureAwait(false);
        Debug.Assert(marker is not null, "A marker is always opened for a file that is not checked.");

        return marker;
    }

    private async Task<IAsyncDisposable?> OpenDeleteMarkerAsync(FileId fileId, bool checkFile, bool throwIfNotFound, CancellationToken cancellationToken)
    {
        // The token only cancels the wait for a marker that is in use: a call that does not have to wait runs to its end whatever the token, as it did
        // when there was no wait.
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

        var indeterminateMarker = _fs.GetIndeterminateMarker(fileId);
        long waitStart = Stopwatch.GetTimestamp();
        var retryDelay = MarkerInUseFirstRetryDelay;

        while (true)
        {
            IOException inUse;

            // The file is checked, its marker opened and the deletion counted under the file's lock, which every transaction holds while it releases its
            // handle on the marker and removes the marker or the file. So the deletion never takes a marker that the commit of the file's add has released
            // and is about to remove, which would leave it pending without a record, and it never gets a marker for a file that the rollback of the add is
            // removing.
            using (await _fileSync.LockAsync((fileId, null), CancellationToken.None).ConfigureAwait(false))
            {
                if (checkFile && !CheckDelete(fileId, throwIfNotFound))
                    return null;

                try
                {
                    // The marker marks the file as potentially deleted and is kept open with a shared handle: any number of transactions can have a deletion
                    // of the file pending, each with a handle of its own, and a cleanup pass that probes the marker with an exclusive (FileShare.None) handle
                    // is kept out while any of them holds it. It is flushed to disk before returning: once the application commits its deletion, a power
                    // loss before the repository commit would otherwise lose the only record that the file should go.
                    const string message = "A transaction tentatively has marked this file for deletion.";

                    var handle = await _fs.OpenOrCreateSharedMarkerAsync(indeterminateMarker, "TRANSACTION PENDING DELETE", message, flushToDisk: true)
                        .ConfigureAwait(false);

                    return RegisterPendingDelete(fileId, handle);
                }
                catch (IOException ex) when (RepoFileSystem.IsInUse(ex))
                {
                    // A transaction that added the file and has not finished holds the marker exclusively, so the marker cannot be opened for as long as
                    // that transaction is open. That keeps the deletion out of a marker that the commit of the add removes. Anything else that has the
                    // marker open without sharing it, such as a cleanup pass that is probing it, looks the same from here.
                    inUse = ex;
                }
            }

            // The wait is made without the file's lock: the transaction that holds the marker needs the lock to release it. Nothing has been recorded, so the
            // wait can be cancelled. Task.Delay waits whole milliseconds, so a remainder below one would be attempts with no wait in between.
            var remaining = MarkerInUseMaxWait - Stopwatch.GetElapsedTime(waitStart);

            if (remaining < TimeSpan.FromMilliseconds(1))
            {
                throw new RepoFileBusyException(
                    $"The deletion of file ID '{fileId}' could not be recorded because the file's indeterminate marker was still in use after " +
                    $"{MarkerInUseMaxWait.TotalMilliseconds:0} ms, which normally means that the transaction that added the file has not committed or rolled " +
                    "back yet. Nothing was recorded, and the deletion can be made again.",
                    inUse);
            }

            DebugStepHook?.Invoke(DebugStep.DeleteMarkerInUse);

            await Task.Delay(retryDelay < remaining ? retryDelay : remaining, cancellationToken).ConfigureAwait(false);
            retryDelay *= 2;
        }
    }

    /// <summary>
    /// MUST BE CALLED WITH THE FILE'S LOCK HELD. Counts a handle on a file's indeterminate marker as a pending deletion of the file and returns the handle
    /// that takes it out of the count again when it is released.
    /// </summary>
    private PendingDeleteMarker RegisterPendingDelete(FileId fileId, IAsyncDisposable handle)
    {
        lock (_pendingDeletesSync)
            _pendingDeletes[fileId] = _pendingDeletes.GetValueOrDefault(fileId) + 1;

        return new PendingDeleteMarker(this, fileId, handle);
    }

    private void UnregisterPendingDelete(FileId fileId)
    {
        lock (_pendingDeletesSync)
        {
            int count = _pendingDeletes[fileId] - 1;

            if (count is 0)
                _pendingDeletes.Remove(fileId);
            else
                _pendingDeletes[fileId] = count;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if a transaction has a deletion of the file pending through the file's indeterminate marker. A deletion is only
    /// counted under the file's lock, so the caller must hold that lock for an answer of <see langword="false"/> to stay valid.
    /// </summary>
    private bool HasPendingDeletes(FileId fileId)
    {
        lock (_pendingDeletesSync)
            return _pendingDeletes.ContainsKey(fileId);
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
        return CheckDelete(fileId, throwIfNotFound);
    }

    private bool CheckDelete(FileId fileId, bool throwIfNotFound)
    {
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
    /// <param name="fileIds">The files whose deletions are recorded.</param>
    /// <param name="cancellationToken">A token that cancels the wait for an indeterminate marker that is in use (see <see cref="MarkerInUseMaxWait"/>), which
    /// only the record of markers has. A journal is written without observing it.</param>
    internal async Task<StagedDeleteRecord> TxnStageDeletesAsync(IReadOnlyCollection<FileId> fileIds, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

        // A version 1 repository must not contain journals: a FulcrumFS 1.0.x cleaner may run against it, and it would leave the files of a committed journal
        // undeleted. The decision stays valid until the record is finished because per-file markers are valid in every version and nothing downgrades a
        // repository.
        if (_formatVersion < RepoInfoFile.DeleteJournalsVersion)
            return await StagedDeleteRecord.CreateMarkersAsync(this, fileIds, cancellationToken).ConfigureAwait(false);

        // Journal IDs come from the repository's sequential generator in every file ID mode, so journals sort by the time they were staged.
        var journal = await _fs.CreateStagedJournalAsync(_idGenerator.CreateSequential().Guid, fileIds).ConfigureAwait(false);
        return StagedDeleteRecord.FromJournal(this, journal, fileIds);
    }

    /// <summary>
    /// Physically deletes a file listed in a committed delete journal, in immediate delete mode. Returns <see langword="false"/> if the deletion could not
    /// complete; the failure is logged to the file's delete marker and the journal must be kept as the record of the deletion.
    /// </summary>
    internal ValueTask<bool> TxnCommitStagedDeleteAsync(FileId fileId) => DeleteAsync(fileId, immediate: true);

    /// <summary>
    /// Commits the add of a file: releases the transaction's exclusive handle on the file's indeterminate marker and removes the marker, as one step under
    /// the file's lock (see <see cref="LockAndReleaseMarkerAsync(FileId, IAsyncDisposable?)"/>). A deletion of the file from another transaction therefore
    /// opens the marker before the release, which fails because the marker is in use, or after the removal, which gives it a marker of its own; it never
    /// takes the marker that is about to be removed.
    /// </summary>
    internal async ValueTask TxnCommitAddAsync(FileId fileId, IAsyncDisposable? indeterminateMarker)
    {
        using (await LockAndReleaseMarkerAsync(fileId, indeterminateMarker).ConfigureAwait(false))
        {
            DebugStepHook?.Invoke(DebugStep.AddCommitStarted);
            await _fs.TryClearIndeterminateStateAsync(fileId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Commits the deletion of a file according to the repository's <see cref="FileRepoOptions.DeleteMode"/>: releases the transaction's handle on the file's
    /// indeterminate marker, and records or carries out the deletion, which removes the marker, as one step under the file's lock.
    /// </summary>
    /// <remarks>
    /// Releasing the handle takes the deletion out of the count of the pending ones although nothing else records it yet. Under the lock, the rollback of
    /// another pending deletion of the file cannot come in between and remove the marker. The commit itself removes the marker whatever other deletions are
    /// pending: by then the file is gone, or its delete marker is flushed to disk, so what becomes of the others no longer matters.
    /// </remarks>
    internal async ValueTask TxnCommitDeleteAsync(FileId fileId, IAsyncDisposable? indeterminateMarker)
    {
        using (await LockAndReleaseMarkerAsync(fileId, indeterminateMarker).ConfigureAwait(false))
        {
            DebugStepHook?.Invoke(DebugStep.DeleteCommitStarted);
            await DeleteLockedAsync(fileId, immediate: Options.DeleteMode is DeleteMode.Immediate).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records the deletions an ordinary transaction is committing in one committed delete journal, flushed to disk, and returns <see langword="true"/>; or
    /// returns <see langword="false"/> without writing anything when each deletion is committed with
    /// <see cref="TxnCommitDeleteAsync(FileId, IAsyncDisposable?)"/> instead: in immediate delete mode, on a repository whose format has no delete journals,
    /// and for fewer than <see cref="JournalCommitMinimumDeletions"/> deletions. A failure to write the journal is thrown; the deletions must then be
    /// committed one by one so that they are still recorded. Once the journal is written, each deletion is finished with
    /// <see cref="TxnFinishJournaledDeleteAsync(FileId, IAsyncDisposable?)"/>.
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
    /// releasing the transaction's handle on its indeterminate marker and removing the marker, without flushing: a power loss can only bring the marker back
    /// together with the journal, which deletes the file, and the cleaner removes the marker of a file whose group is gone. The marker is removed whatever
    /// other deletions of the file are pending, because the journal has recorded the deletion.
    /// </summary>
    internal async ValueTask TxnFinishJournaledDeleteAsync(FileId fileId, IAsyncDisposable? indeterminateMarker)
    {
        using (await LockAndReleaseMarkerAsync(fileId, indeterminateMarker).ConfigureAwait(false))
            await _fs.TryClearIndeterminateStateAsync(fileId).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the add of a file: releases the transaction's exclusive handle on the file's indeterminate marker and deletes the file with its marker, as
    /// one step under the file's lock, so that a deletion of the file from another transaction finds the marker in use or the file gone.
    /// </summary>
    internal async ValueTask TxnRollbackAddAsync(FileId fileId, IAsyncDisposable? indeterminateMarker)
    {
        using (await LockAndReleaseMarkerAsync(fileId, indeterminateMarker).ConfigureAwait(false))
            await DeleteLockedAsync(fileId, immediate: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the deletion of a file: releases the transaction's handle on the file's indeterminate marker and removes the marker, unless another
    /// transaction still has a deletion of the file pending.
    /// </summary>
    internal async ValueTask TxnRollbackDeleteAsync(FileId fileId, IAsyncDisposable? indeterminateMarker)
    {
        using (await LockAndReleaseMarkerAsync(fileId, indeterminateMarker).ConfigureAwait(false))
        {
            // The marker is all that records the deletion of a transaction that has it pending. If the application of that transaction commits its database
            // and the process ends before the repository commit, the cleaner must find the marker to learn that the file is to go. So the marker stays until
            // the last pending deletion is rolled back, or until one is committed, which records the deletion durably before it removes the marker.
            if (!HasPendingDeletes(fileId))
                await _fs.TryClearIndeterminateStateAsync(fileId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the lock of a file and then releases a transaction's handle on the file's indeterminate marker, so that the release and what the caller does to
    /// the marker under the lock are one step for everything else that takes the lock: a deletion that opens the marker, and the rollback of another pending
    /// deletion, which removes the marker only if no deletion is pending.
    /// </summary>
    /// <param name="fileId">The file whose lock is taken.</param>
    /// <param name="indeterminateMarker">The transaction's handle on the file's indeterminate marker, or <see langword="null"/> if it no longer holds
    /// one.</param>
    /// <returns>The file's lock, which the caller releases.</returns>
    /// <remarks>
    /// The handle is released in every case. If the lock cannot be taken, because the repository has been disposed or cannot be accessed, the handle is
    /// released without it and the exception is propagated: the marker then stays on disk with nobody holding it, which leaves the file indeterminate for the
    /// cleaner to resolve.
    /// </remarks>
    private async ValueTask<KeyLock<(FileId FileId, string? VariantId)>> LockAndReleaseMarkerAsync(FileId fileId, IAsyncDisposable? indeterminateMarker)
    {
        KeyLock<(FileId FileId, string? VariantId)> fileLock = default;

        try
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            fileLock = await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false);

            if (indeterminateMarker is not null)
                await indeterminateMarker.DisposeAsync().ConfigureAwait(false);

            return fileLock;
        }
        catch
        {
            if (!fileLock.IsDefault)
                fileLock.Dispose();

            if (indeterminateMarker is not null)
            {
                try { await indeterminateMarker.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }

            throw;
        }
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

    /// <summary>
    /// A transaction's shared handle on the indeterminate marker of a file it has tentatively deleted, counted in <see cref="_pendingDeletes"/> from the
    /// moment it is opened until it is disposed. Disposing it is what takes the deletion out of the count, so the count is right whichever way a transaction
    /// lets go of the handle: by finishing the deletion, or by releasing what it still holds when it is disposed without having finished.
    /// </summary>
    private sealed class PendingDeleteMarker(FileRepo repository, FileId fileId, IAsyncDisposable handle) : IAsyncDisposable
    {
        private IAsyncDisposable? _handle = handle;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _handle, null) is not { } handle)
                return;

            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                repository.UnregisterPendingDelete(fileId);
            }
        }
    }
}
