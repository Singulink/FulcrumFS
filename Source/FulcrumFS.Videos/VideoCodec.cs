namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for video codecs used in video processing.
/// </summary>
public abstract class VideoCodec
{
    /// <summary>
    /// Gets the H.262 (MPEG-2 Part 2) video codec.
    /// </summary>
    public static VideoCodec H262 { get; } = new H262Impl();

    /// <summary>
    /// Gets the H.263 video codec.
    /// </summary>
    public static VideoCodec H263 { get; } = new H263Impl();

    /// <summary>
    /// Gets the H.263+ video codec.
    /// </summary>
    public static VideoCodec H263Plus { get; } = new H263PlusImpl();

    /// <summary>
    /// Gets the H.264 (AVC) video codec.
    /// </summary>
    public static VideoCodec H264 { get; } = new H264Impl();

    /// <summary>
    /// Gets the H.265 (HEVC) video codec.
    /// </summary>
    public static VideoCodec H265 { get; } = new H265Impl();

    /// <summary>
    /// Gets the MPEG-1 (Part 2) video codec.
    /// </summary>
    public static VideoCodec Mpeg1 { get; } = new Mpeg1Impl();

    /// <inheritdoc cref="H262" />
    public static VideoCodec Mpeg2 => H262;

    /// <summary>
    /// Gets the MPEG-4 (Part 2) video codec.
    /// </summary>
    public static VideoCodec Mpeg4 { get; } = new Mpeg4Impl();

    /// <inheritdoc cref="H264" />
    public static VideoCodec AVC => H264;

    /// <inheritdoc cref="H265" />
    public static VideoCodec HEVC => H265;

    /// <summary>
    /// Gets the VP8 video codec.
    /// </summary>
    public static VideoCodec VP8 { get; } = new VP8Impl();

    /// <summary>
    /// Gets the VP9 video codec.
    /// </summary>
    public static VideoCodec VP9 { get; } = new VP9Impl();

    /// <summary>
    /// Gets the AV1 video codec.
    /// </summary>
    public static VideoCodec AV1 { get; } = new AV1Impl();
}
