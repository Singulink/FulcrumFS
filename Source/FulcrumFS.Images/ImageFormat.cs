using System.Collections.Immutable;
using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata;

namespace FulcrumFS.Images;

/// <summary>
/// Represents the compression level for image formats that support it.
/// </summary>
public enum ImageCompressionLevel
{
    /// <summary>
    /// Low compression level, only recommended for fast processing of images that are not expected to be stored for long periods of time.
    /// </summary>
    Low,

    /// <summary>
    /// Medium compression level, recommended for use cases where increased image size is acceptable to achieve faster processing times.
    /// </summary>
    Medium,

    /// <summary>
    /// High compression level, recommended for must use-cases, including long-term storage of images where size is an important factor but processing time
    /// should not be expended to compress images beyond the point of diminishing returns.
    /// </summary>
    High,

    /// <summary>
    /// Maximum compression level, recommended only for long-term storage of images where file size is a critical factor and processing time is not a concern.
    /// </summary>
    Best,
}

/// <summary>
/// Represents a base class for image formats used in image processing.
/// </summary>
public abstract class ImageFormat
{
    /// <summary>
    /// Gets the JPEG image format.
    /// </summary>
    public static ImageFormat Jpeg { get; } = new JpegImpl();

    /// <summary>
    /// Gets the PNG image format.
    /// </summary>
    public static ImageFormat Png { get; } = new PngImpl();

    internal static ImmutableArray<ImageFormat> AllFormats { get; } = [Jpeg, Png];

    /// <summary>
    /// Gets the file extensions associated with this image format.
    /// </summary>
    public IEnumerable<string> Extensions => ImageSharpFormat.FileExtensions.Select(ext => "." + ext);

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

    internal IImageFormat ImageSharpFormat { get; }

    private ImageFormat(IImageFormat format)
    {
        ImageSharpFormat = format;
    }

    internal virtual int GetQuality(ImageMetadata metadata) => 100;

    internal virtual bool HasExtraStrippableMetadata(ImageMetadata metadata) => false;

    internal abstract IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int quality, StripImageMetadataMode stripMetadataMode);

    private class JpegImpl : ImageFormat
    {
        public JpegImpl() : base(JpegFormat.Instance)
        {
        }

        public override string Name => "JPEG";

        public override bool SupportsTransparency => false;

        public override bool SupportsCompression => false;

        public override bool SupportsQuality => true;

        internal override int GetQuality(ImageMetadata metadata)
        {
            var jpegMetadata = metadata.GetJpegMetadata();
            return jpegMetadata.ColorType is not null ? jpegMetadata.Quality : 100;
        }

        internal override IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int quality, StripImageMetadataMode stripMetadataMode)
        {
            return new JpegEncoder { Quality = quality };
        }
    }

    private class PngImpl : ImageFormat
    {
        public PngImpl() : base(PngFormat.Instance)
        {
        }

        public override string Name => "PNG";

        public override bool SupportsTransparency => true;

        public override bool SupportsCompression => true;

        public override bool SupportsQuality => false;

        internal override bool HasExtraStrippableMetadata(ImageMetadata metadata)
        {
            var pngMetadata = metadata.GetPngMetadata();
            return pngMetadata.TextData.Count > 0;
        }

        internal override IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int quality, StripImageMetadataMode stripMetadataMode)
        {
            return new PngEncoder {
                ChunkFilter = stripMetadataMode is StripImageMetadataMode.All ? PngChunkFilter.ExcludeTextChunks : PngChunkFilter.None,
                CompressionLevel = compressionLevel switch {
                    ImageCompressionLevel.Low => PngCompressionLevel.Level3,
                    ImageCompressionLevel.Medium => PngCompressionLevel.Level5,
                    ImageCompressionLevel.High => PngCompressionLevel.Level7,
                    ImageCompressionLevel.Best => PngCompressionLevel.Level9,
                    _ => throw new UnreachableException("Unexpected compression level."),
                },
            };
        }
    }
}
