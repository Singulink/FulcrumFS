namespace FulcrumFS.Videos;

/// <summary>
/// Exception thrown when a video process operation is attempted but no streams in the video require re-encoding. Only thrown if the
/// <see cref="VideoProcessor.ThrowWhenReencodeOptional"/> option is set.
/// </summary>
public sealed class VideoReencodeOptionalException : Exception
{
    private const string DefaultMessage = "Video re-encode operation skipped (source video does not require re-encoding).";

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoReencodeOptionalException" /> class with a default message.
    /// </summary>
    public VideoReencodeOptionalException() : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoReencodeOptionalException" /> class with a specified error message.
    /// </summary>
    public VideoReencodeOptionalException(string? message) : base(message ?? DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoReencodeOptionalException" /> class with a specified error message and a reference to the inner
    /// exception that is the cause of this exception.
    /// </summary>
    public VideoReencodeOptionalException(string? message, Exception? innerException) : base(message ?? DefaultMessage, innerException)
    {
    }
}
