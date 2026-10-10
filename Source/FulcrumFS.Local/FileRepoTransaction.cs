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
    /// transaction is still active and the markers must not be resolved.
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
    /// as an exception. To delete a file that may already be gone, for example when retrying or replaying work that may already have run, use
    /// <see cref="TryDeleteAsync(FileId, CancellationToken)"/> instead.</para>
    /// <para>
    /// Deleting a file that was added by this transaction discards the add. Deleting a file that this transaction has already deleted or discarded does
    /// nothing.</para>
    /// <para>
    /// Deleting a file that another transaction has also deleted succeeds, including when that deletion has committed and the file is pending cleanup: the
    /// deletions converge in every <see cref="FileRepoOptions.DeleteMode"/>, whichever kind of transaction made them. Only a file that is gone, or never
    /// existed, throws.</para>
    /// <para>
    /// Deleting a file that another transaction has added and not committed yet throws an <see cref="IOException"/>, because that transaction holds the
    /// file's indeterminate marker, which this deletion is recorded in. Nothing is recorded, and the transaction stays usable: the deletion succeeds once
    /// the adding transaction has committed, and finds no file once it has rolled back. A <see cref="FileRepoStagedTransaction"/> on a repository with
    /// delete journals accepts such a deletion instead.</para>
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file was not found in the repository: it never existed, or its deletion has already been carried
    /// out.</exception>
    /// <exception cref="IOException">Another transaction is adding the file and has not committed or rolled back yet. Nothing has been recorded.</exception>
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
    /// This is the idempotent form of <see cref="DeleteAsync(FileId, CancellationToken)"/> for callers that are retrying or replaying work that may already
    /// have run, such as a queue of deletions replayed after a crash. Nothing is written to the repository when <see langword="false"/> is returned.</para>
    /// <para>
    /// A file that another transaction has deleted exists until its deletion has been carried out, so this method returns <see langword="true"/> for a file
    /// whose deletion has committed and is pending cleanup, and the deletions converge as described on
    /// <see cref="DeleteAsync(FileId, CancellationToken)"/>.</para>
    /// <para>
    /// Prefer <see cref="DeleteAsync(FileId, CancellationToken)"/> when the application believes the file exists: a file that is unexpectedly missing usually
    /// indicates an application bug or a missing referenced file, and it should surface rather than be ignored.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet is not reported as missing: this method throws an <see cref="IOException"/> for it,
    /// as <see cref="DeleteAsync(FileId, CancellationToken)"/> does.</para>
    /// </remarks>
    /// <exception cref="IOException">Another transaction is adding the file and has not committed or rolled back yet. Nothing has been recorded.</exception>
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

            foreach (var fileId in _adds.FileIds)
                await elc.TryRunAsync(_adds.CommitAsync(fileId)).ConfigureAwait(false);

            // The journal is written while the indeterminate markers are still held, so the cleaner cannot resolve the files before their deletion is
            // recorded. A journal that cannot be written is reported like any other completion failure, and the deletions fall back to a marker each.
            if (_deleted.Count > 0)
                await elc.TryRunAsync(CommitDeletesToJournalAsync()).ConfigureAwait(false);

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(isJournaled ? FinishJournaledDeleteAsync(fileId) : CommitDeleteAsync(fileId)).ConfigureAwait(false);
                _deleted.Remove(fileId);
            }

            _adds.ClearDiscarded();
        }

        if (elc.HasExceptions)
            await Repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Commit, elc.ResultException).ConfigureAwait(false);

        async Task CommitDeletesToJournalAsync() => isJournaled = await Repository.TxnCommitDeletesToJournalAsync(_deleted).ConfigureAwait(false);

        async Task CommitDeleteAsync(FileId fileId)
        {
            await ReleaseDeleteMarkerAsync(fileId).ConfigureAwait(false);
            await Repository.TxnCommitDeleteAsync(fileId).ConfigureAwait(false);
        }

        async Task FinishJournaledDeleteAsync(FileId fileId)
        {
            await ReleaseDeleteMarkerAsync(fileId).ConfigureAwait(false);
            await Repository.TxnFinishJournaledDeleteAsync(fileId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rolls back the transaction, discarding any file additions or deletions made during the transaction.
    /// </summary>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => RollbackAsync(Repository, cancellationToken);

    /// <summary>
    /// Disposes the transaction, rolling back any changes that were not already committed.
    /// </summary>
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
            foreach (var fileId in _adds.FileIds)
                await elc.TryRunAsync(_adds.RollbackAsync(fileId)).ConfigureAwait(false);

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(RollbackDeleteAsync(fileId)).ConfigureAwait(false);
                _deleted.Remove(fileId);
            }

            _adds.ClearDiscarded();
        }

        if (elc.HasExceptions)
            await repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Rollback, elc.ResultException).ConfigureAwait(false);

        async Task RollbackDeleteAsync(FileId fileId)
        {
            await ReleaseDeleteMarkerAsync(fileId).ConfigureAwait(false);
            await repository.TxnRollbackDeleteAsync(fileId).ConfigureAwait(false);
        }
    }

    private async Task<bool> DeleteCoreAsync(FileId fileId, bool throwIfNotFound, CancellationToken cancellationToken)
    {
        using var syncLock = await _sync.LockAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Deleting a file this transaction added discards the add; deleting it again after that is a no-op.
        if (await _adds.TryDiscardAsync(fileId).ConfigureAwait(false))
            return true;

        // A repeated delete must not touch the repository: a second TxnDeleteAsync would open a second shared marker handle and replace the first one in
        // _deleteMarkers, leaking it.
        if (_deleted.Contains(fileId))
            return true;

        var indeterminateMarker = await Repository.TxnDeleteAsync(fileId, throwIfNotFound).ConfigureAwait(false);

        if (indeterminateMarker is null)
            return false;

        _deleted.Add(fileId);
        _deleteMarkers[fileId] = indeterminateMarker;
        return true;
    }

    private async ValueTask ReleaseDeleteMarkerAsync(FileId fileId)
    {
        if (_deleteMarkers.Remove(fileId, out var handle))
            await handle.DisposeAsync().ConfigureAwait(false);
    }
}
