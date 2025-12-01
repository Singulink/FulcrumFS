using System.Diagnostics.CodeAnalysis;
using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for processing an audio stream.
/// </summary>
public sealed class AudioStreamProcessingOptions
{
    // Helper fields to support our copy constructor:
    private readonly bool _storeWithoutCopying;
    private bool _isResultCodecsInitialized;
    private bool _isReencodeBehaviorInitialized;
    private bool _isStripMetadataInitialized;
    private bool _isQualityInitialized;
    private bool _isMaxChannelsInitialized;
    private bool _isSampleRateInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioStreamProcessingOptions"/> class - this constructor is the copy constructor.
    /// </summary>
    public AudioStreamProcessingOptions(AudioStreamProcessingOptions baseConfig)
    {
        _storeWithoutCopying = true;
        if (baseConfig._isResultCodecsInitialized) ResultCodecs = baseConfig.ResultCodecs;
        if (baseConfig._isReencodeBehaviorInitialized) ReencodeBehavior = baseConfig.ReencodeBehavior;
        if (baseConfig._isStripMetadataInitialized) StripMetadata = baseConfig.StripMetadata;
        if (baseConfig._isQualityInitialized) Quality = baseConfig.Quality;
        if (baseConfig._isMaxChannelsInitialized) MaxChannels = baseConfig.MaxChannels;
        if (baseConfig._isSampleRateInitialized) SampleRate = baseConfig.SampleRate;
        _storeWithoutCopying = false;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioStreamProcessingOptions"/> class.
    /// Note: this creates an incomplete object that cannot be used for reading properties until it is assigned to
    /// <see cref="VideoProcessor.AudioStreamOptions" /> (and then accessed by the reference stored there).
    /// It allows you to use object initializer syntax to adjust any combination of properties when creating the options by writing code like
    /// <c>new VideoProcessor(VideoProcessors.Preserve) { AudioStreamOptions = new() { Quality = AudioQuality.High } }</c>.
    /// </summary>
    public AudioStreamProcessingOptions()
    {
    }

    // Internal assignment constructor used for combining existing properties on a base config with overrides from an assigned config.
    // Note: we assume that every property is available on either the baseConfig or the overrideConfig - the caller must ensure this.
    internal AudioStreamProcessingOptions(AudioStreamProcessingOptions? baseConfig, AudioStreamProcessingOptions overrideConfig)
    {
        _storeWithoutCopying = true;
        ResultCodecs = overrideConfig._isResultCodecsInitialized ? overrideConfig.ResultCodecs : baseConfig!.ResultCodecs;
        ReencodeBehavior = overrideConfig._isReencodeBehaviorInitialized ? overrideConfig.ReencodeBehavior : baseConfig!.ReencodeBehavior;
        StripMetadata = overrideConfig._isStripMetadataInitialized ? overrideConfig.StripMetadata : baseConfig!.StripMetadata;
        Quality = overrideConfig._isQualityInitialized ? overrideConfig.Quality : baseConfig!.Quality;
        MaxChannels = overrideConfig._isMaxChannelsInitialized ? overrideConfig.MaxChannels : baseConfig!.MaxChannels;
        SampleRate = overrideConfig._isSampleRateInitialized ? overrideConfig.SampleRate : baseConfig!.SampleRate;
        _storeWithoutCopying = false;
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamProcessingOptions"/> that always re-encodes to a standardized AAC format, and preserves metadata.
    /// </summary>
    public static AudioStreamProcessingOptions StandardizedAAC { get; } = new AudioStreamProcessingOptions()
    {
        ResultCodecs = [AudioCodec.AAC],
        ReencodeBehavior = VideoReencodeBehavior.Always,
        StripMetadata = false,
        Quality = AudioQuality.Medium,
        MaxChannels = AudioChannels.Stereo,
        SampleRate = AudioSampleRate.Hz48000,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamProcessingOptions"/> that always preserves audio streams without re-encoding, and preserves
    /// metadata.
    /// </summary>
    public static AudioStreamProcessingOptions Preserve { get; } = new AudioStreamProcessingOptions()
    {
        ResultCodecs = AudioCodec.AllSourceCodecs,
        ReencodeBehavior = VideoReencodeBehavior.AvoidReencoding,
        StripMetadata = false,
        Quality = AudioQuality.Medium,
        MaxChannels = AudioChannels.Preserve,
        SampleRate = AudioSampleRate.Preserve,
    };

    /// <summary>
    /// Gets or initializes the allowable result audio codecs.
    /// Any streams of the audio not matching one of these codecs will be re-encoded to use one of them.
    /// Audio streams already using one of these codecs may be copied without re-encoding, depending on <see cref="ReencodeBehavior" />.
    /// When audio streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Providing an empty list is not allowed.
    /// </summary>
    [field: AllowNull]
    public IReadOnlyList<AudioCodec> ResultCodecs
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isResultCodecsInitialized);
            return field;
        }
        init
        {
            IReadOnlyList<AudioCodec> result;
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
    /// Gets or initializes the behavior for re-encoding the audio stream.
    /// </summary>
    public VideoReencodeBehavior ReencodeBehavior
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
    /// Gets or initializes a value indicating whether to strip metadata from this stream.
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
    /// Gets or initializes the quality to use for audio encoding.
    /// Default is <see cref="AudioQuality.Medium" />.
    /// </summary>
    public AudioQuality Quality
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
    /// Gets or initializes the maximum number of audio channels to include in the output stream.
    /// Note: if the source has more channels than this value, channels will be downmixed.
    /// Default is <see cref="AudioChannels.Stereo" />.
    /// </summary>
    public AudioChannels MaxChannels
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaxChannelsInitialized);
            return field;
        }
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
            _isMaxChannelsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum sample rate (in Hz) for the audio stream (any rates above this will be downsampled).
    /// Default is <see cref="AudioSampleRate.Hz48000" />.
    /// </summary>
    public AudioSampleRate SampleRate
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isSampleRateInitialized);
            return field;
        }
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
            _isSampleRateInitialized = true;
        }
    }
}
