using SixLabors.ImageSharp;
using LibFormats = SixLabors.ImageSharp.Formats;
using LibMetadata = SixLabors.ImageSharp.Metadata;

namespace FulcrumFS.Images;

/// <content>
/// Contains the implementation of the JPEG image format.
/// </content>
public abstract partial class ImageFormat
{
    private class JpegFormat : ImageFormat
    {
        public JpegFormat() : base(LibFormats.Jpeg.JpegFormat.Instance) { }

        public override FileFormat FileFormat => FileFormat.Jpeg;

        public override string Name => "JPEG";

        public override bool SupportsTransparency => false;

        public override bool SupportsCompression => false;

        public override bool SupportsQuality => true;

        public override bool SupportsMultipleFrames => false;

        internal override int GetJpegEquivalentQuality(LibMetadata.ImageMetadata metadata)
        {
            // The quality the JPEG decoder estimated from the quantization tables of the source. (ImageSharp 3 had a nullable color type that told whether
            // the decoder had filled the metadata in, which was checked here; since ImageSharp 4 the color type is always set.)
            return metadata.GetJpegMetadata().Quality;
        }

        internal override LibFormats.IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int jpegEquivalentQuality, ImageMetadataStrippingMode stripMetadataMode)
        {
            return new LibFormats.Jpeg.JpegEncoder { Quality = jpegEquivalentQuality };
        }
    }
}
