namespace FulcrumFS;

/// <summary>
/// Represents an exception that can be thrown when file processing does not result in any changes to the source file.
/// </summary>
/// <remarks>
/// See <see cref="FileProcessingPipeline.ThrowWhenSourceUnchanged"/> for more information about when this exception may be thrown.
/// </remarks>
public class FileSourceUnchangedException : FileProcessingException
{
    private const string DefaultMessage = "File processing did not result in any changes to the source file.";

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSourceUnchangedException"/> class with a default message.
    /// </summary>
    public FileSourceUnchangedException() : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSourceUnchangedException"/> class with a specified error message.
    /// </summary>
    public FileSourceUnchangedException(string? message) : base(message ?? DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSourceUnchangedException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    public FileSourceUnchangedException(string? message, Exception? innerException) : base(message ?? DefaultMessage, innerException)
    {
    }
}
