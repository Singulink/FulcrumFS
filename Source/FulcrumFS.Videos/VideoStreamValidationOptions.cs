namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line
#pragma warning disable SA1642 // Constructor summary documentation should begin with standard text

/// <summary>
/// Options for validating video streams during processing.
/// </summary>
public sealed record VideoStreamValidationOptions
{
    /// <summary>
    /// <para>
    /// Initializes a new instance of the <see cref="VideoStreamValidationOptions"/> class.</para>
    /// <para>
    /// By default, validates that there is exactly 1 video stream only.</para>
    /// </summary>
    public VideoStreamValidationOptions()
    {
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoStreamValidationOptions"/> that applies no validation.
    /// </summary>
    public static VideoStreamValidationOptions None { get; } = new VideoStreamValidationOptions()
    {
        MaxStreams = null,
        MinStreams = null,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoStreamValidationOptions"/> that validates exactly 1 video stream.
    /// </summary>
    public static VideoStreamValidationOptions StandardVideo { get; } = new VideoStreamValidationOptions();

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum width of the source video in pixels.</para>
    /// <para>
    /// Default is no maximum.</para>
    /// </summary>
    public int? MaxWidth
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxWidth));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum height of the source video in pixels.</para>
    /// <para>
    /// Default is no maximum.</para>
    /// </summary>
    public int? MaxHeight
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxHeight));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum number of pixels in the source video.</para>
    /// <para>
    /// Default is no maximum.</para>
    /// </summary>
    public int? MaxPixels
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxPixels));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum number of video streams in the source video file.</para>
    /// <para>
    /// Default is 1.</para>
    /// </summary>
    public int? MaxStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value ?? 0, nameof(MaxStreams));
            field = value;
        }
    } = 1;

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum length of each video stream in the source video file.</para>
    /// <para>
    /// Default is no maximum.</para>
    /// </summary>
    public TimeSpan? MaxLength
    {
        get;
        init
        {
            if (value != null) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero, nameof(MaxLength));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum width of the source video in pixels.</para>
    /// <para>
    /// Default is no minimum.</para>
    /// </summary>
    public int? MinWidth
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinWidth));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum height of the source video in pixels.</para>
    /// <para>
    /// Default is no minimum.</para>
    /// </summary>
    public int? MinHeight
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinHeight));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum number of pixels in the source video.</para>
    /// <para>
    /// Default is no minimum.</para>
    /// </summary>
    public int? MinPixels
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinPixels));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum number of video streams in the source video file.</para>
    /// <para>
    /// Default is 1.</para>
    /// </summary>
    public int? MinStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinStreams));
            field = value;
        }
    } = 1;

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum length of each video stream in the source video file.</para>
    /// <para>
    /// Default is no minimum.</para>
    /// </summary>
    public TimeSpan? MinLength
    {
        get;
        init
        {
            if (value != null) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero, nameof(MinLength));
            field = value;
        }
    }
}
