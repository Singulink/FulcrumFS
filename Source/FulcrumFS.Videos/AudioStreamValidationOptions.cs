namespace FulcrumFS.Videos;

/// <summary>
/// Options for validating audio streams during processing.
/// </summary>
public sealed class AudioStreamValidationOptions
{
    // Helper fields to support our copy constructor:
    private bool _isMaxStreamsInitialized;
    private bool _isMaxLengthInitialized;
    private bool _isMinStreamsInitialized;
    private bool _isMinLengthInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioStreamValidationOptions"/> class - this constructor is the copy constructor.
    /// </summary>
    public AudioStreamValidationOptions(AudioStreamValidationOptions baseConfig)
    {
        if (baseConfig._isMaxStreamsInitialized) MaxStreams = baseConfig.MaxStreams;
        if (baseConfig._isMaxLengthInitialized) MaxLength = baseConfig.MaxLength;
        if (baseConfig._isMinStreamsInitialized) MinStreams = baseConfig.MinStreams;
        if (baseConfig._isMinLengthInitialized) MinLength = baseConfig.MinLength;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioStreamValidationOptions"/> class.
    /// Note: this creates an incomplete object that cannot be used for reading properties until it is assigned to
    /// <see cref="VideoProcessor.AudioSourceValidation" /> (and then accessed by the reference stored there).
    /// It allows you to use object initializer syntax to adjust any combination of properties when creating the options by writing code like
    /// <c>new VideoProcessor(VideoProcessor.Preserve) { AudioSourceValidation = new() { MaxStreams = 3 } }</c>.
    /// </summary>
    private AudioStreamValidationOptions()
    {
    }

    // Internal assignment constructor used for combining existing properties on a base config with overrides from an assigned config.
    // Note: we assume that every property is available on either the baseConfig or the overrideConfig - the caller must ensure this.
    internal AudioStreamValidationOptions(AudioStreamValidationOptions? baseConfig, AudioStreamValidationOptions overrideConfig)
    {
        MaxStreams = overrideConfig._isMaxStreamsInitialized ? overrideConfig.MaxStreams : baseConfig!.MaxStreams;
        MaxLength = overrideConfig._isMaxLengthInitialized ? overrideConfig.MaxLength : baseConfig!.MaxLength;
        MinStreams = overrideConfig._isMinStreamsInitialized ? overrideConfig.MinStreams : baseConfig!.MinStreams;
        MinLength = overrideConfig._isMinLengthInitialized ? overrideConfig.MinLength : baseConfig!.MinLength;
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamValidationOptions"/> that applies no validation.
    /// </summary>
    public static AudioStreamValidationOptions None { get; } = new AudioStreamValidationOptions()
    {
        MaxStreams = null,
        MaxLength = null,
        MinStreams = null,
        MinLength = null,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamValidationOptions"/> that validates exactly 1 audio stream.
    /// </summary>
    public static AudioStreamValidationOptions StandardAudio { get; } = new AudioStreamValidationOptions()
    {
        MaxStreams = 1,
        MaxLength = null,
        MinStreams = 1,
        MinLength = null,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamValidationOptions"/> that validates either 0 or 1 audio streams.
    /// </summary>
    public static AudioStreamValidationOptions OptionalStandardAudio { get; } = new AudioStreamValidationOptions()
    {
        MaxStreams = 1,
        MaxLength = null,
        MinStreams = null,
        MinLength = null,
    };

    /// <summary>
    /// Gets or initializes the maximum number of audio streams in the source video file.
    /// </summary>
    public int? MaxStreams
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaxStreamsInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxStreams));
            field = value;
            _isMaxStreamsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum length of each audio stream in the source video file.
    /// </summary>
    public TimeSpan? MaxLength
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaxLengthInitialized);
            return field;
        }
        init
        {
            if (value != null) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero, nameof(MaxLength));
            field = value;
            _isMaxLengthInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum number of audio streams in the source video file.
    /// </summary>
    public int? MinStreams
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMinStreamsInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinStreams));
            field = value;
            _isMinStreamsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum length of each audio stream in the source video file.
    /// </summary>
    public TimeSpan? MinLength
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMinLengthInitialized);
            return field;
        }
        init
        {
            if (value != null) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero, nameof(MinLength));
            field = value;
            _isMinLengthInitialized = true;
        }
    }
}
