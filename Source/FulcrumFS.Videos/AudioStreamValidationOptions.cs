namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line
#pragma warning disable SA1642 // Constructor summary documentation should begin with standard text

/// <summary>
/// Options for validating audio streams during processing.
/// </summary>
public sealed record AudioStreamValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AudioStreamValidationOptions"/> class.
    /// </summary>
    /// <remarks>
    /// By default, validates that there is at most 1 audio stream only.
    /// </remarks>
    public AudioStreamValidationOptions()
    {
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamValidationOptions"/> that applies no validation.
    /// </summary>
    public static AudioStreamValidationOptions None { get; } = new AudioStreamValidationOptions()
    {
        MaxStreams = null,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamValidationOptions"/> that validates exactly 1 audio stream.
    /// </summary>
    public static AudioStreamValidationOptions StandardAudio { get; } = new AudioStreamValidationOptions()
    {
        MinStreams = 1,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="AudioStreamValidationOptions"/> that validates either 0 or 1 audio streams.
    /// </summary>
    public static AudioStreamValidationOptions OptionalStandardAudio { get; } = new AudioStreamValidationOptions();

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum number of audio streams in the source video file.</para>
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
    /// Gets or initializes the maximum length of each audio stream in the source video file.</para>
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
    /// Gets or initializes the minimum number of audio streams in the source video file.</para>
    /// <para>
    /// Must be at least 1. Default is <see langword="null" />, which means no minimum.</para>
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
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the minimum length of each audio stream in the source video file.</para>
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

        if (MinLength > MaxLength)
            problems.Add($"{nameof(AudioStreamValidationOptions)}.{nameof(MinLength)} ({MinLength}) is greater than {nameof(MaxLength)} ({MaxLength}).");

        if (MinStreams > MaxStreams)
            problems.Add($"{nameof(AudioStreamValidationOptions)}.{nameof(MinStreams)} ({MinStreams}) is greater than {nameof(MaxStreams)} ({MaxStreams}).");

        return problems;
    }
}
