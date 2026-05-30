namespace FulcrumFS;

/// <summary>
/// Represents a handler invoked when a transaction-completion step (commit or rollback) fails. Handlers receive a
/// <see cref="RepoTransactionCompletionFailureInfo"/> describing the failed operation and the underlying exception, and may perform logging or alerting.
/// The event fires every time a completion failure occurs; the failure does not affect the integrity of the underlying data and can typically be ignored
/// outside of diagnostics. Handler exceptions are not caught and will propagate out of the operation that detected the failure.
/// </summary>
public delegate Task RepoTransactionCompletionFailedHandler(RepoTransactionCompletionFailureInfo failure);
