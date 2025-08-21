namespace FulcrumFS.Images;

/// <summary>
/// Specifies the available modes for resizing an image.
/// </summary>
public enum ImageResizeMode
{
    /// <summary>
    /// Fit (contain): Preserve source image aspect ratio and scale down as needed to fit within the target width and height. Does not upscale smaller
    /// images; output dimensions are less than or equal to the target size.
    /// </summary>
    FitDown,

    /// <summary>
    /// Pad (letterbox): Pad the source image to match the target aspect ratio and scale down as needed to fit within the target size. Unused area is filled
    /// using <see cref="ImageResizeOptions.PadColor"/>. Does not upscale smaller images; output dimensions are less than or equal to the target size.
    /// </summary>
    PadDown,

    /// <summary>
    /// Crop (cover): Crop the source image to match the target aspect ratio and scale down as needed to fit within the target size. Does not upscale smaller
    /// images; output dimensions are less than or equal to the target size.
    /// </summary>
    CropDown,
}
