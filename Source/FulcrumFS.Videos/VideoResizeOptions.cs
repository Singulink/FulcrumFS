namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for resizing videos.
/// </summary>
public sealed class VideoResizeOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeOptions" /> class with the specified target size.
    /// Resizes the video to fit within the specified dimensions while preserving the original aspect ratio, final video may be smaller than target size.
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).
    /// </summary>
    public VideoResizeOptions(int width, int height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoResizeOptions" /> class - this constructor is the copy constructor.
    /// </summary>
    public VideoResizeOptions(VideoResizeOptions baseConfig)
    {
        // Note: I haven't used PropertyHelpers here as there are no "base configs", we have another publicly exposed constructor, there are no configurable
        // sub-classes, it is unlikely we will add more config options, etc.
        Width = baseConfig.Width;
        Height = baseConfig.Height;
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
