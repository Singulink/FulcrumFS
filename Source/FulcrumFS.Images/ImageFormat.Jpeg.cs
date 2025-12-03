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

        public override string Name => "JPEG";

        public override bool SupportsTransparency => false;

        public override bool SupportsCompression => false;

        public override bool SupportsQuality => true;

        public override bool SupportsMultipleFrames => false;

        internal override int GetJpegEquivalentQuality(LibMetadata.ImageMetadata metadata)
        {
            var jpegMetadata = metadata.GetJpegMetadata();
            return jpegMetadata.ColorType is not null ? jpegMetadata.Quality : 100;
        }

        internal override LibFormats.IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int jpegEquivalentQuality, ImageMetadataStrippingMode stripMetadataMode)
        {
            return new LibFormats.Jpeg.JpegEncoder { Quality = jpegEquivalentQuality };
        }
    }
}
