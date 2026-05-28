using LibFormats = SixLabors.ImageSharp.Formats;

namespace FulcrumFS.Images;

/// <content>
/// Contains the implementation of the BMP image format.
/// </content>
public abstract partial class ImageFormat
{
    private class BmpFormat : ImageFormat
    {
        public BmpFormat() : base(LibFormats.Bmp.BmpFormat.Instance) { }

        public override FileFormat FileFormat => FileFormat.Bmp;

        public override string Name => "BMP";

        public override bool SupportsTransparency => true;

        public override bool SupportsCompression => false;

        public override bool SupportsQuality => false;

        public override bool SupportsMultipleFrames => false;

        internal override LibFormats.IImageEncoder GetEncoder(ImageCompressionLevel compressionLevel, int quality, ImageMetadataStrippingMode stripMetadataMode)
        {
            return new LibFormats.Bmp.BmpEncoder { SupportTransparency = true };
        }
    }
}
