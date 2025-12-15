using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for resizing videos.
/// </summary>
public sealed record VideoResizeOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeOptions" /> class with the specified mode and target size.
    /// Note: restrictions of H.264 and HEVC still apply when rescaling videos, such as requiring even dimensions with 4:2:0 subsampling, being internally
    /// padded up to block sizes, etc.; if the size you specify is too small for the video to be possible, an exception will be thrown during processing.
    /// </summary>
    public VideoResizeOptions(VideoResizeMode mode, int width, int height)
    {
        Mode = mode;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Gets or initializes the resize mode to use when resizing videos.
    /// </summary>
    public VideoResizeMode Mode
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(Mode));
            field = value;
        }
    }

    /// <summary>
    /// Gets the target width in pixels.
    /// </summary>
    public int Width
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(Width));
            field = value;
        }
    }

    /// <summary>
    /// Gets the target height in pixels.
    /// </summary>
    public int Height
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(Height));
            field = value;
        }
    }
}
