using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata;

namespace FulcrumFS.Images;

/// <content>
/// Contains the implementation of the PNG image format.
/// </content>
public abstract partial class ImageFormat
{
    private class PngImpl : ImageFormat
    {
        public PngImpl() : base(PngFormat.Instance) { }

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
