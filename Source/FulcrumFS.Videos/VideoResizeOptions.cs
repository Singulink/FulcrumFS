namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for resizing videos.
/// </summary>
public class VideoResizeOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeOptions"/> class with the specified resize mode.
    /// </summary>
    public VideoResizeOptions(VideoResizeMode resizeMode, int width, int height)
    {
        ResizeMode = resizeMode;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Gets or initializes the resize mode to use when resizing videos.
    /// </summary>
    public VideoResizeMode ResizeMode { get; }

    /// <summary>
    /// Gets the target width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the target height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets or initializes the background color to use when resizing videos.
    /// Default is <see langword="null"/>, which uses black as the background color.
    /// </summary>
    public VideoBackgroundColor? PadColor { get; init; }
}
