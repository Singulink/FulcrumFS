namespace FulcrumFS.Videos;

/// <summary>
/// Specifies tuning options for video encoding.
/// </summary>
public enum VideoTune
{
    /// <summary>
    /// Default tuning ffmpeg chooses.
    /// </summary>
    Default,

    /// <summary>
    /// Tune for high quality films or movies.
    /// </summary>
    Film,

    /// <summary>
    /// Tune for cartoons and animated content.
    /// </summary>
    Animation,

    /// <summary>
    /// Tune for grainy content, to preserve the grain structure.
    /// </summary>
    Grain,

    /// <summary>
    /// Tune for still images or slideshows.
    /// </summary>
    StillImage,

    /// <summary>
    /// Tune for fast decoding.
    /// </summary>
    FastDecode,

    /// <summary>
    /// Tune for low latency streaming.
    /// </summary>
    ZeroLatency,
}
