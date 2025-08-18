namespace FulcrumFS;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// The exception that is thrown when an attempt is made to add a file variant that already exists in the repository.
/// </summary>
public class RepoFileVariantAlreadyExistsException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileVariantAlreadyExistsException"/> class with a specified message.
    /// </summary>
    public RepoFileVariantAlreadyExistsException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileVariantAlreadyExistsException"/> class with a specified message and an inner exception.
    /// </summary>
    public RepoFileVariantAlreadyExistsException(string message, Exception? innerException) : base(message, innerException) { }
}
