namespace FulcrumFS;

/// <summary>
/// Represents errors that occur during file processing operations.
/// </summary>
public class FileProcessException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessException"/> class.
    /// </summary>
    public FileProcessException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessException"/> class with a specified error message.
    /// </summary>
    public FileProcessException(string? message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessException"/> class with a specified error message and a reference to the inner exception that is
    /// the cause of this exception.
    /// </summary>
    public FileProcessException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
