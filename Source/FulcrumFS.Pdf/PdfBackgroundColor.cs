namespace FulcrumFS.Pdf;

/// <summary>
/// Represents the background color rendered behind transparent regions of a PDF page during image extraction.
/// </summary>
public readonly record struct PdfBackgroundColor
{
    /// <summary>
    /// Gets a white background color.
    /// </summary>
    public static PdfBackgroundColor White { get; } = new(255, 255, 255);

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
    /// Initializes a new instance of the <see cref="PdfBackgroundColor"/> struct with the specified RGB values.
    /// </summary>
    public PdfBackgroundColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>
    /// Creates a new instance of the <see cref="PdfBackgroundColor"/> struct from RGB values.
    /// </summary>
    public static PdfBackgroundColor FromRgb(byte r, byte g, byte b) => new(r, g, b);
}
