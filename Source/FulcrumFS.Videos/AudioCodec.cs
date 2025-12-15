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
    /// Gets the AAC (AAC-LC) audio codec, supports encoding.
    /// </summary>
    public static AudioCodec AAC { get; } = new AACImpl();

    /// <summary>
    /// Gets the HE-AAC audio codec, does not support encoding.
    /// </summary>
    public static AudioCodec HEAAC { get; } = new HEAACImpl();

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
    /// Gets a list of all supported audio codecs (with encodable ones first).
    /// </summary>
    public static IReadOnlyList<AudioCodec> AllSourceCodecs { get; } =
    [
        AAC,
        HEAAC,
        MP2,
        MP3,
        Vorbis,
        Opus,
    ];

    /// <summary>
    /// Gets a list of all supported audio codecs, that have encoding support (<see cref="SupportsEncoding" />).
    /// </summary>
    public static IReadOnlyList<AudioCodec> AllResultCodecs { get; } =
    [
        AAC,
    ];

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

    // Internal helper to get a file extension suitable for writing an audio stream in a file with this codec - does not necessarily correspond to a
    // MediaContainerFormat with writing support:
    internal abstract string WritableFileExtension { get; }

    // Internal helper to get whether this codec supports muxing into mp4 container:
    internal abstract bool SupportsMP4Muxing { get; }

    // Internal helper to get whether this codec has a supported decoder in the current ffmpeg configuration:
    internal abstract bool HasSupportedDecoder { get; }

    private sealed class AACImpl : AudioCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "aac";
        public override string? Profile => "LC";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsAACDecoder;
    }

    private sealed class HEAACImpl : AudioCodec
    {
        public override string Name => "aac";
        public override string? Profile => "HE-AAC";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsAACDecoder;
    }

    private sealed class MP2Impl : AudioCodec
    {
        public override string Name => "mp2";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsMP2Decoder;
    }

    private sealed class MP3Impl : AudioCodec
    {
        public override string Name => "mp3";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsMP3Decoder;
    }

    private sealed class VorbisImpl : AudioCodec
    {
        public override string Name => "vorbis";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsVorbisDecoder;
    }

    private sealed class OpusImpl : AudioCodec
    {
        public override string Name => "opus";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsOpusDecoder;
    }
}
