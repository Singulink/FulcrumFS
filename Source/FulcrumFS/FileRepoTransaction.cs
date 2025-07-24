using System.Runtime.CompilerServices;
using System.Threading;
using Nito.AsyncEx;

namespace FulcrumFS;

/// <summary>
/// Represents a transaction for a file repository, allowing changes to be committed or rolled back.
/// </summary>
public sealed class FileRepoTransaction : IAsyncDisposable
{
    private readonly FileRepo _repository;
    private readonly HashSet<Guid> _addedIds = [];
    private readonly HashSet<Guid> _deletedIds = [];

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
    /// Adds a new file to the repository with the specified file stream, processing it through the given pipeline.
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
    /// Adds a new file to the repository with the specified stream and extension, processing it through the given pipeline.
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
        _addedIds.Add(result.Id);

        return result;
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID.
    /// </summary>
    public async Task DeleteAsync(Guid fileId)
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
        using var syncLock = await _sync.LockAsync(cancellationToken).ConfigureAwait(false);

        List<Exception>? errors = null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AddError(Exception ex) => (errors ??= []).Add(ex);

        foreach (var fileId in _addedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await Repository.TxnCommitAddAsync(fileId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                AddError(ex);
            }

            _addedIds.Remove(fileId);
        }

        foreach (var fileId in _deletedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await Repository.TxnCommitDeleteAsync(fileId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                AddError(ex);
            }

            _deletedIds.Remove(fileId);
        }

        if (errors is [var error])
            await Repository.OnTxnCommitErrorAsync(error).ConfigureAwait(false);
        else if (errors is { Count: > 1 })
            await Repository.OnTxnCommitErrorAsync(new AggregateException(errors)).ConfigureAwait(false);
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
        using var syncLock = await _sync.LockAsync(cancellationToken).ConfigureAwait(false);

        List<Exception>? errors = null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AddError(Exception ex) => (errors ??= []).Add(ex);

        foreach (var fileId in _addedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await repository.TxnRollbackAddAsync(fileId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                AddError(ex);
            }

            _addedIds.Remove(fileId);
        }

        foreach (var fileId in _deletedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await repository.TxnRollbackDeleteAsync(fileId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                AddError(ex);
            }

            _deletedIds.Remove(fileId);
        }

        if (errors is [var error])
            await repository.OnTxnRollbackErrorAsync(error).ConfigureAwait(false);
        else if (errors is { Count: > 1 })
            await repository.OnTxnRollbackErrorAsync(new AggregateException(errors)).ConfigureAwait(false);
    }
}
