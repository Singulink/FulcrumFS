namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for processing a video stream.
/// </summary>
public class VideoStreamProcessingOptions
{
    /// <summary>
    /// Gets the options that indicate to copy the codec without re-encoding.
    /// </summary>
    public static VideoStreamProcessingOptions CopyCodecOptions { get; } = new();

    /// <summary>
    /// Gets the options that indicate to remove this stream.
    /// </summary>
    public static VideoStreamProcessingOptions RemoveStreamOptions { get; } = new() { ShouldRemove = true };

    /// <summary>
    /// Gets or initializes the result video codec for this mapping, or null for copy.
    /// Note: if using the copy codec, options that require re-encoding will be ignored.
    /// </summary>
    public VideoCodec? ResultCodec { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to remove this stream.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ShouldRemove { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to copy the stream without re-encoding if it makes the final file smaller overall than otherwise.
    /// Default is false.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ShouldCopyIfLarger { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata from this stream.
    /// If unset, metadata preservation is undefined, and it may only be partially preserved.
    /// Note: if stripping metadata is enabled globally, it will be removed regardless.
    /// </summary>
    public bool? StripMetadata { get; init; }

    /// <summary>
    /// Gets or intializes a value indicating how to re-order this stream.
    /// If set, adjusts the index of this stream within its stream type (video) by the specified amount with respect to other streams of the same type; if
    /// movement exceeds the maximum amount possible, it is clamped.
    /// The re-ordering is performed after any streams with <see cref="ShouldRemove" /> have been removed, and is performed in order for each stream as per the
    /// original order.
    /// </summary>
    public int? IndexAdjustmentWithinStreamType { get; init; }

    /// <summary>
    /// Gets or initializes the CRF (constant rate factor) for the video encoding.
    /// CRF values range from 0 to 51 for 8-bit formats, or 0 to 63 for 10 bit formats - a lower value is higher quality.
    /// Currently only supported for H.264 (default is 23) and H.265 (default is 28, visually similar to 23 in H.264, but about half the file size) - ignored
    /// for unsupported codecs (including the copy codec).
    /// A CRF value of 0 is lossless for supported video profiles (otherwise, it is just the best possible); however, generally around 16 for H.264 it is
    /// visually lossless (depending on the particular file).
    /// The resulting quality also depends on if a maximum bitrate is set though.
    /// </summary>
    public int? ConstantRateFactor { get; init; }

    /// <summary>
    /// Gets or initializes maximum bitrate of the stream (in bits / second).
    /// Note: some video codecs do not support a maximum bitrate in the traditional sense (e.g., H.264 without 2-pass encoding), so for these codecs it will
    /// instead conceptually correspond to a target / average maximum bitrate.
    /// </summary>
    public int? MaximumBitRate { get; init; }

    /// <summary>
    /// Gets or initializes the tuning options for video encoding.
    /// Ignored for codecs that do not support tuning.
    /// </summary>
    public VideoTune? VideoTune { get; init; }

    /// <summary>
    /// Gets or initializes the H.264 profile to use when encoding with the H.264 codec.
    /// </summary>
    public H264Profile? H264Profile { get; init; }

    /// <summary>
    /// Gets or initializes the H.265 profile to use when encoding with the H.265 codec.
    /// </summary>
    public H265Profile? H265Profile { get; init; }

    /// <summary>
    /// Gets or initializes the compression preset to use for video encoding.
    /// </summary>
    public VideoCompressionPreset? CompressionPreset { get; init; }

    /// <summary>
    /// Gets or initializes the bit depth to use for video encoding - default is 8.
    /// If set to an unsupported bit depth, the value will be ignored and defaulted instead.
    /// Note: this option might be overridden if a profile is selected that requires a specific bit depth.
    /// </summary>
    public int? BitDepth { get; init; }

    /// <summary>
    /// Gets or initializes the options for resizing the video. If set to <see langword="null"/>, the video will not be resized.
    /// </summary>
    public VideoResizeOptions? ResizeOptions { get; init; }
}
