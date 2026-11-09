using Singulink.Enums;

namespace FulcrumFS.Images;

/// <summary>
/// Provides options for resizing images.
/// </summary>
public sealed record ImageResizeOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImageResizeOptions"/> class with the specified dimensions and resize mode.
    /// </summary>
    /// <param name="mode">The resize mode to apply.</param>
    /// <param name="width">The target width in pixels. Must be greater than zero.</param>
    /// <param name="height">The target height in pixels. Must be greater than zero.</param>
    /// <param name="matchSourceOrientation">
    /// If <see langword="true"/>, the target <paramref name="width"/> and <paramref name="height"/> are swapped prior to resizing when needed so that the
    /// longest target dimension matches the longest source dimension (i.e. portrait targets are applied to portrait sources and landscape targets to
    /// landscape sources). Default is <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined <see cref="ImageResizeMode"/> value, or
    /// <paramref name="width"/> or <paramref name="height"/> is zero or negative.</exception>
    public ImageResizeOptions(ImageResizeMode mode, int width, int height, bool matchSourceOrientation = false)
    {
        mode.ThrowIfNotDefined();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Mode = mode;
        Width = width;
        Height = height;
        MatchSourceOrientation = matchSourceOrientation;
    }

    /// <summary>
    /// Gets or initializes the resize mode, which determines how the image is scaled to fit within the target width and height.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="ImageResizeMode"/> value.</exception>
    public ImageResizeMode Mode
    {
        get;
        init
        {
            value.ThrowIfNotDefined();
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the target width in pixels. Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int Width
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the target height in pixels. Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int Height
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating whether the target <see cref="Width"/> and <see cref="Height"/> should be swapped prior to resizing when needed
    /// so that the longest target dimension matches the longest source dimension (i.e. portrait targets are applied to portrait sources and landscape targets
    /// to landscape sources). Default is <see langword="false"/>.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool MatchSourceOrientation { get; init; }
#pragma warning restore SA1623

    /// <summary>
    /// Gets or initializes the padding color to use when <see cref="Mode"/> is set to a padding mode. If the value is <see langword="null"/>, padding
    /// operations will fall back to using <see cref="ImageProcessingOptions.BackgroundColor"/> as the padding color. Default is <see langword="null"/>.
    /// </summary>
    public ImageBackgroundColor? PadColor { get; init; }
}
