namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line
#pragma warning disable SA1642 // Constructor summary documentation should begin with standard text

/// <summary>
/// Options for validating video streams during processing.
/// </summary>
public sealed record VideoStreamValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoStreamValidationOptions"/> class.
    /// </summary>
    /// <remarks>
    /// By default, validates that there is exactly 1 video stream only.
    /// </remarks>
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
    /// Must be at least 1. Default is <see langword="null" />, which means no maximum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxWidth
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum height of the source video in pixels.</para>
    /// <para>
    /// Must be at least 1. Default is <see langword="null" />, which means no maximum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxHeight
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum number of pixels in the source video.</para>
    /// <para>
    /// Must be at least 1. Default is <see langword="null" />, which means no maximum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxPixels
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum number of video streams in the source video file.</para>
    /// <para>
    /// Must be at least zero. Default is 1.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? MaxStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value ?? 0, nameof(value));
            field = value;
        }
    } = 1;

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum length of each video stream in the source video file.</para>
    /// <para>
    /// Must be greater than zero. Default is <see langword="null" />, which means no maximum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan? MaxLength
    {
        get;
        init
        {
            if (value != null) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum width of the source video in pixels.</para>
    /// <para>
    /// Must be at least 1. Default is <see langword="null" />, which means no minimum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MinWidth
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum height of the source video in pixels.</para>
    /// <para>
    /// Must be at least 1. Default is <see langword="null" />, which means no minimum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MinHeight
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum number of pixels in the source video.</para>
    /// <para>
    /// Must be at least 1. Default is <see langword="null" />, which means no minimum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MinPixels
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum number of video streams in the source video file.</para>
    /// <para>
    /// Must be at least 1. Default is 1.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MinStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    } = 1;

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum length of each video stream in the source video file.</para>
    /// <para>
    /// Must be greater than zero. Default is <see langword="null" />, which means no minimum.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan? MinLength
    {
        get;
        init
        {
            if (value != null) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets the problems of the options, where a minimum is greater than its maximum, which <see cref="VideoProcessingOptions"/> reports in one exception.
    /// </summary>
    internal List<string> GetProblems()
    {
        List<string> problems = [];

        if (MinWidth > MaxWidth)
            problems.Add($"{nameof(VideoStreamValidationOptions)}.{nameof(MinWidth)} ({MinWidth}) is greater than {nameof(MaxWidth)} ({MaxWidth}).");

        if (MinHeight > MaxHeight)
            problems.Add($"{nameof(VideoStreamValidationOptions)}.{nameof(MinHeight)} ({MinHeight}) is greater than {nameof(MaxHeight)} ({MaxHeight}).");

        if (MinPixels > MaxPixels)
            problems.Add($"{nameof(VideoStreamValidationOptions)}.{nameof(MinPixels)} ({MinPixels}) is greater than {nameof(MaxPixels)} ({MaxPixels}).");

        if (MinLength > MaxLength)
            problems.Add($"{nameof(VideoStreamValidationOptions)}.{nameof(MinLength)} ({MinLength}) is greater than {nameof(MaxLength)} ({MaxLength}).");

        if (MinStreams > MaxStreams)
            problems.Add($"{nameof(VideoStreamValidationOptions)}.{nameof(MinStreams)} ({MinStreams}) is greater than {nameof(MaxStreams)} ({MaxStreams}).");

        return problems;
    }
}
