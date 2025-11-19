namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the number of audio channels.
/// </summary>
public enum AudioChannels
{
    /// <summary>
    /// Preserve the original number of channels (up to the maximum supported by the result codec).
    /// </summary>
    Preserve,

    /// <summary>
    /// Mono (1 channel).
    /// </summary>
    Mono,

    /// <summary>
    /// Stereo (2 channels).
    /// </summary>
    Stereo,
}
