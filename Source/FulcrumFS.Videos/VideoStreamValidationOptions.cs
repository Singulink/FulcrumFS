namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Options for validating video streams during processing.
/// </summary>
public class VideoStreamValidationOptions
{
    /// <summary>
    /// Gets or initializes the maximum width of the source video in pixels.
    /// Default is <see langword="null" />, indicating no maximum.
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
    /// Gets or initializes the maximum height of the source video in pixels.
    /// Default is <see langword="null" />, indicating no maximum.
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
    /// Gets or initializes the maximum number of pixels in the source video.
    /// Default is <see langword="null" />, indicating no maximum.
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
    /// Gets or initializes the maximum number of video streams in the source video file.
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
    /// Gets or initializes the maximum length of each video stream in the source video file.
    /// Default is <see langword="null" />, indicating no maximum.
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
    /// Gets or initializes the minimum width of the source video in pixels.
    /// Default is <see langword="null" />, indicating no minimum.
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
    /// Gets or initializes the minimum height of the source video in pixels.
    /// Default is <see langword="null" />, indicating no minimum.
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
    /// Gets or initializes the minimum number of pixels in the source video.
    /// Default is <see langword="null" />, indicating no minimum.
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
    /// Gets or initializes the minimum number of video streams in the source video file.
    /// Default is 1.
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
    /// Gets or initializes the minimum length of each video stream in the source video file.
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
