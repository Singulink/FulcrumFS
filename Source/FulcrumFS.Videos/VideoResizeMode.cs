namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the resize mode to use when resizing videos.
/// </summary>
public enum VideoResizeMode
{
    /// <summary>
    /// Resize the video to fit within the specified dimensions while preserving the orginal aspect ratio, final video may be smaller than target size.
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).
    /// </summary>
    FitDown,

    /// <summary>
    /// Resize the video to fit within the specified dimensions while preserving the original aspect ratio of the video, and extending the frame to the
    /// specified size.
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).
    /// Background areas are filled with <see cref="VideoResizeOptions.PadColor" />.
    /// </summary>
    FitDownToSize,

    /// <summary>
    /// Resize the video to fill the specified dimensions while preserving the aspect ratio. May result in cropping.
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).
    /// </summary>
    FillDown,

    /// <summary>
    /// Stretch the video to exactly match the specified dimensions, ignoring the aspect ratio.
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).
    /// </summary>
    StretchDown,
}
