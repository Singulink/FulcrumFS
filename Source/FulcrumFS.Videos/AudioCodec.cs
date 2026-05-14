namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for audio codecs used in video processing.
/// </summary>
public abstract class AudioCodec
{
    private AudioCodec()
    {
    }

    /// <summary>
    /// Gets the AAC (AAC-LC) audio codec. Supports encoding.
    /// </summary>
    public static AudioCodec AAC { get; } = new Impl(
        name: "aac",
        profile: "LC",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: true,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsAACDecoder);

    /// <summary>
    /// Gets the HE-AAC audio codec. Does not support encoding.
    /// </summary>
    public static AudioCodec HEAAC { get; } = new Impl(
        name: "aac",
        profile: "HE-AAC",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsAACDecoder);

    /// <summary>
    /// Gets the MP2 audio codec. Does not support encoding.
    /// </summary>
    public static AudioCodec MP2 { get; } = new Impl(
        name: "mp2",
        profile: null,
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsMP2Decoder);

    /// <summary>
    /// Gets the MP3 audio codec. Does not support encoding.
    /// </summary>
    public static AudioCodec MP3 { get; } = new Impl(
        name: "mp3",
        profile: null,
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsMP3Decoder);

    /// <summary>
    /// Gets the Vorbis audio codec. Does not support encoding.
    /// </summary>
    public static AudioCodec Vorbis { get; } = new Impl(
        name: "vorbis",
        profile: null,
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsVorbisDecoder);

    /// <summary>
    /// Gets the Opus audio codec. Does not support encoding.
    /// </summary>
    public static AudioCodec Opus { get; } = new Impl(
        name: "opus",
        profile: null,
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsOpusDecoder);

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
    public abstract bool SupportsEncoding { get; }

    /// <summary>
    /// Gets the name of the codec as used in ffprobe output (codec_name).
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the name of the profile as used in ffprobe output (profile), or <see langword="null" /> if unspecified.
    /// </summary>
    public abstract string? Profile { get; }

    // Internal helper to get a file extension suitable for writing an audio stream in a file with this codec - does not necessarily correspond to a
    // MediaContainerFormat with writing support:
    internal abstract string WritableFileExtension { get; }

    // Internal helper to get whether this codec supports muxing into mp4 container:
    internal abstract bool SupportsMP4Muxing { get; }

    // Internal helper to get whether this codec has a supported decoder in the current ffmpeg configuration:
    internal abstract bool HasSupportedDecoder { get; }

    private sealed class Impl(
        string name,
        string? profile,
        string writableFileExtension,
        bool supportsMP4Muxing,
        bool supportsEncoding,
        Func<bool> hasSupportedDecoder) : AudioCodec
    {
        public override bool SupportsEncoding { get; } = supportsEncoding;

        public override string Name { get; } = name;

        public override string? Profile { get; } = profile;

        internal override string WritableFileExtension { get; } = writableFileExtension;

        internal override bool SupportsMP4Muxing { get; } = supportsMP4Muxing;

        internal override bool HasSupportedDecoder => hasSupportedDecoder();
    }
}
