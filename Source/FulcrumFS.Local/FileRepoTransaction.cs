namespace FulcrumFS;

/// <summary>
/// Represents a transaction for a file repository, allowing changes to be committed or rolled back.
/// </summary>
public sealed class FileRepoTransaction : IFileRepoTransaction
{
    private readonly TransactionAddSet _adds;
    private readonly HashSet<FileId> _deleted = [];

    /// <summary>
    /// Tracks the shared open handle on each deleted file's indeterminate marker. Keeping these open is what signals to cleanup operations that the
    /// transaction is still active and the markers must not be resolved. The repository counts each as a pending deletion of its file until it is released,
    /// which keeps the rollback of another transaction's deletion of the same file from removing the marker. A handle is handed to the repository when its
    /// deletion is committed or rolled back, because the repository releases it in the same step in which it finishes the deletion.
    /// </summary>
    private readonly Dictionary<FileId, IAsyncDisposable> _deleteMarkers = [];

    private readonly AsyncLock _sync = new();

    private bool _isDisposed;

    /// <summary>
    /// Gets the file repository associated with this transaction.
    /// </summary>
    public FileRepo Repository
    {
        get {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return field;
        }
        private init;
    }

    /// <inheritdoc/>
    IFileRepo IFileRepoTransaction.Repository => Repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoTransaction"/> class for the specified file repository.
    /// </summary>
    internal FileRepoTransaction(FileRepo repository)
    {
        Repository = repository;
        _adds = new TransactionAddSet(repository);
    }

    /// <summary>
    /// Adds a new file to the repository with the specified file stream, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="stream">The source file stream.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline provider to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The resulting file group, including the main file and any auto-variants produced by the pipeline.</returns>
    public TaskWithProgress<RepoFileGroupInfo> AddAsync(FileStream stream, bool leaveOpen, IFileProcessingPipelineSelector pipeline, CancellationToken cancellationToken = default)
    {
        string extension = FilePath.ParseAbsolute(stream.Name, PathOptions.None).Extension;
        return AddAsync(stream, extension, leaveOpen, pipeline, cancellationToken);
    }

    /// <summary>
    /// Adds a new file to the repository with the specified stream and extension, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="extension">The source file extension.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline provider to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The resulting file group, including the main file and any auto-variants produced by the pipeline.</returns>
    public TaskWithProgress<RepoFileGroupInfo> AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        return new(cancellationToken, async (ct, progressCallback) =>
        {
            using var syncLock = await _sync.LockAsync(ct).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            return await _adds.AddAsync(stream, extension, leaveOpen, pipeline, progressCallback, ct).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    TaskWithProgress<IRepoFileGroup> IFileRepoTransaction.AddAsync(
        FileStream stream,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IRepoFileGroup>.Upcast(AddAsync(stream, leaveOpen, pipeline, cancellationToken));
    }

    /// <inheritdoc/>
    TaskWithProgress<IRepoFileGroup> IFileRepoTransaction.AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IRepoFileGroup>.Upcast(AddAsync(stream, extension, leaveOpen, pipeline, cancellationToken));
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID.
    /// </summary>
    /// <param name="fileId">The ID of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <remarks>
    /// <para>
    /// Use this method when the application believes the file exists, so that a mismatch (a stale reference, or a referenced file that is missing) surfaces
    /// as an exception. To delete a file that may be missing without that being an error, use <see cref="TryDeleteAsync(FileId, CancellationToken)"/>
    /// instead.</para>
    /// <para>
    /// Deleting a file that was added by this transaction discards the add. A failure to delete that file is reported through
    /// <see cref="FileRepo.TransactionCompletionFailed"/> as the failure of a rollback, not thrown, and the file is left for the cleaner; the event is
    /// raised once this method has released the transaction, so a handler can act on it, and a handler that throws makes this method throw its exception
    /// (the add is discarded all the same). Deleting a file that this transaction has already deleted or discarded does nothing.</para>
    /// <para>
    /// Deleting a file that another transaction has also deleted succeeds, including when that deletion has committed and the file is pending cleanup: the
    /// deletions converge in every <see cref="FileRepoOptions.DeleteMode"/>, whichever kind of transaction made them. Only a file that is gone, or never
    /// existed, throws.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet cannot be deleted while that transaction is open, because it holds the file's
    /// indeterminate marker, which this deletion is recorded in. The adding transaction normally finishes within milliseconds, so this method waits for
    /// the marker, for up to half a second, and then makes the deletion as if it had been called at that moment: the file is deleted if its add was
    /// committed, and not found if it was rolled back. If the marker is still in use when the wait is over, the method throws a
    /// <see cref="RepoFileBusyException"/>. Nothing has been recorded then, and the transaction stays usable: the deletion can be made again, and
    /// succeeds once the adding transaction has committed. The wait cannot end if the adding transaction is one that the caller itself has open, which
    /// is why it is short. A <see cref="FileRepoStagedTransaction"/> on a repository with delete journals accepts such a deletion instead.</para>
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file was not found in the repository: it never existed, or its deletion has already been carried
    /// out.</exception>
    /// <exception cref="RepoFileBusyException">The file's indeterminate marker was still in use when the wait for it was over, which normally means that
    /// another transaction is adding the file and has not committed or rolled back yet. Nothing has been recorded.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the call waited: for another call on this transaction, or for the file's
    /// indeterminate marker. Nothing has been recorded.</exception>
    public async Task DeleteAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        await DeleteCoreAsync(fileId, throwIfNotFound: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID if it exists, and reports whether it did.
    /// </summary>
    /// <param name="fileId">The ID of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the file existed and is now scheduled for deletion by this transaction (or was added by this transaction and has been
    /// discarded); <see langword="false"/> if no such file exists.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Use this method instead of <see cref="DeleteAsync(FileId, CancellationToken)"/> when a missing file is not an error for the caller, for example when
    /// deleting a record whose file may be gone after the database was restored from a backup taken later than the repository's. Nothing is written to the
    /// repository when <see langword="false"/> is returned.</para>
    /// <para>
    /// A file that another transaction has deleted exists until its deletion has been carried out, so this method returns <see langword="true"/> for a file
    /// whose deletion has committed and is pending cleanup, and the deletions converge as described on
    /// <see cref="DeleteAsync(FileId, CancellationToken)"/>.</para>
    /// <para>
    /// Prefer <see cref="DeleteAsync(FileId, CancellationToken)"/> when the application believes the file exists: a file that is unexpectedly missing usually
    /// indicates an application bug or a missing referenced file, and it should surface rather than be ignored.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet is not reported as missing while that transaction is open: this method waits for
    /// the file's indeterminate marker as <see cref="DeleteAsync(FileId, CancellationToken)"/> does, returns <see langword="true"/> if the add was
    /// committed in that time and <see langword="false"/> if it was rolled back, and throws a <see cref="RepoFileBusyException"/> if the marker is still
    /// in use when the wait is over.</para>
    /// </remarks>
    /// <exception cref="RepoFileBusyException">The file's indeterminate marker was still in use when the wait for it was over, which normally means that
    /// another transaction is adding the file and has not committed or rolled back yet. Nothing has been recorded.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the call waited: for another call on this transaction, or for the file's
    /// indeterminate marker. Nothing has been recorded.</exception>
    public Task<bool> TryDeleteAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        return DeleteCoreAsync(fileId, throwIfNotFound: false, cancellationToken);
    }

    /// <summary>
    /// Commits the transaction, making all file additions and deletion made during the transaction permanent in the repository.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the call while it waits for another call on this transaction to finish. Once the
    /// commit has started it is not observed.</param>
    /// <remarks>
    /// <para>
    /// In the deferred delete modes the deletions are recorded for the cleaner: a transaction that deletes two or more files records them in one delete
    /// journal, flushed to disk once for all of them, on a repository whose <see cref="FileRepo.FormatVersion"/> is 2 or later, and otherwise each deleted
    /// file gets a delete marker of its own. In <see cref="DeleteMode.Immediate"/> mode the files are deleted now; one that cannot be deleted at once
    /// gets a delete marker, and a cleanup pass deletes it.</para>
    /// <para>
    /// A failure to commit an individual file, or to write the journal, is not thrown: it is reported through
    /// <see cref="FileRepo.TransactionCompletionFailed"/>. If the journal cannot be written, each deletion is recorded with its own delete marker instead. A
    /// file whose commit failed stays accessible in an indeterminate state for the next cleanup pass to resolve.</para>
    /// <para>
    /// If the repository cannot be accessed, for example because its volume has stopped answering, the commit waits for it for up to
    /// <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> once for all of its files, not once for each, counting only the time it waits. Once that time
    /// is used up, each further file is attempted once without waiting, and one that cannot be committed is reported and left for the cleaner as
    /// above.</para>
    /// <para>
    /// An indeterminate marker that cannot be removed because something else has it open is tolerated, not reported: removing the marker is what commits
    /// an added file, the file is accessible either way, and it stays indeterminate until the next cleanup pass resolves it and removes the marker. The
    /// attempt is logged to the marker (see <see cref="FileRepoOptions.MarkerFileLogging"/>).</para>
    /// <para>
    /// The files are committed one by one, so the transaction has no single point at which it commits, and once the commit has started the method throws
    /// in two cases only: a <see cref="FileRepo.TransactionCompletionFailed"/> handler threw, which happens after every file has been attempted, or the
    /// repository has been disposed, which leaves the files not yet committed indeterminate. Neither means that the transaction was rolled back, and
    /// disposing it afterwards rolls nothing back. An <see cref="OperationCanceledException"/> is thrown before the commit has started: nothing has been
    /// committed, the transaction is still open, and disposing it rolls it back. Pass <see cref="CancellationToken.None"/> once the application's database
    /// has committed.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token was cancelled before the commit had started. Nothing has been committed.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed: nothing is committed, and a transaction that was disposed without
    /// having been committed was rolled back by its disposal. Or the repository has been disposed: the files that had not been committed yet are left
    /// indeterminate.</exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var elc = new ExceptionListCapture(ex => ex is not ObjectDisposedException);
        bool isJournaled = false;

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            // Disposal rolled the transaction back, so there is nothing left to commit: returning normally would tell the caller that its files are in the
            // repository when they are gone.
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            // The files share one wait for access to the repository, so that on a volume that has stopped answering the commit fails after one wait
            // instead of one per file.
            var waitBudget = new AccessWaitBudget(Repository.Options.MaxAccessWaitOrRetryTime);

            foreach (var fileId in _adds.FileIds)
                await elc.TryRunAsync(_adds.CommitAsync(fileId, waitBudget)).ConfigureAwait(false);

            // The journal is written while the indeterminate markers are still held, so the cleaner cannot resolve the files before their deletion is
            // recorded. A journal that cannot be written is reported like any other completion failure, and the deletions fall back to a marker each.
            if (_deleted.Count > 0)
                await elc.TryRunAsync(CommitDeletesToJournalAsync(waitBudget)).ConfigureAwait(false);

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(isJournaled ? FinishJournaledDeleteAsync(fileId, waitBudget) : CommitDeleteAsync(fileId, waitBudget))
                    .ConfigureAwait(false);

                _deleted.Remove(fileId);
            }

            _adds.ClearDiscarded();
        }

        if (elc.HasExceptions)
            await Repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Commit, elc.ResultException).ConfigureAwait(false);

        async Task CommitDeletesToJournalAsync(AccessWaitBudget waitBudget) =>
            isJournaled = await Repository.TxnCommitDeletesToJournalAsync(_deleted, waitBudget).ConfigureAwait(false);

        async Task CommitDeleteAsync(FileId fileId, AccessWaitBudget waitBudget) =>
            await Repository.TxnCommitDeleteAsync(fileId, TakeDeleteMarker(fileId), waitBudget).ConfigureAwait(false);

        async Task FinishJournaledDeleteAsync(FileId fileId, AccessWaitBudget waitBudget) =>
            await Repository.TxnFinishJournaledDeleteAsync(fileId, TakeDeleteMarker(fileId), waitBudget).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the transaction, discarding any file additions or deletions made during the transaction.
    /// </summary>
    /// <remarks>
    /// Failures to roll back individual files are not thrown: they are reported through <see cref="FileRepo.TransactionCompletionFailed"/>, and the affected
    /// files are left for the cleaner. If the repository cannot be accessed, the rollback waits for it for up to
    /// <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> once for all of its files, as <see cref="CommitAsync(CancellationToken)"/> does.
    /// </remarks>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => RollbackAsync(Repository, cancellationToken);

    /// <summary>
    /// Disposes the transaction, rolling back any changes that were not already committed.
    /// </summary>
    /// <remarks>
    /// The rollback is made as by <see cref="RollbackAsync(CancellationToken)"/>, which describes how failures are reported and how long it waits for access
    /// to the repository.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        var repository = Repository;
        _isDisposed = true;

        try
        {
            await RollbackAsync(repository, default).ConfigureAwait(false);
        }
        catch { }

        // Defensive: ensure any remaining marker handles are released even if RollbackAsync failed to do so. This avoids leaking exclusive file handles
        // that would block cleanup from resolving the markers later.

        await _adds.ReleaseIndeterminateMarkersAsync().ConfigureAwait(false);

        if (_deleteMarkers.Count > 0)
        {
            var remainingHandles = _deleteMarkers.Values.ToArray();
            _deleteMarkers.Clear();

            foreach (var handle in remainingHandles)
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private async Task RollbackAsync(FileRepo repository, CancellationToken cancellationToken)
    {
        var elc = new ExceptionListCapture(ex => ex is not ObjectDisposedException);

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            // The files share one wait for access to the repository, as in a commit.
            var waitBudget = new AccessWaitBudget(repository.Options.MaxAccessWaitOrRetryTime);

            foreach (var fileId in _adds.FileIds)
                await elc.TryRunAsync(_adds.RollbackAsync(fileId, waitBudget)).ConfigureAwait(false);

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(RollbackDeleteAsync(fileId, waitBudget)).ConfigureAwait(false);
                _deleted.Remove(fileId);
            }

            _adds.ClearDiscarded();
        }

        if (elc.HasExceptions)
            await repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Rollback, elc.ResultException).ConfigureAwait(false);

        async Task RollbackDeleteAsync(FileId fileId, AccessWaitBudget waitBudget) =>
            await repository.TxnRollbackDeleteAsync(fileId, TakeDeleteMarker(fileId), waitBudget).ConfigureAwait(false);
    }

    private async Task<bool> DeleteCoreAsync(FileId fileId, bool throwIfNotFound, CancellationToken cancellationToken)
    {
        FileRepo repository;
        Exception? discardFailure;

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            repository = Repository;

            // Deleting a file this transaction added discards the add; deleting it again after that is a no-op.
            (bool isDiscarded, discardFailure) = await _adds.TryDiscardAsync(fileId).ConfigureAwait(false);

            if (!isDiscarded)
                return await DeleteLockedAsync(fileId, throwIfNotFound, cancellationToken).ConfigureAwait(false);
        }

        // A failure to delete a discarded add is reported once the transaction's lock is released, as the failures of a commit or a rollback are, so that a
        // handler can act on the transaction.
        if (discardFailure is not null)
            await repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Rollback, discardFailure).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> DeleteLockedAsync(FileId fileId, bool throwIfNotFound, CancellationToken cancellationToken)
    {
        // A repeated delete must not touch the repository: a second TxnDeleteAsync would open a second shared marker handle and replace the first one in
        // _deleteMarkers, leaking it.
        if (_deleted.Contains(fileId))
            return true;

        // The token cancels the wait for a marker that is in use, normally because the transaction that added the file has not finished. Nothing has
        // been recorded until the call returns, so a cancellation leaves the transaction as it was.
        var indeterminateMarker = await Repository.TxnDeleteAsync(fileId, throwIfNotFound, cancellationToken).ConfigureAwait(false);

        if (indeterminateMarker is null)
            return false;

        _deleted.Add(fileId);
        _deleteMarkers[fileId] = indeterminateMarker;
        return true;
    }

    /// <summary>
    /// Stops tracking the handle on a deleted file's indeterminate marker and returns it for the repository to release, or <see langword="null"/> if the
    /// transaction no longer holds one.
    /// </summary>
    private IAsyncDisposable? TakeDeleteMarker(FileId fileId) => _deleteMarkers.Remove(fileId, out var handle) ? handle : null;
}
