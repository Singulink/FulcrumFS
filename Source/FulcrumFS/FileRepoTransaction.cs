namespace FulcrumFS;

/// <summary>
/// Represents a transaction for a file repository, allowing changes to be committed or rolled back.
/// </summary>
public sealed class FileRepoTransaction : IAsyncDisposable
{
    private readonly FileRepo _repository;
    private readonly HashSet<FileId> _addedIds = [];
    private readonly HashSet<FileId> _deletedIds = [];

    private readonly AsyncLock _sync = new();

    private bool _isDisposed;

    /// <summary>
    /// Gets the file repository associated with this transaction.
    /// </summary>
    public FileRepo Repository => !_isDisposed ? _repository : throw new ObjectDisposedException(nameof(FileRepoTransaction));

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoTransaction"/> class for the specified file repository.
    /// </summary>
    internal FileRepoTransaction(FileRepo repository)
    {
        _repository = repository;
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
        return await AddAsync(stream, leaveOpen, new FileProcessPipeline([processor]), cancellationToken).ConfigureAwait(false);
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
        return await AddAsync(stream, extension, leaveOpen, new FileProcessPipeline([processor]), cancellationToken).ConfigureAwait(false);
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

        var result = await Repository.TxnAddAsync(stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
        _addedIds.Add(result.FileId);

        return result;
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID.
    /// </summary>
    public async Task DeleteAsync(FileId fileId)
    {
        using var syncLock = await _sync.LockAsync().ConfigureAwait(false);

        if (_addedIds.Contains(fileId))
        {
            await Repository.TxnRollbackAddAsync(fileId).ConfigureAwait(false);
            _addedIds.Remove(fileId);
        }
        else
        {
            await Repository.TxnDeleteAsync(fileId).ConfigureAwait(false);
            _deletedIds.Add(fileId);
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
            foreach (var fileId in _addedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await elc.TryRunAsync(Repository.TxnCommitAddAsync(fileId)).ConfigureAwait(false);
                _addedIds.Remove(fileId);
            }

            foreach (var fileId in _deletedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await elc.TryRunAsync(Repository.TxnCommitDeleteAsync(fileId)).ConfigureAwait(false);
                _deletedIds.Remove(fileId);
            }
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

        _isDisposed = true;

        try
        {
            await RollbackAsync(_repository, default).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task RollbackAsync(FileRepo repository, CancellationToken cancellationToken)
    {
        var elc = new ExceptionListCapture(ex => ex is not ObjectDisposedException);

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var fileId in _addedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await elc.TryRunAsync(repository.TxnRollbackAddAsync(fileId)).ConfigureAwait(false);
                _addedIds.Remove(fileId);
            }

            foreach (var fileId in _deletedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await elc.TryRunAsync(repository.TxnRollbackDeleteAsync(fileId)).ConfigureAwait(false);
                _deletedIds.Remove(fileId);
            }
        }

        if (elc.HasExceptions)
            await repository.OnTxnRollbackErrorAsync(elc.ResultException).ConfigureAwait(false);
    }
}
