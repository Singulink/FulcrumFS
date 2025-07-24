using System.Runtime.CompilerServices;

namespace FulcrumFS;

/// <summary>
/// Represents a transaction for a file repository, allowing changes to be committed or rolled back.
/// </summary>
public class FileRepositoryTransaction : IAsyncDisposable
{
    private readonly FileRepository _repository;
    private readonly HashSet<Guid> _addedFileIds = [];
    private readonly HashSet<Guid> _deletedFileIds = [];
    private bool _isDisposed;

    /// <summary>
    /// Gets the file repository associated with this transaction.
    /// </summary>
    public FileRepository Repository => !_isDisposed ? _repository : throw new ObjectDisposedException(nameof(FileRepositoryTransaction));

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepositoryTransaction"/> class for the specified file repository.
    /// </summary>
    internal FileRepositoryTransaction(FileRepository repository)
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
        var result = await Repository.TxnAddAsync(stream, extension, leaveOpen, pipeline, cancellationToken).ConfigureAwait(false);
        _addedFileIds.Add(result.Id);
        return result;
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID.
    /// </summary>
    public async Task DeleteAsync(Guid fileId)
    {
        await Repository.TxnDeleteAsync(fileId).ConfigureAwait(false);
        _deletedFileIds.Add(fileId);
    }

    /// <summary>
    /// Commits the transaction, making all file additions and deletion made during the transaction permanent in the repository.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        List<Exception>? errors = null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AddError(Exception ex) => (errors ??= []).Add(ex);

        foreach (var fileId in _addedFileIds.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (_deletedFileIds.Contains(fileId))
                {
                    await Repository.TxnCommitDeleteAsync(fileId, addedInCurrentTransaction: true).ConfigureAwait(false);
                    _deletedFileIds.Remove(fileId);
                }
                else
                {
                    await Repository.TxnCommitAddAsync(fileId).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                AddError(ex);
            }

            _addedFileIds.Remove(fileId);
        }

        foreach (var fileId in _deletedFileIds.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await Repository.TxnCommitDeleteAsync(fileId, addedInCurrentTransaction: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                AddError(ex);
            }

            _deletedFileIds.Remove(fileId);
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

    private async Task RollbackAsync(FileRepository repository, CancellationToken cancellationToken)
    {
        List<Exception>? errors = null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AddError(Exception ex) => (errors ??= []).Add(ex);

        foreach (var fileId in _addedFileIds.ToArray())
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

            _addedFileIds.Remove(fileId);
            _deletedFileIds.Remove(fileId); // Ensure we don't try to rollback delete if it was added in this transaction
        }

        foreach (var fileId in _deletedFileIds.ToArray())
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

            _deletedFileIds.Remove(fileId);
        }

        if (errors is [var error])
            await repository.OnTxnRollbackErrorAsync(error).ConfigureAwait(false);
        else if (errors is { Count: > 1 })
            await repository.OnTxnRollbackErrorAsync(new AggregateException(errors)).ConfigureAwait(false);
    }
}
