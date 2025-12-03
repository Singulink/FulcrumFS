namespace FulcrumFS.Images;

/// <summary>
/// Specifies a mapping from a source image format to a result image format.
/// </summary>
public sealed record ImageFormatMapping
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImageFormatMapping"/> class with the specified source and result image formats. If <paramref
    /// name="resultFormat"/> is <see langword="null"/>, <paramref name="sourceFormat"/> will be used as the result format.
    /// </summary>
    /// <param name="sourceFormat">The source format to use.</param>
    /// <param name="resultFormat">The result format to convert to, or <see langword="null"/> to keep the source format.</param>
    public ImageFormatMapping(ImageFormat sourceFormat, ImageFormat? resultFormat = null)
    {
        SourceFormat = sourceFormat;
        ResultFormat = resultFormat ?? sourceFormat;
    }

    /// <summary>
    /// Gets the source image format for this mapping.
    /// </summary>
    public ImageFormat SourceFormat { get; }

    /// <summary>
    /// Gets the result image format for this mapping.
    /// </summary>
    public ImageFormat ResultFormat { get; }
}
