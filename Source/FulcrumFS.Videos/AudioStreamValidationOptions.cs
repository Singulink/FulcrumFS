namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Options for validating audio streams during processing.
/// </summary>
public class AudioStreamValidationOptions
{
    /// <summary>
    /// Gets or initializes the maximum number of audio streams in the source video file.
    /// Default is 1.
    /// </summary>
    public int? MaxStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxStreams));
            field = value;
        }
    } = 1;

    /// <summary>
    /// Gets or initializes the maximum length of each audio stream in the source video file.
    /// Default is <see langword="null" />, indicating no limit.
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
    /// Default is <see langword="null" />, indicating no minimum.
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
    /// Default is <see langword="null" />, indicating no minimum.
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
