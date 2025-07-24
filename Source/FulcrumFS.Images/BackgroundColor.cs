using SixLabors.ImageSharp;

namespace FulcrumFS.Images;

/// <summary>
/// Represents a background color applied during image processing.
/// </summary>
public readonly record struct BackgroundColor
{
    /// <summary>
    /// Gets the red component of the background color.
    /// </summary>
    public byte R { get; }

    /// <summary>
    /// Gets the green component of the background color.
    /// </summary>
    public byte G { get; }

    /// <summary>
    /// Gets the blue component of the background color.
    /// </summary>
    public byte B { get; }

    /// <summary>
    /// Gets a value indicating whether to skip applying the background color if the source image and resulting format support transparency.
    /// </summary>
    public bool SkipIfTransparencySupported { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundColor"/> struct with the specified RGB values and a flag indicating whether to skip applying the
    /// color if transparency is supported.
    /// </summary>
    public BackgroundColor(byte r, byte g, byte b, bool skipIfTransparencySupported)
    {
        R = r;
        G = g;
        B = b;
        SkipIfTransparencySupported = skipIfTransparencySupported;
    }

    /// <summary>
    /// Creates a new instance of the <see cref="BackgroundColor"/> struct from RGB values a flag indicating whether to skip applying the color if transparency
    /// is supported.
    /// </summary>
    public static BackgroundColor FromRgb(byte r, byte g, byte b, bool skipIfTransparencySupported = false)
        => new(r, g, b, skipIfTransparencySupported);

    internal Color ToLibColor() => Color.FromRgb(R, G, B);
}
