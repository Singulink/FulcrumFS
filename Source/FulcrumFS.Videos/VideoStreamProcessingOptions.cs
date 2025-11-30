using System.Diagnostics.CodeAnalysis;
using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for processing a video stream.
/// </summary>
public sealed class VideoStreamProcessingOptions
{
    // Helper fields to support our copy constructor:
    private readonly bool _storeWithoutCopying;
    private bool _isResultCodecsInitialized;
    private bool _isReencodeBehaviorInitialized;
    private bool _isStripMetadataInitialized;
    private bool _isQualityInitialized;
    private bool _isMaximumBitsPerChannelInitialized;
    private bool _isMaximumChromaSubsamplingInitialized;
    private bool _isCompressionPreferenceInitialized;
    private bool _isResizeOptionsInitialized;
    private bool _isFpsOptionsInitialized;
    private bool _isRemapHDRToSDRInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoStreamProcessingOptions"/> class - this constructor is the copy constructor.
    /// </summary>
    public VideoStreamProcessingOptions(VideoStreamProcessingOptions baseConfig)
    {
        _storeWithoutCopying = true;
        if (baseConfig._isResultCodecsInitialized) ResultCodecs = baseConfig.ResultCodecs;
        if (baseConfig._isReencodeBehaviorInitialized) ReencodeBehavior = baseConfig.ReencodeBehavior;
        if (baseConfig._isStripMetadataInitialized) StripMetadata = baseConfig.StripMetadata;
        if (baseConfig._isQualityInitialized) Quality = baseConfig.Quality;
        if (baseConfig._isMaximumBitsPerChannelInitialized) MaximumBitsPerChannel = baseConfig.MaximumBitsPerChannel;
        if (baseConfig._isMaximumChromaSubsamplingInitialized) MaximumChromaSubsampling = baseConfig.MaximumChromaSubsampling;
        if (baseConfig._isCompressionPreferenceInitialized) CompressionPreference = baseConfig.CompressionPreference;
        if (baseConfig._isResizeOptionsInitialized) ResizeOptions = baseConfig.ResizeOptions;
        if (baseConfig._isFpsOptionsInitialized) FpsOptions = baseConfig.FpsOptions;
        if (baseConfig._isRemapHDRToSDRInitialized) RemapHDRToSDR = baseConfig.RemapHDRToSDR;
        _storeWithoutCopying = false;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoStreamProcessingOptions"/> class.
    /// Note: this creates an incomplete object that cannot be used for reading properties until it is assigned to
    /// <see cref="VideoProcessor.VideoStreamOptions" /> (and then accessed by the reference stored there).
    /// It allows you to use object initializer syntax to adjust any combination of properties when creating the options by writing code like
    /// <c>new VideoProcessor(VideoProcessor.Preserve) { VideoStreamOptions = new() { Quality = VideoQuality.High } }</c>.
    /// </summary>
    public VideoStreamProcessingOptions()
    {
    }

    // Internal assignment constructor used for combining existing properties on a base config with overrides from an assigned config.
    // Note: we assume that every property is available on either the baseConfig or the overrideConfig - the caller must ensure this.
    internal VideoStreamProcessingOptions(VideoStreamProcessingOptions? baseConfig, VideoStreamProcessingOptions overrideConfig)
    {
        _storeWithoutCopying = true;
        ResultCodecs = overrideConfig._isResultCodecsInitialized ? overrideConfig.ResultCodecs : baseConfig!.ResultCodecs;
        ReencodeBehavior = overrideConfig._isReencodeBehaviorInitialized ? overrideConfig.ReencodeBehavior : baseConfig!.ReencodeBehavior;
        StripMetadata = overrideConfig._isStripMetadataInitialized ? overrideConfig.StripMetadata : baseConfig!.StripMetadata;
        Quality = overrideConfig._isQualityInitialized ? overrideConfig.Quality : baseConfig!.Quality;
        MaximumBitsPerChannel = overrideConfig._isMaximumBitsPerChannelInitialized ? overrideConfig.MaximumBitsPerChannel : baseConfig!.MaximumBitsPerChannel;
        MaximumChromaSubsampling
            = overrideConfig._isMaximumChromaSubsamplingInitialized ? overrideConfig.MaximumChromaSubsampling : baseConfig!.MaximumChromaSubsampling;
        CompressionPreference = overrideConfig._isCompressionPreferenceInitialized ? overrideConfig.CompressionPreference : baseConfig!.CompressionPreference;
        ResizeOptions = overrideConfig._isResizeOptionsInitialized ? overrideConfig.ResizeOptions : baseConfig!.ResizeOptions;
        FpsOptions = overrideConfig._isFpsOptionsInitialized ? overrideConfig.FpsOptions : baseConfig!.FpsOptions;
        RemapHDRToSDR = overrideConfig._isRemapHDRToSDRInitialized ? overrideConfig.RemapHDRToSDR : baseConfig!.RemapHDRToSDR;
        _storeWithoutCopying = false;
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoStreamProcessingOptions"/> that always re-encodes to a standardized H.264 format (60fps max, 8 bits per
    /// channel, 4:2:0 chroma subsampling), and preserves metadata.
    /// </summary>
    public static VideoStreamProcessingOptions StandardizedH264 { get; } = new VideoStreamProcessingOptions()
    {
        ResultCodecs = [VideoCodec.H264],
        ReencodeBehavior = ReencodeBehavior.Always,
        StripMetadata = false,
        Quality = VideoQuality.Medium,
        MaximumBitsPerChannel = BitsPerChannel.Bits8,
        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
        CompressionPreference = VideoCompressionLevel.Medium,
        ResizeOptions = null,
        FpsOptions = new(VideoFpsLimitMode.DivideByInteger, (60, 1)),
        RemapHDRToSDR = true,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoStreamProcessingOptions"/> that always preserves video streams without re-encoding, and preserves
    /// metadata.
    /// </summary>
    public static VideoStreamProcessingOptions Preserve { get; } = new VideoStreamProcessingOptions()
    {
        ResultCodecs = VideoCodec.AllSourceCodecs,
        ReencodeBehavior = ReencodeBehavior.IfNeeded,
        StripMetadata = false,
        Quality = VideoQuality.Medium,
        MaximumBitsPerChannel = BitsPerChannel.Preserve,
        MaximumChromaSubsampling = ChromaSubsampling.Preserve,
        CompressionPreference = VideoCompressionLevel.Medium,
        ResizeOptions = null,
        FpsOptions = null,
        RemapHDRToSDR = false,
    };

    /// <summary>
    /// Gets or initializes the allowable result video codecs.
    /// Any streams of the video not matching one of these codecs will be re-encoded to use one of them.
    /// Video streams already using one of these codecs may be copied without re-encoding, depending on <see cref="ReencodeBehavior" />.
    /// When video streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Providing an empty list is not allowed.
    /// </summary>
    [field: AllowNull]
    public IReadOnlyList<VideoCodec> ResultCodecs
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isResultCodecsInitialized);
            return field;
        }
        init
        {
            IReadOnlyList<VideoCodec> result;
            if (!_storeWithoutCopying)
            {
                result = [.. value];

                if (!result.Any())
                    throw new ArgumentException("Codecs cannot be empty.", nameof(value));

                if (result.Any((x) => x is null))
                    throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

                if (result.Distinct().Count() != result.Count)
                    throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

                if (!result[0].SupportsEncoding)
                    throw new ArgumentException("The first codec in the list must support encoding.", nameof(value));
            }
            else
            {
                result = value;
            }

            field = result;
            _isResultCodecsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the behavior for re-encoding the video stream.
    /// </summary>
    public ReencodeBehavior ReencodeBehavior
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isReencodeBehaviorInitialized);
            return field;
        }
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
            _isReencodeBehaviorInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata from these streams.
    /// Note: if stripping metadata is enabled globally, it will be removed regardless.
    /// Note: metadata copying is subject to the considerations described in <see cref="VideoProcessor.StripMetadata" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool StripMetadata
#pragma warning restore SA1623 // Property summary documentation should match accessors
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isStripMetadataInitialized);
            return field;
        }
        init
        {
            field = value;
            _isStripMetadataInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the quality to use for video encoding.
    /// Default is <see cref="VideoQuality.Medium" />.
    /// </summary>
    public VideoQuality Quality
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isQualityInitialized);
            return field;
        }
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
            _isQualityInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum bits per channel to use for video encoding.
    /// Default is <see cref="BitsPerChannel.Bits8" />.
    /// </summary>
    public BitsPerChannel MaximumBitsPerChannel
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaximumBitsPerChannelInitialized);
            return field;
        }
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
            _isMaximumBitsPerChannelInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum chroma subsampling to use for video encoding.
    /// Default is <see cref="ChromaSubsampling.Subsampling420" />.
    /// </summary>
    public ChromaSubsampling MaximumChromaSubsampling
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaximumChromaSubsamplingInitialized);
            return field;
        }
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
            _isMaximumChromaSubsamplingInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the compression level to use for video encoding.
    /// Note: does not affect quality, only affects file size and encoding speed trade-off.
    /// Default is <see cref="VideoCompressionLevel.Medium" />.
    /// </summary>
    public VideoCompressionLevel CompressionPreference
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isCompressionPreferenceInitialized);
            return field;
        }
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
            _isCompressionPreferenceInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the options for resizing the video.
    /// If set to <see langword="null" />, the video will not be resized.
    /// Default is <see langword="null" />.
    /// </summary>
    public VideoResizeOptions? ResizeOptions
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isResizeOptionsInitialized);
            return field;
        }
        init
        {
            field = value;
            _isResizeOptionsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the options for limiting the frames per second (FPS) of the video.
    /// If set to <see langword="null" />, the video will not be resampled.
    /// </summary>
    public VideoFpsOptions? FpsOptions
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isFpsOptionsInitialized);
            return field;
        }
        init
        {
            field = value;
            _isFpsOptionsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating whether to remap HDR to SDR for any HDR streams.
    /// Note: this uses a basic tone-mapping algorithm and may not produce optimal results for all content.
    /// Note: if the video is re-encoded, it will always be converted to SDR regardless of this setting, as currently we do not support encoding to HDR.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool RemapHDRToSDR
#pragma warning restore SA1623 // Property summary documentation should match accessors
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isRemapHDRToSDRInitialized);
            return field;
        }
        init
        {
            field = value;
            _isRemapHDRToSDRInitialized = true;
        }
    }
}
