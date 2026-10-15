namespace FulcrumFS;

/// <summary>
/// Adds files for a transaction and tracks the files it has tentatively added; shared by <see cref="FileRepoTransaction"/> and
/// <see cref="FileRepoStagedTransaction"/>. Not thread-safe: the owning transaction serializes every call through its own lock, which an add takes itself.
/// </summary>
internal sealed class TransactionAddSet
{
    private readonly FileRepo _repository;
    private readonly HashSet<FileId> _added = [];

    /// <summary>
    /// Tracks files that were added by the transaction and then discarded by a delete in it, so that deleting one again is a no-op like any other repeated
    /// delete within the transaction (the discarded file is gone from the repository, so the repository alone would report it as not found).
    /// </summary>
    private readonly HashSet<FileId> _discarded = [];

    /// <summary>
    /// Tracks the exclusive open handle on each added file's indeterminate marker. Keeping these open is what signals to cleanup operations that the
    /// transaction is still active and the markers must not be resolved, and what keeps another transaction from recording a deletion of the file in a
    /// marker that the commit of the add removes. A handle is handed to the repository when its file is committed, rolled back or discarded, because the
    /// repository releases it in the same step in which it removes the marker or the file.
    /// </summary>
    private readonly Dictionary<FileId, IAsyncDisposable> _indeterminateMarkers = [];

    public TransactionAddSet(FileRepo repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Gets the files that were added and have not been committed, rolled back or discarded yet. <see cref="CommitAsync"/> and <see cref="RollbackAsync"/>
    /// remove a file from it, which is safe while it is being enumerated.
    /// </summary>
    public IReadOnlyCollection<FileId> FileIds => _added;

    /// <summary>
    /// Creates the task of an add of the transaction, which tentatively adds a file to the repository and starts tracking it. The task takes the transaction's
    /// lock, and checks that the transaction can still add files once it has the lock.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="extension">The source extension, or <see langword="null"/> to take it from the file name of <paramref name="stream"/>, which is then a
    /// <see cref="FileStream"/>.</param>
    /// <param name="leaveOpen">Whether to leave the stream open; otherwise the add closes it whether it succeeds or fails.</param>
    /// <param name="pipeline">The pipeline, or the selector that chooses it by extension.</param>
    /// <param name="sync">The transaction's lock, which serializes the calls on it.</param>
    /// <param name="throwIfNotOpen">Throws if the transaction can no longer add files.</param>
    /// <param name="cancellationToken">A token that cancels the add.</param>
    public TaskWithProgress<RepoFileGroupInfo> AddAsync(
        Stream stream,
        string? extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        AsyncLock sync,
        Action throwIfNotOpen,
        CancellationToken cancellationToken)
    {
        var source = new AddSourceStream(stream, leaveOpen);

        // The task is not given the token, because a task whose token is cancelled throws before it starts the add, which then could not close the stream.
        // The add checks the token itself, first, so that every failure of the add goes through the catch below.
        return new(CancellationToken.None, async (_, progressCallback) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read here rather than when the add is called, so that a stream whose name is not a path fails the add like any other failure.
                extension ??= FilePath.ParseAbsolute(((FileStream)stream).Name, PathOptions.None).Extension;

                using var syncLock = await sync.LockAsync(cancellationToken).ConfigureAwait(false);
                throwIfNotOpen();

                var (result, indeterminateMarker) = await _repository.TxnAddAsync(source, extension, pipeline, progressCallback, cancellationToken)
                    .ConfigureAwait(false);

                _added.Add(result.FileId);
                _indeterminateMarkers[result.FileId] = indeterminateMarker;

                return result;
            }
            catch
            {
                // Until the processing context of the main file has taken the stream over, the add closes it.
                await source.CloseIfNotTakenOverAsync().ConfigureAwait(false);
                throw;
            }
        });
    }

    /// <summary>
    /// Discards the add of a file, if the transaction added it. Returns whether the file was added by the transaction and is now discarded, or was
    /// discarded earlier, and the failure to delete the file, if there was one.
    /// </summary>
    /// <remarks>
    /// A discard is the rollback of one add, so a failure to delete the file is not thrown: the caller reports it through
    /// <see cref="FileRepo.TransactionCompletionFailed"/> as the failure of a rollback, once it has released the transaction's lock, as a commit and a
    /// rollback report theirs, so that a handler can act on the transaction. The file is left indeterminate for the cleaner, which deletes it because no
    /// record references it. The add is discarded whatever happens: a later commit of the transaction must not commit a file the application has deleted.
    /// A disposed repository is thrown, as it is by every operation of the transaction.
    /// </remarks>
    public async ValueTask<(bool IsDiscarded, Exception? Failure)> TryDiscardAsync(FileId fileId)
    {
        if (_discarded.Contains(fileId))
            return (true, null);

        if (!_added.Contains(fileId))
            return (false, null);

        Exception? failure = null;

        try
        {
            await _repository.TxnRollbackAddAsync(fileId, TakeIndeterminateMarker(fileId), waitBudget: null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            failure = ex;
        }
        finally
        {
            _added.Remove(fileId);
            _discarded.Add(fileId);
        }

        return (true, failure);
    }

    /// <summary>
    /// Commits the add of a file and stops tracking it, even if the commit fails (the file is then left indeterminate for the cleaner). The commit waits for
    /// access to the repository within the transaction commit's wait budget.
    /// </summary>
    public async ValueTask CommitAsync(FileId fileId, AccessWaitBudget waitBudget)
    {
        try
        {
            await _repository.TxnCommitAddAsync(fileId, TakeIndeterminateMarker(fileId), waitBudget).ConfigureAwait(false);
        }
        finally
        {
            _added.Remove(fileId);
        }
    }

    /// <summary>
    /// Rolls back the add of a file and stops tracking it, even if the rollback fails (the file is then left indeterminate for the cleaner). The rollback
    /// waits for access to the repository within the transaction rollback's wait budget.
    /// </summary>
    public async ValueTask RollbackAsync(FileId fileId, AccessWaitBudget waitBudget)
    {
        try
        {
            await _repository.TxnRollbackAddAsync(fileId, TakeIndeterminateMarker(fileId), waitBudget).ConfigureAwait(false);
        }
        finally
        {
            _added.Remove(fileId);
        }
    }

    /// <summary>
    /// Forgets the adds that deletes in the transaction discarded.
    /// </summary>
    public void ClearDiscarded() => _discarded.Clear();

    /// <summary>
    /// Releases every indeterminate marker handle that is still held without committing or rolling back its file, which leaves the file indeterminate for
    /// the cleaner. Failures to release are ignored.
    /// </summary>
    public async ValueTask ReleaseIndeterminateMarkersAsync()
    {
        if (_indeterminateMarkers.Count is 0)
            return;

        var remainingHandles = _indeterminateMarkers.Values.ToArray();
        _indeterminateMarkers.Clear();

        foreach (var handle in remainingHandles)
        {
            try { await handle.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }

    /// <summary>
    /// Stops tracking the handle on a file's indeterminate marker and returns it for the repository to release, or <see langword="null"/> if the transaction
    /// no longer holds one.
    /// </summary>
    private IAsyncDisposable? TakeIndeterminateMarker(FileId fileId) => _indeterminateMarkers.Remove(fileId, out var handle) ? handle : null;
}
