namespace FulcrumFS.Videos;

/// <summary>
/// Exception thrown when an video resize operation is attempted but the video does not require resizing. Only thrown if the <see
/// cref="VideoResizeOptions.ThrowWhenSkipped"/> resize option is set.
/// </summary>
public class VideoResizeSkippedException : Exception
{
    private const string DefaultMessage = "Video resize operation skipped (source video does not require resizing).";

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeSkippedException" /> class with a default message.
    /// </summary>
    public VideoResizeSkippedException() : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeSkippedException" /> class with a specified error message.
    /// </summary>
    public VideoResizeSkippedException(string? message) : base(message ?? DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeSkippedException" /> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    public VideoResizeSkippedException(string? message, Exception? innerException) : base(message ?? DefaultMessage, innerException)
    {
    }
}
