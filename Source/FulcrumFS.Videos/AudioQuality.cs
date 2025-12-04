namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the quality level to use for audio encoding.
/// </summary>
public enum AudioQuality
{
    /// <summary>
    /// Lowest quality, resulting in the smallest file size - quality will be noticeably degraded, but may be acceptable for some uses.
    /// </summary>
    Lowest,

    /// <summary>
    /// Low quality, resulting in a smaller file size - quality may be noticeably degraded, but could still be fine for some kinds of audio.
    /// </summary>
    Low,

    /// <summary>
    /// Medium quality, resulting in a balance between quality and file size - quality should be good enough for standard audio.
    /// </summary>
    Medium,

    /// <summary>
    /// High quality, resulting in very good quality but larger file size - quality should be close to audibly lossless to most humans for most audio.
    /// </summary>
    High,

    /// <summary>
    /// Best quality, resulting in the best possible quality - quality should be close to audibly lossless to most humans for all common kinds of audio.
    /// </summary>
    Highest,
}
