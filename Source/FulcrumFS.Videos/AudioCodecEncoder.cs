namespace FulcrumFS.Videos;

/// <summary>
/// The audio codec encoder to use for audio encoding.
/// </summary>
public enum AudioCodecEncoder
{
    /// <summary>
    /// The libfdk_aac encoder for AAC audio codec.
    /// </summary>
    LibFDKAAC,

    /// <summary>
    /// The native AAC encoder.
    /// </summary>
    AAC,

    /// <summary>
    /// The libtwolame encoder for MP2 audio codec.
    /// </summary>
    LibTwoLame,

    /// <summary>
    /// The libmp3lame encoder for MP3 audio codec.
    /// </summary>
    LibMp3Lame,

    /// <summary>
    /// The native MP2 encoder.
    /// </summary>
    MP2,

    /// <summary>
    /// The libshine encoder for MP3 audio codec.
    /// </summary>
    LibShine,

    /// <summary>
    /// The native vorbis encoder.
    /// </summary>
    Vorbis,

    /// <summary>
    /// The libvorbis encoder for Vorbis audio codec.
    /// </summary>
    LibVorbis,

    /// <summary>
    /// The libopus encoder for Opus audio codec.
    /// </summary>
    LibOpus,
}
