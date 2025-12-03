namespace FulcrumFS;

/// <summary>
/// The exception that is thrown when an error occurs during file processing.
/// </summary>
public class FileProcessingException : Exception
{
    private const string DefaultMessage = "An error occurred while processing the file.";

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessingException"/> class.
    /// </summary>
    public FileProcessingException() : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessingException"/> class with a specified error message.
    /// </summary>
    public FileProcessingException(string? message) : base(message ?? DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessingException"/> class with a specified error message and a reference to the inner exception that is
    /// the cause of this exception.
    /// </summary>
    public FileProcessingException(string? message, Exception? innerException) : base(message ?? DefaultMessage, innerException)
    {
    }
}
