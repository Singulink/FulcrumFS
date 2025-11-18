namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for audio codecs used in video processing.
/// </summary>
public abstract class AudioCodec
{
    /// <summary>
    /// Gets the AAC (AAC-LC) audio codec.
    /// </summary>
    public static AudioCodec AAC { get; } = new AACImpl();

    /// <summary>
    /// Gets the HE-AAC audio codec.
    /// </summary>
    public static AudioCodec HEAAC { get; } = new HEAACImpl();

    /// <summary>
    /// Gets the MP2 audio codec.
    /// </summary>
    public static AudioCodec MP2 { get; } = new MP2Impl();

    /// <summary>
    /// Gets the MP3 audio codec.
    /// </summary>
    public static AudioCodec MP3 { get; } = new MP3Impl();

    /// <summary>
    /// Gets the Vorbis audio codec.
    /// </summary>
    public static AudioCodec Vorbis { get; } = new VorbisImpl();

    /// <summary>
    /// Gets the Opus audio codec.
    /// </summary>
    public static AudioCodec Opus { get; } = new OpusImpl();
}
