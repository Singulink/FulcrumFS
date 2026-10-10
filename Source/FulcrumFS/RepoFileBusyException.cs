namespace FulcrumFS;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// The exception that is thrown when the deletion of a file in a repository cannot be recorded because the file is in use by something else in the
/// repository, which normally means that the transaction that added the file has not committed or rolled back yet.
/// </summary>
/// <remarks>
/// <para>
/// The condition clears itself: nothing has been recorded when this exception is thrown, the transaction that got it stays usable, and the deletion can be
/// made again. It succeeds once the transaction that added the file has committed, and finds no file once that transaction has rolled back. An
/// implementation may already have waited briefly for that before it throws, as the local <c>FileRepo</c> does.</para>
/// <para>
/// The exception derives from <see cref="IOException"/>, and it is not a <see cref="RepoFileNotFoundException"/>: the file exists. See
/// <see cref="IFileRepoTransaction.DeleteAsync(FileId, CancellationToken)"/> for which deletions an implementation accepts instead of throwing.</para>
/// </remarks>
public class RepoFileBusyException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileBusyException"/> class with a specified message.
    /// </summary>
    public RepoFileBusyException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileBusyException"/> class with a specified message and an inner exception.
    /// </summary>
    public RepoFileBusyException(string message, Exception? innerException) : base(message, innerException) { }
}
