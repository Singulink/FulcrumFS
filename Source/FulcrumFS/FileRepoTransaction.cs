namespace FulcrumFS;

/// <summary>
/// Represents a transaction for a file repository, allowing changes to be committed or rolled back.
/// </summary>
public sealed class FileRepoTransaction : IAsyncDisposable
{
    private readonly HashSet<FileId> _added = [];
    private readonly HashSet<FileId> _deleted = [];

    /// <summary>
    /// Tracks the exclusive open handle on each pending file's indeterminate marker. Keeping these open is what signals to cleanup operations that the
    /// transaction is still active and the markers must not be resolved.
    /// </summary>
    private readonly Dictionary<FileId, IAsyncDisposable> _indeterminateMarkers = [];

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

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoTransaction"/> class for the specified file repository.
    /// </summary>
    internal FileRepoTransaction(FileRepo repository)
    {
        Repository = repository;
    }

    /// <summary>
    /// Adds a new file to the repository with the specified file stream, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="stream">The source file stream.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline provider to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The resulting file group, including the main file and any auto-variants produced by the pipeline.</returns>
    public Task<RepoFileGroupInfo> AddAsync(FileStream stream, bool leaveOpen, IFileProcessingPipelineSelector pipeline, CancellationToken cancellationToken = default)
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
    public async Task<RepoFileGroupInfo> AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        using var syncLock = await _sync.LockAsync(cancellationToken).ConfigureAwait(false);

        var (result, indeterminateMarker) = await Repository.TxnAddAsync(stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
        _added.Add(result.FileId);
        _indeterminateMarkers[result.FileId] = indeterminateMarker;

        return result;
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID.
    /// </summary>
    public async Task DeleteAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        using var syncLock = await _sync.LockAsync(cancellationToken).ConfigureAwait(false);

        if (_added.Contains(fileId))
        {
            await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
            await Repository.TxnRollbackAddAsync(fileId).ConfigureAwait(false);
            _added.Remove(fileId);
        }
        else
        {
            var indeterminateMarker = await Repository.TxnDeleteAsync(fileId).ConfigureAwait(false);
            _deleted.Add(fileId);
            _indeterminateMarkers[fileId] = indeterminateMarker;
        }
    }

    /// <summary>
    /// Commits the transaction, making all file additions and deletion made during the transaction permanent in the repository.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var elc = new ExceptionListCapture(ex => ex is not ObjectDisposedException);

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var fileId in _added)
            {
                await elc.TryRunAsync(CommitAddAsync(fileId)).ConfigureAwait(false);
                _added.Remove(fileId);
            }

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(CommitDeleteAsync(fileId)).ConfigureAwait(false);
                _deleted.Remove(fileId);
            }
        }

        if (elc.HasExceptions)
            await Repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Commit, elc.ResultException).ConfigureAwait(false);

        async Task CommitAddAsync(FileId fileId)
        {
            await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
            await Repository.TxnCommitAddAsync(fileId).ConfigureAwait(false);
        }

        async Task CommitDeleteAsync(FileId fileId)
        {
            await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
            await Repository.TxnCommitDeleteAsync(fileId).ConfigureAwait(false);
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

        if (_indeterminateMarkers.Count > 0)
        {
            var remainingHandles = _indeterminateMarkers.Values.ToArray();
            _indeterminateMarkers.Clear();

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
            foreach (var fileId in _added)
            {
                await elc.TryRunAsync(RollbackAddAsync(fileId)).ConfigureAwait(false);
                _added.Remove(fileId);
            }

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(RollbackDeleteAsync(fileId)).ConfigureAwait(false);
                _deleted.Remove(fileId);
            }
        }

        if (elc.HasExceptions)
            await repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Rollback, elc.ResultException).ConfigureAwait(false);

        async Task RollbackAddAsync(FileId fileId)
        {
            await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
            await repository.TxnRollbackAddAsync(fileId).ConfigureAwait(false);
        }

        async Task RollbackDeleteAsync(FileId fileId)
        {
            await ReleaseIndeterminateMarkerAsync(fileId).ConfigureAwait(false);
            await repository.TxnRollbackDeleteAsync(fileId).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseIndeterminateMarkerAsync(FileId fileId)
    {
        if (_indeterminateMarkers.Remove(fileId, out var handle))
            await handle.DisposeAsync().ConfigureAwait(false);
    }
}
