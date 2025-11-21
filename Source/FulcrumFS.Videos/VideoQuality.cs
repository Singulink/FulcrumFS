namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the quality level to use for video encoding.
/// </summary>
public enum VideoQuality
{
    /// <summary>
    /// Lowest quality, resulting in the smallest file size - quality will be noticeably degraded, but may be acceptable for some uses.
    /// </summary>
    Worst,

    /// <summary>
    /// Low quality, resulting in a smaller file size - quality may be noticeably degraded, but could still be fine for some kinds of videos.
    /// </summary>
    Low,

    /// <summary>
    /// Medium quality, resulting in a balance between quality and file size - quality should be good enough for standard videos.
    /// </summary>
    Medium,

    /// <summary>
    /// High quality, resulting in the very good quality but larger file size - quality should be close to visually lossless for most videos.
    /// </summary>
    High,

    /// <summary>
    /// Best quality, resulting in the best possible quality - quality should be close to visually lossless for all common kinds of videos.
    /// </summary>
    Best,
}
