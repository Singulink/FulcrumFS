namespace FulcrumFS;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that is thrown when a file in the repository is not found.
/// </summary>
public class RepoFileNotFoundException : FileNotFoundException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileNotFoundException"/> class with a default message.
    /// </summary>
    public RepoFileNotFoundException() : base("The specified file was not found in the repository.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileNotFoundException"/> class with a specified message.
    /// </summary>
    public RepoFileNotFoundException(string? message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileNotFoundException"/> class with a specified message and an inner exception.
    /// </summary>
    public RepoFileNotFoundException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
