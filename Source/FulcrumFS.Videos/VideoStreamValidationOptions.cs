namespace FulcrumFS.Videos;

/// <summary>
/// Options for validating video streams during processing.
/// </summary>
public sealed class VideoStreamValidationOptions
{
    // Helper fields to support our copy constructor:
    private bool _isMaxWidthInitialized;
    private bool _isMaxHeightInitialized;
    private bool _isMaxPixelsInitialized;
    private bool _isMaxStreamsInitialized;
    private bool _isMaxLengthInitialized;
    private bool _isMinWidthInitialized;
    private bool _isMinHeightInitialized;
    private bool _isMinPixelsInitialized;
    private bool _isMinStreamsInitialized;
    private bool _isMinLengthInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoStreamValidationOptions"/> class - this constructor is the copy constructor.
    /// </summary>
    public VideoStreamValidationOptions(VideoStreamValidationOptions baseConfig)
    {
        if (baseConfig._isMaxWidthInitialized) MaxWidth = baseConfig.MaxWidth;
        if (baseConfig._isMaxHeightInitialized) MaxHeight = baseConfig.MaxHeight;
        if (baseConfig._isMaxPixelsInitialized) MaxPixels = baseConfig.MaxPixels;
        if (baseConfig._isMaxStreamsInitialized) MaxStreams = baseConfig.MaxStreams;
        if (baseConfig._isMaxLengthInitialized) MaxLength = baseConfig.MaxLength;
        if (baseConfig._isMinWidthInitialized) MinWidth = baseConfig.MinWidth;
        if (baseConfig._isMinHeightInitialized) MinHeight = baseConfig.MinHeight;
        if (baseConfig._isMinPixelsInitialized) MinPixels = baseConfig.MinPixels;
        if (baseConfig._isMinStreamsInitialized) MinStreams = baseConfig.MinStreams;
        if (baseConfig._isMinLengthInitialized) MinLength = baseConfig.MinLength;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoStreamValidationOptions"/> class.
    /// Note: this creates an incomplete object that cannot be used for reading properties until it is assigned to
    /// <see cref="VideoProcessor.VideoSourceValidation" /> (and then accessed by the reference stored there).
    /// It allows you to use object initializer syntax to adjust any combination of properties when creating the options by writing code like
    /// <c>new VideoProcessor(VideoProcessor.Preserve) { VideoSourceValidation = new() { MaxWidth = 100 } }</c>.
    /// </summary>
    public VideoStreamValidationOptions()
    {
    }

    // Internal assignment constructor used for combining existing properties on a base config with overrides from an assigned config.
    // Note: we assume that every property is available on either the baseConfig or the overrideConfig - the caller must ensure this.
    internal VideoStreamValidationOptions(VideoStreamValidationOptions? baseConfig, VideoStreamValidationOptions overrideConfig)
    {
        MaxWidth = overrideConfig._isMaxWidthInitialized ? overrideConfig.MaxWidth : baseConfig!.MaxWidth;
        MaxHeight = overrideConfig._isMaxHeightInitialized ? overrideConfig.MaxHeight : baseConfig!.MaxHeight;
        MaxPixels = overrideConfig._isMaxPixelsInitialized ? overrideConfig.MaxPixels : baseConfig!.MaxPixels;
        MaxStreams = overrideConfig._isMaxStreamsInitialized ? overrideConfig.MaxStreams : baseConfig!.MaxStreams;
        MaxLength = overrideConfig._isMaxLengthInitialized ? overrideConfig.MaxLength : baseConfig!.MaxLength;
        MinWidth = overrideConfig._isMinWidthInitialized ? overrideConfig.MinWidth : baseConfig!.MinWidth;
        MinHeight = overrideConfig._isMinHeightInitialized ? overrideConfig.MinHeight : baseConfig!.MinHeight;
        MinPixels = overrideConfig._isMinPixelsInitialized ? overrideConfig.MinPixels : baseConfig!.MinPixels;
        MinStreams = overrideConfig._isMinStreamsInitialized ? overrideConfig.MinStreams : baseConfig!.MinStreams;
        MinLength = overrideConfig._isMinLengthInitialized ? overrideConfig.MinLength : baseConfig!.MinLength;
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoStreamValidationOptions"/> that applies no validation.
    /// </summary>
    public static VideoStreamValidationOptions None { get; } = new VideoStreamValidationOptions()
    {
        MaxWidth = null,
        MaxHeight = null,
        MaxPixels = null,
        MaxStreams = null,
        MaxLength = null,
        MinWidth = null,
        MinHeight = null,
        MinPixels = null,
        MinStreams = null,
        MinLength = null,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoStreamValidationOptions"/> that validates exactly 1 video stream.
    /// </summary>
    public static VideoStreamValidationOptions StandardVideo { get; } = new VideoStreamValidationOptions()
    {
        MaxWidth = null,
        MaxHeight = null,
        MaxPixels = null,
        MaxStreams = 1,
        MaxLength = null,
        MinWidth = null,
        MinHeight = null,
        MinPixels = null,
        MinStreams = 1,
        MinLength = null,
    };

    /// <summary>
    /// Gets or initializes the maximum width of the source video in pixels.
    /// </summary>
    public int? MaxWidth
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaxWidthInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxWidth));
            field = value;
            _isMaxWidthInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum height of the source video in pixels.
    /// </summary>
    public int? MaxHeight
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaxHeightInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxHeight));
            field = value;
            _isMaxHeightInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum number of pixels in the source video.
    /// </summary>
    public int? MaxPixels
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMaxPixelsInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxPixels));
            field = value;
            _isMaxPixelsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum number of video streams in the source video file.
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
    /// Gets or initializes the maximum length of each video stream in the source video file.
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
    /// Gets or initializes the minimum width of the source video in pixels.
    /// </summary>
    public int? MinWidth
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMinWidthInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinWidth));
            field = value;
            _isMinWidthInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum height of the source video in pixels.
    /// </summary>
    public int? MinHeight
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMinHeightInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinHeight));
            field = value;
            _isMinHeightInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum number of pixels in the source video.
    /// </summary>
    public int? MinPixels
    {
        get
        {
            PropertyHelpers.CheckFieldInitialized(_isMinPixelsInitialized);
            return field;
        }
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinPixels));
            field = value;
            _isMinPixelsInitialized = true;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum number of video streams in the source video file.
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
    /// Gets or initializes the minimum length of each video stream in the source video file.
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
