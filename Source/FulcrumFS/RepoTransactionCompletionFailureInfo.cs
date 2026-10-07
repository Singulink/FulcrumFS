using Singulink.Enums;

namespace FulcrumFS;

/// <summary>
/// Describes a transaction-completion failure surfaced via the <see cref="IFileRepo.TransactionCompletionFailed"/> event. The failure occurred during the
/// final commit or rollback step (not during the in-flight transaction body) and does not affect the integrity of the underlying data; affected files
/// remain accessible in an indeterminate state pending repair by the next cleaner pass.
/// </summary>
public sealed class RepoTransactionCompletionFailureInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoTransactionCompletionFailureInfo"/> class.
    /// </summary>
    /// <param name="operation">The transaction-completion operation that failed.</param>
    /// <param name="exception">The exception describing the failure, an <see cref="AggregateException"/> if several errors occurred.</param>
    /// <remarks>
    /// This constructor exists for <see cref="IFileRepo"/> implementations and is not meant for application code.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not a defined <see cref="RepoTransactionCompletionOperation"/> value.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public RepoTransactionCompletionFailureInfo(RepoTransactionCompletionOperation operation, Exception exception)
    {
        operation.ThrowIfNotDefined();

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
