namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the mode for stripping metadata from video files.
/// </summary>
public enum VideoMetadataStrippingMode
{
    /// <summary>
    /// Do not strip any metadata from the video file.
    /// </summary>
    None,

    /// <summary>
    /// Strip only thumbnail metadata from the video.
    /// </summary>
    ThumbnailOnly,

    /// <summary>
    /// Stripping all metadata from the video is preferred (e.g., to reduce file size), but not required - that is, it will be not cause remuxing or
    /// re-encoding, but if it does happen, then all metadata will be stripped.
    /// </summary>
    Preferred,

    /// <summary>
    /// Stripping all metadata from the video is required (e.g., for privacy reasons).
    /// </summary>
    Required,
}
