namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for video codecs used in video processing.
/// </summary>
public abstract class VideoCodec
{
    private VideoCodec()
    {
    }

    /// <summary>
    /// Gets the H.262 (MPEG-2 Part 2) video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec H262 { get; } = new Impl(
        name: "mpeg2video",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsMpeg2VideoDecoder);

    /// <summary>
    /// Gets the H.263 video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec H263 { get; } = new Impl(
        name: "h263",
        writableFileExtension: ".3gp",
        supportsMP4Muxing: false,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsH263Decoder);

    /// <summary>
    /// Gets the H.264 (AVC) video codec. Supports encoding.
    /// </summary>
    public static VideoCodec H264 { get; } = new Impl(
        name: "h264",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: true,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsH264Decoder);

    /// <summary>
    /// <para>
    /// Gets the H.265 (HEVC) video codec. Supports encoding.</para>
    /// <para>
    /// Note: this instance corresponds only to the 'hvc1' tag (which is more compatible); to refer to the 'hev1' tag or others, use <see cref="H265AnyTag" />.
    /// </para><para>
    /// Note: a video file can be losslessly converted to this tag without re-encoding, but would require remuxing.</para>
    /// <para>
    /// Note: these tags are only relevant when muxing into MP4 container formats - this instance won't correspond to any stream outside of an mp4 container.
    /// </para>
    /// </summary>
    public static VideoCodec H265 { get; } = new Impl(
        name: "hevc",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: true,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsHEVCDecoder,
        tagName: "hvc1");

    /// <summary>
    /// <para>
    /// Gets the H.265 (HEVC) video codec. Supports encoding.</para>
    /// <para>
    /// Note: this instance corresponds to any hevc tag (e.g., 'hev1' or 'hvc1'), and does not change tag when used as output.</para>
    /// <para>
    /// Note: these tags are only relevant when muxing into MP4 container formats - this instance will match all H.265 streams in any container.</para>
    /// </summary>
    public static VideoCodec H265AnyTag { get; } = new Impl(
        name: "hevc",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: true,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsHEVCDecoder);

    /// <summary>
    /// Gets the H.266 (VVC) video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec H266 { get; } = new Impl(
        name: "vvc",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsVVCDecoder);

    /// <summary>
    /// Gets the MPEG-1 (Part 2) video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec Mpeg1 { get; } = new Impl(
        name: "mpeg1video",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsMpeg1VideoDecoder);

    /// <inheritdoc cref="H262" />
    public static VideoCodec Mpeg2 => H262;

    /// <summary>
    /// Gets the MPEG-4 (Part 2) video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec Mpeg4 { get; } = new Impl(
        name: "mpeg4",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsMpeg4Decoder);

    /// <inheritdoc cref="H264" />
    public static VideoCodec AVC => H264;

    /// <inheritdoc cref="H265" />
    public static VideoCodec HEVC => H265;

    /// <inheritdoc cref="H265AnyTag" />
    public static VideoCodec HEVCAnyTag => H265AnyTag;

    /// <inheritdoc cref="H266" />
    public static VideoCodec VVC => H266;

    /// <summary>
    /// Gets the VP8 video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec VP8 { get; } = new Impl(
        name: "vp8",
        writableFileExtension: ".webm",
        supportsMP4Muxing: false,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsVP8Decoder && FFprobeUtils.Configuration.SupportsLibVpxDecoder);

    /// <summary>
    /// Gets the VP9 video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec VP9 { get; } = new Impl(
        name: "vp9",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsVP9Decoder && FFprobeUtils.Configuration.SupportsLibVpxVp9Decoder);

    /// <summary>
    /// Gets the AV1 video codec. Does not support encoding.
    /// </summary>
    public static VideoCodec AV1 { get; } = new Impl(
        name: "av1",
        writableFileExtension: ".mp4",
        supportsMP4Muxing: true,
        supportsEncoding: false,
        hasSupportedDecoder: static () => FFprobeUtils.Configuration.SupportsAV1Decoder && FFprobeUtils.Configuration.SupportsLibDav1dDecoder);

    /// <summary>
    /// Gets a list of all supported video codecs (with encodable ones first).
    /// </summary>
    public static IReadOnlyList<VideoCodec> AllSourceCodecs { get; } =
    [
        H264,
        H265,
        H265AnyTag,
        H262,
        H263,
        H266,
        Mpeg1,
        Mpeg4,
        VP8,
        VP9,
        AV1,
    ];

    /// <summary>
    /// Gets a list of all supported video codecs, that have encoding support (<see cref="SupportsEncoding" />).
    /// </summary>
    public static IReadOnlyList<VideoCodec> AllResultCodecs { get; } =
    [
        H264,
        H265,
        H265AnyTag,
    ];

    /// <summary>
    /// Gets a value indicating whether this codec supports encoding, as some codecs only support decoding.
    /// </summary>
    public abstract bool SupportsEncoding { get; }

    /// <summary>
    /// Gets the name of the codec as used in ffprobe output (codec_name).
    /// </summary>
    public abstract string Name { get; }

    // Internal helper to get a file extension suitable for writing a video stream in a file with this codec - does not necessarily correspond to a
    // MediaContainerFormat with writing support:
    internal abstract string WritableFileExtension { get; }

    // Internal helper to get whether this codec supports muxing into mp4 container:
    internal abstract bool SupportsMP4Muxing { get; }

    // Internal helper to get whether this codec has a supported decoder in the current ffmpeg configuration:
    internal abstract bool HasSupportedDecoder { get; }

    // Internal helper to get the supported tag names that this codec can correspond to:
    internal abstract string? TagName { get; }

    private sealed class Impl(
        string name,
        string writableFileExtension,
        bool supportsMP4Muxing,
        bool supportsEncoding,
        Func<bool> hasSupportedDecoder,
        string? tagName = null) : VideoCodec
    {
        public override bool SupportsEncoding { get; } = supportsEncoding;

        public override string Name { get; } = name;

        internal override string WritableFileExtension { get; } = writableFileExtension;

        internal override bool SupportsMP4Muxing { get; } = supportsMP4Muxing;

        internal override bool HasSupportedDecoder => hasSupportedDecoder();

        internal override string? TagName { get; } = tagName;
    }
}
