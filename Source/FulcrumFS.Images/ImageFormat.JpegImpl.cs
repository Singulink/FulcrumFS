using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata;

namespace FulcrumFS.Images;

/// <content>
/// Contains the implementation of the JPEG image format.
/// </content>
public abstract partial class ImageFormat
{
    private class JpegImpl : ImageFormat
    {
        public JpegImpl() : base(JpegFormat.Instance) { }

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
}
