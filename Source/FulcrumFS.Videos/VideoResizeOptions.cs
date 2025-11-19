namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for resizing videos.
/// </summary>
public class VideoResizeOptions
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
    /// Gets the target width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the target height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets or initializes a value indicating whether an <see cref="VideoResizeSkippedException" /> should be thrown when resizing is skipped because no pixel
    /// geometry would change for the selected target size.
    /// </summary>
    /// <remarks>
    /// Setting this property to <see langword="true" /> can help avoid storing duplicate videos in a repository. For example, if you attempt to generate a
    /// low-res version from an existing repository video that is already equal to or smaller than the desired low-res size, <see
    /// cref="VideoResizeSkippedException" /> will be thrown. You can catch this exception and use the reference to the existing video, rather than storing
    /// a new identical low-res version.
    /// </remarks>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ThrowWhenSkipped { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors
}
