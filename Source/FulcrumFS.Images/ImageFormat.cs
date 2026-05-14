using System.Collections.Immutable;
using FulcrumFS.Utilities;
using LibFormats = SixLabors.ImageSharp.Formats;
using LibMetadata = SixLabors.ImageSharp.Metadata;

namespace FulcrumFS.Images;

/// <summary>
/// Represents a base class for image formats used in image processing.
/// </summary>
public abstract partial class ImageFormat
{
    /// <summary>
    /// Gets the JPEG image format.
    /// </summary>
    public static ImageFormat Jpeg { get; } = new JpegFormat();

    /// <summary>
    /// Gets the PNG image format.
    /// </summary>
    public static ImageFormat Png { get; } = new PngFormat();

    /// <summary>
    /// Gets the BMP image format.
    /// </summary>
    public static ImageFormat Bmp { get; } = new BmpFormat();

    internal static ImmutableArray<ImageFormat> AllFormats { get; } = [Jpeg, Png, Bmp];

    /// <summary>
    /// Gets the file extensions associated with this image format (including the leading '.').
    /// </summary>
    public IReadOnlyList<string> Extensions => field ??= [.. LibFormat.FileExtensions.Select(ext => FileExtension.Normalize("." + ext))];

    /// <summary>
    /// Gets the primary file extension associated with this image format (including the leading '.').
    /// </summary>
    /// <remarks>
    /// The primary extension is the first extension in <see cref="Extensions"/> and all files of this format will be written with this extension.
    /// </remarks>
    public string PrimaryExtension => Extensions[0];

    /// <summary>
    /// Gets the name of the image format (e.g., "JPEG", "PNG", "GIF").
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets a value indicating whether this image format supports transparency.
    /// </summary>
    public abstract bool SupportsTransparency { get; }

    /// <summary>
    /// Gets a value indicating whether this image format supports compression settings.
    /// </summary>
    public abstract bool SupportsCompression { get; }

    /// <summary>
    /// Gets a value indicating whether this image format supports quality settings.
    /// </summary>
    public abstract bool SupportsQuality { get; }

    /// <summary>
    /// Gets a value indicating whether this image format supports multiple frames (e.g., animated images).
    /// </summary>
    public abstract bool SupportsMultipleFrames { get; }

    internal LibFormats.IImageFormat LibFormat { get; }

    private ImageFormat(LibFormats.IImageFormat libFormat)
    {
        LibFormat = libFormat;
    }

    /// <inheritdoc/>
    public override string ToString() => Name;

    internal virtual int GetJpegEquivalentQuality(LibMetadata.ImageMetadata metadata) => 100;

    internal virtual bool HasExtraStrippableMetadata(LibMetadata.ImageMetadata metadata) => false;

    internal abstract LibFormats.IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int quality, ImageMetadataStrippingMode stripMetadataMode);
}
