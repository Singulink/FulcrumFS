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
    private class PngFormat : ImageFormat
    {
        public PngFormat() : base(SixLabors.ImageSharp.Formats.Png.PngFormat.Instance) { }

        public override string Name => "PNG";

        public override bool SupportsTransparency => true;

        public override bool SupportsCompression => true;

        public override bool SupportsQuality => false;

        public override bool SupportsMultipleFrames => true;

        internal override bool HasExtraStrippableMetadata(ImageMetadata metadata)
        {
            var pngMetadata = metadata.GetPngMetadata();
            return pngMetadata.TextData.Count > 0;
        }

        internal override IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int quality, ImageMetadataStrippingMode stripMetadataMode)
        {
            return new PngEncoder {
                ChunkFilter = stripMetadataMode >= ImageMetadataStrippingMode.Preferred ?
                    PngChunkFilter.ExcludeTextChunks | PngChunkFilter.ExcludePhysicalChunk : PngChunkFilter.None,

                CompressionLevel = compressionLevel switch {
                    ImageCompressionLevel.Lowest => PngCompressionLevel.Level1,
                    ImageCompressionLevel.Low => PngCompressionLevel.Level3,
                    ImageCompressionLevel.Medium => PngCompressionLevel.Level5,
                    ImageCompressionLevel.High => PngCompressionLevel.Level7,
                    ImageCompressionLevel.Highest => PngCompressionLevel.Level9,
                    _ => throw new UnreachableException("Unexpected compression level."),
                },
            };
        }
    }
}
