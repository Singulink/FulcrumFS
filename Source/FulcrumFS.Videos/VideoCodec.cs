namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for video codecs used in video processing.
/// </summary>
public abstract class VideoCodec
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoCodec"/> class.
    /// Private constructor to prevent external inheritance.
    /// </summary>
    private VideoCodec()
    {
    }

    /// <summary>
    /// Gets the H.262 (MPEG-2 Part 2) video codec, does not support encoding.
    /// </summary>
    public static VideoCodec H262 { get; } = new H262Impl();

    /// <summary>
    /// Gets the H.263 video codec, does not support encoding.
    /// </summary>
    public static VideoCodec H263 { get; } = new H263Impl();

    /// <summary>
    /// Gets the H.264 (AVC) video codec, supports encoding.
    /// </summary>
    public static VideoCodec H264 { get; } = new H264Impl();

    /// <summary>
    /// <para>
    /// Gets the H.265 (HEVC) video codec, supports encoding.</para>
    /// <para>
    /// Note: this instance corresponds only to the 'hvc1' tag (which is more compatible), to refer to the 'hev1' tag / others, use <see cref="H265AnyTag" />.
    /// </para><para>
    /// Note: a video file can be lossly converted between to this tag without re-encoding, but would require remuxing.</para>
    /// <para>
    /// Note: these tags are only relevant when muxing into MP4 container formats - this instance won't correspond to any stream outside of an mp4 container.
    /// </para>
    /// </summary>
    public static VideoCodec H265 { get; } = new H265Impl();

    /// <summary>
    /// <para>
    /// Gets the H.265 (HEVC) video codec, supports encoding.</para>
    /// <para>
    /// Note: this instance corresponds to any hevc tag (e.g., 'hev1' or 'hvc1'), and does not change tag when used as output.</para>
    /// <para>
    /// Note: these tags are only relevant when muxing into MP4 container formats - this instance will match all H.265 streams in any container.</para>
    /// </summary>
    public static VideoCodec H265AnyTag { get; } = new H265AnyTagImpl();

    /// <summary>
    /// Gets the H.266 (VVC) video codec, does not support encoding.
    /// </summary>
    public static VideoCodec H266 { get; } = new H266Impl();

    /// <summary>
    /// Gets the MPEG-1 (Part 2) video codec, does not support encoding.
    /// </summary>
    public static VideoCodec Mpeg1 { get; } = new Mpeg1Impl();

    /// <inheritdoc cref="H262" />
    public static VideoCodec Mpeg2 => H262;

    /// <summary>
    /// Gets the MPEG-4 (Part 2) video codec, does not support encoding.
    /// </summary>
    public static VideoCodec Mpeg4 { get; } = new Mpeg4Impl();

    /// <inheritdoc cref="H264" />
    public static VideoCodec AVC => H264;

    /// <inheritdoc cref="H265" />
    public static VideoCodec HEVC => H265;

    /// <inheritdoc cref="H265AnyTag" />
    public static VideoCodec HEVCAnyTag => H265AnyTag;

    /// <inheritdoc cref="H266" />
    public static VideoCodec VVC => H266;

    /// <summary>
    /// Gets the VP8 video codec, does not support encoding.
    /// </summary>
    public static VideoCodec VP8 { get; } = new VP8Impl();

    /// <summary>
    /// Gets the VP9 video codec, does not support encoding.
    /// </summary>
    public static VideoCodec VP9 { get; } = new VP9Impl();

    /// <summary>
    /// Gets the AV1 video codec, does not support encoding.
    /// </summary>
    public static VideoCodec AV1 { get; } = new AV1Impl();

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
    public virtual bool SupportsEncoding => false;

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
    internal virtual string? TagName => null;

    private sealed class H262Impl : VideoCodec
    {
        public override string Name => "mpeg2video";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsMpeg2VideoDecoder;
    }

    private sealed class H263Impl : VideoCodec
    {
        public override string Name => "h263";
        internal override string WritableFileExtension => ".3gp";
        internal override bool SupportsMP4Muxing => false;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsH263Decoder;
    }

    private sealed class H264Impl : VideoCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "h264";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsH264Decoder;
    }

    private sealed class H265Impl : VideoCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "hevc";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsHEVCDecoder;
        internal override string? TagName => "hvc1";
    }

    private sealed class H265AnyTagImpl : VideoCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "hevc";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsHEVCDecoder;
    }

    private sealed class H266Impl : VideoCodec
    {
        public override string Name => "vvc";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsVVCDecoder;
    }

    private sealed class Mpeg1Impl : VideoCodec
    {
        public override string Name => "mpeg1video";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsMpeg1VideoDecoder;
    }

    private sealed class Mpeg4Impl : VideoCodec
    {
        public override string Name => "mpeg4";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsMpeg4Decoder;
    }

    private sealed class VP8Impl : VideoCodec
    {
        public override string Name => "vp8";
        internal override string WritableFileExtension => ".webm";
        internal override bool SupportsMP4Muxing => false;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsVP8Decoder;
    }

    private sealed class VP9Impl : VideoCodec
    {
        public override string Name => "vp9";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsVP9Decoder;
    }

    private sealed class AV1Impl : VideoCodec
    {
        public override string Name => "av1";
        internal override string WritableFileExtension => ".mp4";
        internal override bool SupportsMP4Muxing => true;
        internal override bool HasSupportedDecoder => FFprobeUtils.Configuration.SupportsAV1Decoder;
    }
}
