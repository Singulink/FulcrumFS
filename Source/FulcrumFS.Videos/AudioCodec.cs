namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for audio codecs used in video processing.
/// </summary>
public abstract class AudioCodec
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AudioCodec"/> class.
    /// Private constructor to prevent external inheritance.
    /// </summary>
    private AudioCodec()
    {
    }

    /// <summary>
    /// Gets the AAC (Advanced Audio Coding) audio codec, supports encoding.
    /// </summary>
    public static AudioCodec AAC { get; } = new AACImpl();

    /// <summary>
    /// Gets the MP2 audio codec, does not support encoding.
    /// </summary>
    public static AudioCodec MP2 { get; } = new MP2Impl();

    /// <summary>
    /// Gets the MP3 audio codec, does not support encoding.
    /// </summary>
    public static AudioCodec MP3 { get; } = new MP3Impl();

    /// <summary>
    /// Gets the Vorbis audio codec, does not support encoding.
    /// </summary>
    public static AudioCodec Vorbis { get; } = new VorbisImpl();

    /// <summary>
    /// Gets the Opus audio codec, does not support encoding.
    /// </summary>
    public static AudioCodec Opus { get; } = new OpusImpl();

    /// <summary>
    /// Gets a value indicating whether this codec supports encoding, as some codecs only support decoding.
    /// </summary>
    public virtual bool SupportsEncoding => false;

    /// <summary>
    /// Gets the name of the codec as used in ffprobe output (codec_name).
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the name of the profile as used in ffprobe output (profile), or <see langword="null" /> if unspecified.
    /// </summary>
    public virtual string? Profile => null;

    private sealed class AACImpl : AudioCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "aac";
        public override string? Profile => "LC";
    }

    private sealed class HEAACImpl : AudioCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "aac";
        public override string? Profile => "HE-AAC";
    }

    private sealed class MP2Impl : AudioCodec
    {
        public override string Name => "mp2";
    }

    private sealed class MP3Impl : AudioCodec
    {
        public override string Name => "mp3";
    }

    private sealed class VorbisImpl : AudioCodec
    {
        public override string Name => "vorbis";
    }

    private sealed class OpusImpl : AudioCodec
    {
        public override string Name => "opus";
    }
}
