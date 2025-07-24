namespace FulcrumFS.Images;

/// <summary>
/// Exception thrown when an image resize operation is attempted but the image does not require resizing.
/// </summary>
public class ImageResizeSkippedException : Exception
{
    private const string DefaultMessage = "Resize operation was skipped for the image (source image was already in the desired size).";

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageResizeSkippedException"/> class with a default message.
    /// </summary>
    public ImageResizeSkippedException() : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageResizeSkippedException"/> class with a specified error message.
    /// </summary>
    public ImageResizeSkippedException(string? message) : base(message ?? DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageResizeSkippedException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    public ImageResizeSkippedException(string? message, Exception? innerException) : base(message ?? DefaultMessage, innerException)
    {
    }
}
