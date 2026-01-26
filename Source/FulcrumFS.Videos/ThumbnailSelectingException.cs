namespace FulcrumFS.Videos;

/// <summary>
/// The exception that is thrown when no suitable thumbnail could be selected from a video during thumbnail extraction.
/// </summary>
public class ThumbnailSelectingException : FileProcessingException
{
    private const string DefaultMessage = "No suitable thumbnail could be selected from the video.";

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailSelectingException"/> class.
    /// </summary>
    public ThumbnailSelectingException() : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailSelectingException"/> class with a specified error message.
    /// </summary>
    public ThumbnailSelectingException(string? message) : base(message ?? DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailSelectingException"/> class with a specified error message and a reference to the inner exception
    /// that is the cause of this exception.
    /// </summary>
    public ThumbnailSelectingException(string? message, Exception? innerException) : base(message ?? DefaultMessage, innerException)
    {
    }
}
