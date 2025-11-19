namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Options for validating a source video during processing.
/// </summary>
public class VideoSourceValidationOptions
{
    /// <summary>
    /// Gets or initializes the maximum width of the source video in pixels.
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
    /// Defaults to 1.
    /// </summary>
    public int? MaxVideoStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxVideoStreams));
            field = value;
        }
    } = 1;

    /// <summary>
    /// Gets or initializes the maximum number of audio streams in the source video file.
    /// Defaults to 1.
    /// </summary>
    public int? MaxAudioStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxAudioStreams));
            field = value;
        }
    } = 1;

    /// <summary>
    /// Gets or initializes the maximum video length (in seconds) in the source video file.
    /// </summary>
    public double? MaxVideoLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value ?? 1, 0, nameof(MaxVideoLength));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum width of the source video in pixels.
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
    /// Defaults to 1.
    /// </summary>
    public int? MinVideoStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinVideoStreams));
            field = value;
        }
    } = 1;

    /// <summary>
    /// Gets or initializes the minimum number of audio streams in the source video file.
    /// </summary>
    public int? MinAudioStreams
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinAudioStreams));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum video length (in seconds) in the source video file.
    /// </summary>
    public double? MinVideoLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value ?? 1, 0, nameof(MinVideoLength));
            field = value;
        }
    }
}
