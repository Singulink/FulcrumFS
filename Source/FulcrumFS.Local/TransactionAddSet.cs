namespace FulcrumFS;

/// <summary>
/// Tracks the files a transaction has tentatively added; shared by <see cref="FileRepoTransaction"/> and <see cref="FileRepoStagedTransaction"/>. Not
/// thread-safe: the owning transaction serializes every call through its own lock.
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
    /// transaction is still active and the markers must not be resolved.
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
    /// Tentatively adds a file to the repository and starts tracking it.
    /// </summary>
    public async Task<RepoFileGroupInfo> AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        Func<ProgressValue, ValueTask>? progressCallback,
        CancellationToken cancellationToken)
    {
        var (result, indeterminateMarker) = await _repository.TxnAddAsync(stream, extension, leaveOpen, pipeline, progressCallback, cancellationToken)
            .ConfigureAwait(false);

        _added.Add(result.FileId);
        _indeterminateMarkers[result.FileId] = indeterminateMarker;

        return result;
    }

    /// <summary>
    /// Discards the add of a file, if the transaction added it. Returns <see langword="true"/> if the file was added by the transaction and is now
    /// discarded, or was discarded earlier; <see langword="false"/> if the transaction did not add it.
    /// </summary>
    public async ValueTask<bool> TryDiscardAsync(FileId fileId)
    {
        if (_discarded.Contains(fileId))
            return true;

        if (!_added.Contains(fileId))
            return false;

        await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
        await _repository.TxnRollbackAddAsync(fileId).ConfigureAwait(false);
        _added.Remove(fileId);
        _discarded.Add(fileId);
        return true;
    }

    /// <summary>
    /// Commits the add of a file and stops tracking it, even if the commit fails (the file is then left indeterminate for the cleaner).
    /// </summary>
    public async ValueTask CommitAsync(FileId fileId)
    {
        try
        {
            await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
            await _repository.TxnCommitAddAsync(fileId).ConfigureAwait(false);
        }
        finally
        {
            _added.Remove(fileId);
        }
    }

    /// <summary>
    /// Rolls back the add of a file and stops tracking it, even if the rollback fails (the file is then left indeterminate for the cleaner).
    /// </summary>
    public async ValueTask RollbackAsync(FileId fileId)
    {
        try
        {
            await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
            await _repository.TxnRollbackAddAsync(fileId).ConfigureAwait(false);
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

    private async ValueTask ReleaseIndeterminateMarkerAsync(FileId fileId)
    {
        if (_indeterminateMarkers.Remove(fileId, out var handle))
            await handle.DisposeAsync().ConfigureAwait(false);
    }
}
