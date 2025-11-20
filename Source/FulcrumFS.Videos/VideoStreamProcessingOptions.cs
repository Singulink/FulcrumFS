using Singulink.Enums;

namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents options for processing a video stream.
/// </summary>
public class VideoStreamProcessingOptions
{
    /// <summary>
    /// Gets or initializes the allowable result video codecs.
    /// Any streams of the video not matching one of these codecs will be re-encoded to use one of them.
    /// Video streams already using one of these codecs may be copied without re-encoding, depending on <see cref="ReencodeBehavior" />.
    /// When video streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Providing an empty list is not allowed.
    /// Default is a list containing <see cref="VideoCodec.H264" />.
    /// </summary>
    public IReadOnlyList<VideoCodec> ResultCodecs
    {
        get;
        init
        {
            IReadOnlyList<VideoCodec> result = [.. value];

            if (!result.Any())
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Count)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            if (!result[0].SupportsEncoding)
                throw new ArgumentException("The first codec in the list must support encoding.", nameof(value));

            field = result;
        }
    } = [VideoCodec.H264];

    /// <summary>
    /// Gets or initializes the behavior for re-encoding the video stream.
    /// Default is <see cref="ReencodeBehavior.Always" />.
    /// </summary>
    public ReencodeBehavior ReencodeBehavior
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ReencodeBehavior.Always;

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata from this stream.
    /// If set to <see langword="null" />, metadata preservation is undefined, and it may only be partially preserved.
    /// Note: if stripping metadata is enabled globally, it will be removed regardless.
    /// Default is <see langword="false" />.
    /// </summary>
    public bool? StripMetadata { get; init; } = false;

    /// <summary>
    /// Gets or initializes the quality to use for video encoding.
    /// Default is <see cref="VideoQuality.Medium" />.
    /// </summary>
    public VideoQuality Quality
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = VideoQuality.Medium;

    /// <summary>
    /// Gets or initializes the maximum bits per channel to use for video encoding.
    /// Default is <see cref="BitsPerChannel.Bits8" />.
    /// </summary>
    public BitsPerChannel MaximumBitsPerChannel
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = BitsPerChannel.Bits8;

    /// <summary>
    /// Gets or initializes the maximum chroma subsampling to use for video encoding.
    /// Default is <see cref="ChromaSubsampling.Subsampling420" />.
    /// </summary>
    public ChromaSubsampling MaximumChromaSubsampling
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ChromaSubsampling.Subsampling420;

    /// <summary>
    /// Gets or initializes the compression level to use for video encoding.
    /// Note: does not affect quality, only affects file size and encoding speed trade-off.
    /// Default is <see cref="VideoCompressionLevel.Medium" />.
    /// </summary>
    public VideoCompressionLevel CompressionPreference
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = VideoCompressionLevel.Medium;

    /// <summary>
    /// Gets or initializes the options for resizing the video.
    /// If set to <see langword="null" />, the video will not be resized.
    /// Default is <see langword="null" />.
    /// </summary>
    public VideoResizeOptions? ResizeOptions { get; init; }
}
