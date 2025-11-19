using Singulink.Enums;

namespace FulcrumFS.Images;

/// <summary>
/// Provides options for resizing images.
/// </summary>
public sealed class ImageResizeOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImageResizeOptions"/> class with the specified dimensions and resize mode.
    /// </summary>
    /// <param name="mode">The resize mode to apply.</param>
    /// <param name="width">The target width in pixels. Must be greater than or equal to 1.</param>
    /// <param name="height">The target height in pixels. Must be greater than or equal to 1.</param>
    public ImageResizeOptions(ImageResizeMode mode, int width, int height)
    {
        mode.ThrowIfNotDefined(nameof(mode));
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1, nameof(width));
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1, nameof(height));

        Mode = mode;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Gets the resize mode, which determines how the image is scaled to fit within the target width and height.
    /// </summary>
    public ImageResizeMode Mode { get; }

    /// <summary>
    /// Gets the target width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the target height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets or initializes the padding color to use when <see cref="Mode"/> is set to a padding mode. If the value is <see langword="null"/>, padding
    /// operations will fall back to using <see cref="ImageProcessor.BackgroundColor"/> as the padding color. Default is <see langword="null"/>.
    /// </summary>
    public ImageBackgroundColor? PadColor { get; init; }

#pragma warning disable SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether an <see cref="ImageResizeSkippedException"/> should be thrown when resizing is skipped because no pixel
    /// geometry would change for the selected <see cref="Mode"/> and target size.
    /// </summary>
    /// <remarks>
    /// Setting this property to <see langword="true"/> can help avoid storing duplicate images in a repository. For example, if you attempt to generate a
    /// thumbnail from an existing repository image that is already equal to or smaller than the desired thumbnail size, <see
    /// cref="ImageResizeSkippedException"/> will be thrown. You can catch this exception and use the reference to the existing image, rather than storing
    /// a new identical thumbnail.
    /// </remarks>
    public bool ThrowWhenSkipped { get; init; }
}
