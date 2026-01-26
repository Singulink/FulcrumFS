namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the resize mode to use when resizing videos.
/// </summary>
public enum VideoResizeMode
{
    /// <summary>
    /// <para>
    /// Resize the video to fit within the specified dimensions while preserving the original aspect ratio, final video may be smaller than target size.</para>
    /// <para>
    /// Does not upscale the video if it is smaller than the specified dimensions (in both dimensions).</para>
    /// </summary>
    FitDown,
}
