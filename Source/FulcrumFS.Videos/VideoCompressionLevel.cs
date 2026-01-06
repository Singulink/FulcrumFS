namespace FulcrumFS.Videos;

/// <summary>
/// Controls how long it takes to compress the video, and how small the file is - does not affect quality.
/// </summary>
public enum VideoCompressionLevel
{
    /// <summary>
    /// Lowest compression level, resulting in the largest file size but fastest encoding.
    /// </summary>
    Lowest,

    /// <summary>
    /// Low compression level, resulting in a larger file size but faster encoding.
    /// </summary>
    Low,

    /// <summary>
    /// Medium compression level, resulting in a balance between file size and encoding time.
    /// </summary>
    Medium,

    /// <summary>
    /// High compression level, resulting in a smaller file size but slower encoding.
    /// </summary>
    High,

    /// <summary>
    /// Highest compression level, resulting in the smallest file size but slowest encoding.
    /// </summary>
    Highest,
}
