namespace FulcrumFS.Videos;

/// <summary>
/// The H.264 profile to use for video encoding.
/// </summary>
public enum H264Profile
{
    /// <summary>
    /// Baseline profile for H.264 encoding (supported by all devices that support H.264) - 8 bits per channel, 4:2:0 chroma subsampling.
    /// </summary>
    Baseline,

    /// <summary>
    /// Main profile for H.264 encoding (generally supported by devices from the early 2010s) - 8 bits per channel, 4:2:0 chroma subsampling.
    /// </summary>
    Main,

    /// <summary>
    /// High profile for H.264 encoding (generally supported by devices from the early-to-mid 2010s) - 8 bits per channel, 4:2:0 chroma subsampling.
    /// </summary>
    High,

    /// <summary>
    /// 10-bit version of high profile for H.264 encoding - supports more than 8 bits per channel, 4:2:0 chroma subsampling.
    /// </summary>
    High10,

    /// <summary>
    /// 4:2:2 chroma subsampling version of high profile for H.264 encoding - supports more than 8 bits per channel, 4:2:2 chroma subsampling.
    /// </summary>
    High422,

    /// <summary>
    /// 4:4:4 chroma subsampling version of high profile for H.264 encoding - supports more than 8 bits per channel, 4:4:4 chroma subsampling.
    /// </summary>
    High444,
}
