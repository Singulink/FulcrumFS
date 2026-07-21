namespace FulcrumFS;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// The exception that is thrown when an operation cannot proceed because the repository is in a corrupted state that requires external intervention. Examples
/// include multiple data files mapping to the same variant slot, or a rebase that cannot be resumed because required files are missing. See the
/// <see cref="FileRepo.CorruptionDetected"/> event for programmatic discovery of these (and other) corruption conditions; the
/// <see cref="RepoCorruptionInfo.Kind"/> on the event carries the specific kind that triggered this throw.
/// </summary>
public class RepoCorruptedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoCorruptedException"/> class with the specified message.
    /// </summary>
    public RepoCorruptedException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoCorruptedException"/> class with the specified message and inner exception.
    /// </summary>
    public RepoCorruptedException(string message, Exception? innerException) : base(message, innerException) { }
}
