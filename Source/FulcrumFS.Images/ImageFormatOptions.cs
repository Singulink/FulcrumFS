namespace FulcrumFS.Images;

/// <summary>
/// Represents options for specifying the source and result image formats when processing images.
/// </summary>
public sealed class ImageFormatOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImageFormatOptions"/> class with the specified source and result image formats. If the result format is
    /// <see langword="null"/>, the source format will be used as the result format.
    /// </summary>
    /// <param name="sourceFormat">The source format to use.</param>
    /// <param name="resultFormat">The result format to convert the image to.</param>
    public ImageFormatOptions(ImageFormat sourceFormat, ImageFormat? resultFormat = null)
    {
        SourceFormat = sourceFormat;
        ResultFormat = resultFormat ?? sourceFormat;
    }

    /// <summary>
    /// Gets the source image format.
    /// </summary>
    public ImageFormat SourceFormat { get; }

    /// <summary>
    /// Gets the result image format.
    /// </summary>
    public ImageFormat ResultFormat { get; }

    /// <summary>
    /// Gets or initializes the compression level to use for the resulting image. Does not affect the quality of the image, but rather the file size and how
    /// much computation is used to try compressing the image. Has no effect on formats that don't support a compression level. Default is <see
    /// cref="ImageCompressionLevel.High"/>.
    /// </summary>
    public ImageCompressionLevel CompressionLevel { get; init; } = ImageCompressionLevel.High;

    /// <summary>
    /// Gets or initializes the quality of the resulting image. This is typically used for lossy formats like JPEG and has no effect on lossless formats. The
    /// quality of the result is capped to the quality of the source (if applicable) to prevent unnecessarily increasing file size. Default is <c>82</c>.
    /// </summary>
    public int Quality { get; init; } = 82;

    /// <summary>
    /// Gets or initializes the behavior for re-encoding the image. Default is <see cref="ImageReencodeBehavior.Always"/>.
    /// </summary>
    public ImageReencodeBehavior Reencode { get; init; } = ImageReencodeBehavior.Always;
}
