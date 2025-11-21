namespace FulcrumFS.Images;

/// <summary>
/// Represents the compression level for image formats that support it.
/// </summary>
public enum ImageCompressionLevel
{
    /// <summary>
    /// Low compression level, only recommended for fast processing of images that are not expected to be stored for long periods of time.
    /// </summary>
    Low,

    /// <summary>
    /// Medium compression level, recommended for use cases where increased image size is acceptable to achieve faster processing times.
    /// </summary>
    Medium,

    /// <summary>
    /// High compression level, recommended for most use-cases, including long-term storage of images where size is an important factor but processing time
    /// should not be expended to compress images beyond the point of diminishing returns.
    /// </summary>
    High,

    /// <summary>
    /// Maximum compression level, recommended only for long-term storage of images where file size is a critical factor and processing time is not a concern.
    /// </summary>
    Best,
}
