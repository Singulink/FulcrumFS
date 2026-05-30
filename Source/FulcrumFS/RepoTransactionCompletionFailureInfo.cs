namespace FulcrumFS;

/// <summary>
/// Describes a transaction-completion failure surfaced via the <see cref="FileRepo.TransactionCompletionFailed"/> event. The failure occurred during the
/// final commit or rollback step (not during the in-flight transaction body) and does not affect the integrity of the underlying data; affected files
/// remain accessible in an indeterminate state pending repair by the next cleaner pass.
/// </summary>
public sealed record RepoTransactionCompletionFailureInfo
{
    internal RepoTransactionCompletionFailureInfo(RepoTransactionCompletionOperation operation, Exception exception)
    {
        Operation = operation;
        Exception = exception;
    }

    /// <summary>
    /// Gets the transaction-completion operation that failed (commit or rollback).
    /// </summary>
    public RepoTransactionCompletionOperation Operation { get; }

    /// <summary>
    /// Gets the exception describing the failure. When multiple errors occurred, this will be an <see cref="AggregateException"/>.
    /// </summary>
    public Exception Exception { get; }
}
