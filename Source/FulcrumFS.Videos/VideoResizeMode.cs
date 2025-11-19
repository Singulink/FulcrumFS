namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the resize mode to use when resizing videos.
/// </summary>
public enum VideoResizeMode
{
    /// <summary>
    /// Resize the video to fit within the specified dimensions while preserving the original aspect ratio, final video may be smaller than target size.
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).
    /// </summary>
    FitDown,

    /// <summary>
    /// Resize the video to fill the specified dimensions while preserving the aspect ratio. May result in cropping.
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).
    /// </summary>
    CropDown,
}
