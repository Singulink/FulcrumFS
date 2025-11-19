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
    /// Gets the H.265 (HEVC) video codec, supports encoding.
    /// </summary>
    public static VideoCodec H265 { get; } = new H265Impl();

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
    /// Gets a value indicating whether this codec supports encoding, as some codecs only support decoding.
    /// </summary>
    public virtual bool SupportsEncoding => false;

    /// <summary>
    /// Gets the name of the codec as used in ffprobe output (codec_name).
    /// </summary>
    public abstract string Name { get; }

    private sealed class H262Impl : VideoCodec
    {
        public override string Name => "mpeg2video";
    }

    private sealed class H263Impl : VideoCodec
    {
        public override string Name => "h263";
    }

    private sealed class H264Impl : VideoCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "h264";
    }

    private sealed class H265Impl : VideoCodec
    {
        public override bool SupportsEncoding => true;
        public override string Name => "hevc";
    }

    private sealed class H266Impl : VideoCodec
    {
        public override string Name => "vvc";
    }

    private sealed class Mpeg1Impl : VideoCodec
    {
        public override string Name => "mpeg1video";
    }

    private sealed class Mpeg4Impl : VideoCodec
    {
        public override string Name => "mpeg4";
    }

    private sealed class VP8Impl : VideoCodec
    {
        public override string Name => "vp8";
    }

    private sealed class VP9Impl : VideoCodec
    {
        public override string Name => "vp9";
    }

    private sealed class AV1Impl : VideoCodec
    {
        public override string Name => "av1";
    }
}
