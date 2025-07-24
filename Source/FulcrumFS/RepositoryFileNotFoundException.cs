namespace FulcrumFS;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that is thrown when a file in the repository is not found.
/// </summary>
public class RepositoryFileNotFoundException : FileNotFoundException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepositoryFileNotFoundException"/> class with a default message.
    /// </summary>
    public RepositoryFileNotFoundException() : base("The specified file was not found in the repository.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepositoryFileNotFoundException"/> class with a specified message.
    /// </summary>
    public RepositoryFileNotFoundException(string? message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepositoryFileNotFoundException"/> class with a specified message and an inner exception.
    /// </summary>
    public RepositoryFileNotFoundException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
