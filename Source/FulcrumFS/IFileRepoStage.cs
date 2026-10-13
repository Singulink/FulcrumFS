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
    /// <param name="cancellationToken">A cancellation token that cancels the call before the commit has started. A commit that has started is not
    /// interrupted.</param>
    /// <remarks>
    /// <para>
    /// The deletions are committed first, in a single atomic step however many files they cover. A failure of that step is thrown: no added file has been
    /// committed and no deletion applied in that case, and the call can be made again. Once the step has succeeded, the added files are committed and the
    /// deletions are applied according to <see cref="IFileRepo.DeleteMode"/>. A failure to commit an individual file is not thrown but reported through
    /// <see cref="IFileRepo.TransactionCompletionFailed"/>, as for an ordinary transaction, leaving the file for the cleaner. A deletion that the step has
    /// committed and that cannot be applied at once is not a failure and is not reported: a cleanup pass carries it out.</para>
    /// <para>
    /// Calling this method again after it succeeded does nothing. The transaction is never rolled back once this method has started the commit, even if it
    /// then throws: the application's database has committed, so disposing the transaction leaves its files for a cleanup pass, which resolves them through
    /// the resolution callback or, if the step that commits the deletions had taken effect before it failed, carries the deletions out. An exception that
    /// a <see cref="IFileRepo.TransactionCompletionFailed"/> handler throws propagates once the transaction is committed, and changes nothing about
    /// that.</para>
    /// <para>
    /// An <see cref="OperationCanceledException"/> is different: it means that the commit was not started, so the transaction is still staged, and disposing
    /// it rolls it back. Pass <see cref="CancellationToken.None"/> here, or a cancellation could discard deletions that the database has committed.</para>
    /// </remarks>
    /// <exception cref="IOException">The deletions could not be committed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the commit had started. Nothing has been committed.</exception>
    /// <exception cref="InvalidOperationException">The transaction has been rolled back.</exception>
    /// <exception cref="ObjectDisposedException">The transaction was disposed before the stage was committed.</exception>
    Task CommitAsync(CancellationToken cancellationToken = default);
}
