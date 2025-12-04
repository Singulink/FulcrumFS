namespace FulcrumFS.Images;

/// <summary>
/// Represents the compression level for image formats that support it.
/// </summary>
public enum ImageCompressionLevel
{
    /// <summary>
    /// Lowest compression level, should only be used for temporary images when encoding speed is the highest priority and file size is not a concern.
    /// </summary>
    Lowest,

    /// <summary>
    /// Low compression level, only recommended when fast encoding is a priority and images are not expected to be stored for long periods of time.
    /// </summary>
    Low,

    /// <summary>
    /// Medium compression level, recommended when a small increase in image size is acceptable to achieve faster encoding times.
    /// </summary>
    Medium,

    /// <summary>
    /// High compression level, recommended for most use-cases, including long-term storage of images where size is an important factor but encoding time
    /// should not be expended to compress images beyond the point of diminishing returns.
    /// </summary>
    High,

    /// <summary>
    /// Highest compression level, should only be used for long-term storage of images when file size is a critical factor and processing time is not a
    /// concern.
    /// </summary>
    Highest,
}
