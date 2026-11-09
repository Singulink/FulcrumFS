using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for resizing videos.
/// </summary>
public sealed record VideoResizeOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeOptions" /> class with the specified mode and target size.
    /// </summary>
    /// <remarks>
    /// Restrictions of H.264 and HEVC still apply when rescaling videos, such as requiring even dimensions with 4:2:0 subsampling, being internally
    /// padded up to block sizes, etc.; if the size you specify is too small for the video to be possible, an exception will be thrown during processing.
    /// </remarks>
    /// <param name="mode">The resize mode to apply.</param>
    /// <param name="width">The target width in pixels. Must be greater than zero.</param>
    /// <param name="height">The target height in pixels. Must be greater than zero.</param>
    /// <param name="matchSourceOrientation">
    /// If <see langword="true"/>, the target <paramref name="width"/> and <paramref name="height"/> are swapped prior to resizing when needed so that the
    /// longest target dimension matches the longest source dimension (i.e. portrait targets are applied to portrait sources and landscape targets to
    /// landscape sources). Default is <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined <see cref="VideoResizeMode"/> value, or
    /// <paramref name="width"/> or <paramref name="height"/> is zero or negative.</exception>
    public VideoResizeOptions(VideoResizeMode mode, int width, int height, bool matchSourceOrientation = false)
    {
        mode.ThrowIfNotDefined();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Mode = mode;
        Width = width;
        Height = height;
        MatchSourceOrientation = matchSourceOrientation;
    }

    /// <summary>
    /// Gets or initializes the resize mode to use when resizing videos.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="VideoResizeMode"/> value.</exception>
    public VideoResizeMode Mode
    {
        get;
        init
        {
            value.ThrowIfNotDefined();
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the target width in pixels. Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int Width
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the target height in pixels. Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int Height
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating whether the target <see cref="Width"/> and <see cref="Height"/> should be swapped prior to resizing when needed
    /// so that the longest target dimension matches the longest source dimension (i.e. portrait targets are applied to portrait sources and landscape targets
    /// to landscape sources). Default is <see langword="false"/>.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool MatchSourceOrientation { get; init; }
#pragma warning restore SA1623
}
