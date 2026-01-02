namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Options for validating audio streams during processing.
/// </summary>
public sealed record AudioStreamValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AudioStreamValidationOptions"/> class.
    /// By default, validates that there is at most 1 audio stream only.
    /// </summary>
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
    /// Gets or initializes the maximum number of audio streams in the source video file.
    /// Default is 1.
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
    /// Gets or initializes the maximum length of each audio stream in the source video file.
    /// Default is no maximum.
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
    /// Gets or initializes the minimum number of audio streams in the source video file.
    /// Default is no minimum.
    /// </summary>
    public int? MinStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinStreams));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum length of each audio stream in the source video file.
    /// Default is no minimum.
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
