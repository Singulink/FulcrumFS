namespace FulcrumFS;

/// <summary>
/// Identifies which transaction-completion step (commit or rollback) was in progress when the failure surfaced via the
/// <see cref="FileRepo.TransactionCompletionFailed"/> event occurred.
/// </summary>
public enum RepoTransactionCompletionOperation
{
    /// <summary>
    /// The transaction was being committed. Added files are still accessible after the failure; they are merely left in an indeterminate state pending
    /// repair by the next cleaner pass.
    /// </summary>
    Commit,

    /// <summary>
    /// The transaction was being rolled back. Deleted files are still accessible after the failure; they are merely left in an indeterminate state pending
    /// repair by the next cleaner pass.
    /// </summary>
    Rollback,
}
