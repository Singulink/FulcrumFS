namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the mode for stripping metadata from video files.
/// </summary>
public enum StripVideoMetadataMode
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
    /// Strip all metadata from the video.
    /// </summary>
    All,
}
