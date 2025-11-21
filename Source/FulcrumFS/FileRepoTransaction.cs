namespace FulcrumFS;

/// <summary>
/// Represents a transaction for a file repository, allowing changes to be committed or rolled back.
/// </summary>
public sealed class FileRepoTransaction : IAsyncDisposable
{
    private readonly HashSet<FileId> _added = [];
    private readonly HashSet<FileId> _deleted = [];
    private readonly HashSet<FileId> _pendingOutcome = [];

    private readonly AsyncLock _sync = new();

    private bool _isDisposed;

    /// <summary>
    /// Gets the file repository associated with this transaction.
    /// </summary>
    public FileRepo Repository
    {
        get => !_isDisposed ? field : throw new ObjectDisposedException(nameof(FileRepoTransaction));
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
    /// Adds a new file to the repository with the specified file stream, processing it through the specified file processor.
    /// </summary>
    /// <param name="stream">The source file stream.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="processor">The file processor to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The file ID and extension of the resulting file.</returns>
    public async Task<AddFileResult> AddAsync(FileStream stream, bool leaveOpen, FileProcessor processor, CancellationToken cancellationToken = default)
    {
        return await AddAsync(stream, leaveOpen, processor.SingleProcessorPipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a new file to the repository with the specified file stream, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="stream">The source file stream.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The file ID and extension of the resulting file.</returns>
    public Task<AddFileResult> AddAsync(FileStream stream, bool leaveOpen, FileProcessPipeline pipeline, CancellationToken cancellationToken = default)
    {
        string extension = FilePath.ParseAbsolute(stream.Name, PathOptions.None).Extension;
        return AddAsync(stream, extension, leaveOpen, pipeline, cancellationToken);
    }

    /// <summary>
    /// Adds a new file to the repository with the specified stream and extension, processing it through the specified file processor.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="extension">The source file extension.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="processor">The file processor to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The file ID and extension of the resulting file.</returns>
    public async Task<AddFileResult> AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessor processor,
        CancellationToken cancellationToken = default)
    {
        return await AddAsync(stream, extension, leaveOpen, processor.SingleProcessorPipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a new file to the repository with the specified stream and extension, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="extension">The source file extension.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The file ID and extension of the resulting file.</returns>
    public async Task<AddFileResult> AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        FileProcessPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        using var syncLock = await _sync.LockAsync(cancellationToken).ConfigureAwait(false);

        var result = await Repository.TxnAddAsync(stream, extension, leaveOpen, pipeline, OnFileIdCreated, cancellationToken).ConfigureAwait(false);
        _added.Add(result.FileId);

        return result;

        void OnFileIdCreated(FileId fileId)
        {
            lock (_pendingOutcome)
                _pendingOutcome.Add(fileId);
        }
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID.
    /// </summary>
    public async Task DeleteAsync(FileId fileId, CancellationToken cacnellationToken = default)
    {
        using var syncLock = await _sync.LockAsync(cacnellationToken).ConfigureAwait(false);

        if (_added.Contains(fileId))
        {
            await Repository.TxnRollbackAddAsync(fileId).ConfigureAwait(false);
            _added.Remove(fileId);

            lock (_pendingOutcome)
                _pendingOutcome.Remove(fileId);
        }
        else
        {
            await Repository.TxnDeleteAsync(fileId).ConfigureAwait(false);
            _deleted.Add(fileId);

            lock (_pendingOutcome)
                _pendingOutcome.Add(fileId);
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
                await elc.TryRunAsync(Repository.TxnCommitAddAsync(fileId)).ConfigureAwait(false);
                _added.Remove(fileId);
            }

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(Repository.TxnCommitDeleteAsync(fileId)).ConfigureAwait(false);
                _deleted.Remove(fileId);
            }

            lock (_pendingOutcome)
                _pendingOutcome.Clear();
        }

        if (elc.HasExceptions)
            await Repository.OnTxnCommitErrorAsync(elc.ResultException).ConfigureAwait(false);
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

        try
        {
            var repository = Repository;
            _isDisposed = true;

            await RollbackAsync(repository, default).ConfigureAwait(false);
            repository.UnregisterTransaction(this);
        }
        catch { }
    }

    private async Task RollbackAsync(FileRepo repository, CancellationToken cancellationToken)
    {
        var elc = new ExceptionListCapture(ex => ex is not ObjectDisposedException);

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var fileId in _added)
            {
                await elc.TryRunAsync(repository.TxnRollbackAddAsync(fileId)).ConfigureAwait(false);
                _added.Remove(fileId);
            }

            foreach (var fileId in _deleted)
            {
                await elc.TryRunAsync(repository.TxnRollbackDeleteAsync(fileId)).ConfigureAwait(false);
                _deleted.Remove(fileId);
            }

            lock (_pendingOutcome)
                _pendingOutcome.Clear();
        }

        if (elc.HasExceptions)
            await repository.OnTxnRollbackErrorAsync(elc.ResultException).ConfigureAwait(false);
    }

    internal bool IsFilePendingOutcome(FileId fileId)
    {
        lock (_pendingOutcome)
            return _pendingOutcome.Contains(fileId);
    }
}
