namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the audio sample rate.
/// </summary>
public enum AudioSampleRate
{
    /// <summary>
    /// Preserve the original sample rate (up to the maximum supported by the result codec).
    /// </summary>
    Preserve,

    /// <summary>
    /// 44.1 kHz sample rate.
    /// </summary>
    Hz44100,

    /// <summary>
    /// 48 kHz sample rate.
    /// </summary>
    Hz48000,

    /// <summary>
    /// 96 kHz sample rate.
    /// </summary>
    Hz96000,

    /// <summary>
    /// 192 kHz sample rate.
    /// </summary>
    Hz192000,
}
