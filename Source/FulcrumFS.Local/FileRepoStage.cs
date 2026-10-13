namespace FulcrumFS;

/// <summary>
/// Represents a staged <see cref="FileRepoStagedTransaction"/>, returned by <see cref="FileRepoStagedTransaction.StageAsync(CancellationToken)"/>. Committing
/// the stage commits the transaction.
/// </summary>
/// <remarks>
/// A stage belongs to its transaction and is not disposable on its own: disposing the transaction before the stage has been committed rolls the transaction
/// back.
/// </remarks>
public sealed class FileRepoStage : IFileRepoStage
{
    private readonly FileRepoStagedTransaction _transaction;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoStage"/> class for the specified staged transaction.
    /// </summary>
    internal FileRepoStage(FileRepoStagedTransaction transaction)
    {
        _transaction = transaction;
    }

    /// <summary>
    /// Commits the staged transaction, making the files it added and deleted permanent in the repository. Call this after the application's database has
    /// committed.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the call while it waits for another call on the transaction to finish. Once the
    /// commit has started it is not observed.</param>
    /// <remarks>
    /// <para>
    /// The transaction's journal is committed first, by renaming it, which is a single atomic operation however many files it lists. The added files are
    /// committed next, and then the deletions are applied according to <see cref="FileRepoOptions.DeleteMode"/>: in <see cref="DeleteMode.Immediate"/> mode
    /// the files are deleted now and the journal is removed; in the deferred modes the committed journal stays as the delete record of its files, and a
    /// cleanup pass deletes them once it is older than the delete delay.</para>
    /// <para>
    /// A failure to commit the journal is thrown; nothing else has been committed in that case and the call can be retried. Failures to commit or delete
    /// individual files are not thrown: they are reported through <see cref="FileRepo.TransactionCompletionFailed"/>, as for an ordinary transaction, and the
    /// affected files are left for the cleaner. Calling this method again after it succeeded does nothing.</para>
    /// <para>
    /// If the repository cannot be accessed, for example because its volume has stopped answering, the commit of the files waits for it for up to
    /// <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> once for all of them, not once for each, counting only the time it waits. Once that time is
    /// used up, each further file is attempted once without waiting, and one that cannot be committed or deleted is reported and left for the cleaner as
    /// above.</para>
    /// <para>
    /// Once this method has started the commit, the transaction is never rolled back, whatever the method throws. If it is not called again, disposing the
    /// transaction leaves the files to the cleaner. After a failure to commit the journal, that depends on how far the step came: if the rename failed, the
    /// journal is still staged, and the cleaner resolves its files through the resolution callback; if the journal was renamed and could not be flushed to
    /// disk, it is committed although the call threw, and the cleaner deletes its files without asking. The added files are resolved through the callback
    /// in both cases. An exception thrown by a <see cref="FileRepo.TransactionCompletionFailed"/> handler propagates once the transaction is committed.</para>
    /// <para>
    /// An <see cref="OperationCanceledException"/> is thrown before the commit has started: the transaction is still staged, and disposing it rolls it back.
    /// Pass <see cref="CancellationToken.None"/> here, since the application's database has committed.</para>
    /// <para>
    /// On a version 1 repository, which has no journal (see <see cref="FileRepoStagedTransaction"/>), the added files are committed first and then each
    /// deletion is committed as by <see cref="FileRepoTransaction.CommitAsync(CancellationToken)"/>; every failure is reported rather than thrown.</para>
    /// </remarks>
    /// <exception cref="IOException">The journal could not be committed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the commit had started. Nothing has been committed.</exception>
    /// <exception cref="InvalidOperationException">The transaction has been rolled back.</exception>
    /// <exception cref="ObjectDisposedException">The transaction was disposed before the stage was committed.</exception>
    public Task CommitAsync(CancellationToken cancellationToken = default) => _transaction.CommitStageAsync(cancellationToken);
}
