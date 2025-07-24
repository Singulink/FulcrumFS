using Singulink.Enums;

namespace FulcrumFS.Images;

/// <summary>
/// Options for resizing images in a file repository.
/// </summary>
public class ImageResizeOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImageResizeOptions"/> class with the specified dimensions and resize mode.
    /// </summary>
    /// <param name="mode">The resize mode to apply. Must be a valid <see cref="ImageResizeMode"/> value.</param>
    /// <param name="width">The width (or max width) of the image in pixels. Must be greater than or equal to 1.</param>
    /// <param name="height">The height (or max height) of the image in pixels. Must be greater than or equal to 1.</param>
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
    /// Gets the mode to use for resizing the image. This determines how the image is scaled down to fit within the specified width and height.
    /// </summary>
    public ImageResizeMode Mode { get; }

    /// <summary>
    /// Gets the width (or max width) of the resized image.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height (or max height) of the resized image.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets or initializes the padding color to use when <see cref="Mode"/> is set to <see cref="ImageResizeMode.Pad"/>. Falls back <see
    /// cref="ImageProcessOptions.BackgroundColor"/> if set to <see langword="null"/>.
    /// </summary>
    public BackgroundColor? PadColor { get; init; }

#pragma warning disable SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether a <see cref="ImageResizeSkippedException"/> should be throw if the image was not resized due to the
    /// source image already being the desired size.
    /// </summary>
    public bool ThrowIfSkipped { get; init; }
}
