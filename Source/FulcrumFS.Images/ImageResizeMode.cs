namespace FulcrumFS.Images;

/// <summary>
/// Specifies the different modes available for resizing an image.
/// </summary>
public enum ImageResizeMode
{
    /// <summary>
    /// Maintains the aspect ratio of the image and scales it down (if needed) to fit within the desired size.
    /// </summary>
    Max,

    /// <summary>
    /// Pads the image to the aspect ratio of the desired size and scales it down (if needed) to the desired size.
    /// </summary>
    Pad,

    /// <summary>
    /// Crops the image to the aspect ratio of the desired size and scales it down (if needed) to the desired size.
    /// </summary>
    Crop,
}
