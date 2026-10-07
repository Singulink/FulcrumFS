namespace FulcrumFS;

/// <summary>
/// Represents a staged <see cref="IFileRepoStagedTransaction"/>, returned by <see cref="IFileRepoStagedTransaction.StageAsync(CancellationToken)"/>.
/// Committing the stage commits the transaction.
/// </summary>
/// <remarks>
/// A stage belongs to its transaction and is not disposable on its own: disposing the transaction before the stage has been committed rolls the transaction
/// back.
/// </remarks>
public interface IFileRepoStage
{
    /// <summary>
    /// Commits the staged transaction, making the files it added and deleted permanent in the repository. Call this after the application's database has
    /// committed.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <remarks>
    /// <para>
    /// The deletions are committed first, in a single atomic step however many files they cover. A failure of that step is thrown; nothing has been committed
    /// in that case and the call can be retried. Once it has succeeded, the added files are committed and the deletions are applied according to
    /// <see cref="IFileRepo.DeleteMode"/>, and failures of individual files are not thrown but reported through
    /// <see cref="IFileRepo.TransactionCompletionFailed"/>, as for an ordinary transaction, leaving the affected files for the cleaner.</para>
    /// <para>
    /// Calling this method again after it succeeded does nothing. The transaction is never rolled back once this method has been called, even if it failed:
    /// disposing the transaction then leaves its files for a cleanup pass to resolve through the resolution callback.</para>
    /// </remarks>
    /// <exception cref="IOException">The deletions could not be committed.</exception>
    /// <exception cref="InvalidOperationException">The transaction has been rolled back.</exception>
    /// <exception cref="ObjectDisposedException">The transaction was disposed before the stage was committed.</exception>
    Task CommitAsync(CancellationToken cancellationToken = default);
}
