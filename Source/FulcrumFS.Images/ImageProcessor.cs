using System.Diagnostics;
using Microsoft.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FulcrumFS.Images;

/// <summary>
/// Provides functionality to process image files with specified options.
/// </summary>
public class ImageProcessor : FileProcessor
{
    /// <summary>
    /// Gets the options used for processing images with this processor.
    /// </summary>
    public ImageProcessorOptions Options { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProcessor"/> class with the specified processing options.
    /// </summary>
    public ImageProcessor(ImageProcessorOptions options) : base(GetAllowedFileExtensions(options))
    {
        Options = options;
    }

    /// <inheritdoc/>
    protected override async Task<FileProcessResult> ProcessAsync(FileProcessContext context, CancellationToken cancellationToken)
    {
        var stream = await context.GetSourceAsSeekableStreamAsync(maxInMemoryCopySize: 256 * 1024 * 1024).ConfigureAwait(false);

        var sourceFormat = Image.DetectFormat(stream);

        if (Options.Formats.FirstOrDefault(fo => fo.SourceFormat.ImageSharpFormat == sourceFormat) is not { } formatOptions)
        {
            string allowedFormats = string.Join(", ", Options.Formats.Select(f => f.SourceFormat.Name));
            throw new FileProcessException($"The image format '{sourceFormat.Name}' is not allowed. Allowed formats: {allowedFormats}.");
        }

        stream.Position = 0;
        var info = Image.Identify(stream);

        (ushort orientation, bool swapDimensions) = GetOrientationInfo(info.Metadata);

        if (Options.SourceValidation is not null)
            Validate(info, Options.SourceValidation, swapDimensions);

        // Load 1 extra frame so we can check if we had to remove any frames later.

        stream.Position = 0;
        uint maxLoadFrames = (uint)(Options.MaxFrames < int.MaxValue ? Options.MaxFrames + 1 : Options.MaxFrames);
        using var image = Image.Load(new DecoderOptions { MaxFrames = maxLoadFrames }, stream);

        bool changedData = false;
        bool changedMetadata = false;

        // MaxFrames

        if (Options.MaxFrames < int.MaxValue && image.Frames.Count > Options.MaxFrames)
        {
            image.Frames.RemoveFrame(image.Frames.Count - 1);
            changedData = true;
        }

        // OrientToNormal

        if (Options.OrientToNormal && orientation is not 1)
        {
            image.Mutate(x => x.AutoOrient());
            image.Metadata.ExifProfile!.SetValue(ExifTag.Orientation, orientation = 1);

            swapDimensions = false;
            changedData = true;
        }

        // StripMetadata

        switch (Options.StripMetadata)
        {
            case StripImageMetadataMode.All:
                StripStandardMetadata(image.Metadata, orientation, ref changedMetadata);

                if (!changedMetadata)
                    changedMetadata = formatOptions.SourceFormat.HasExtraStrippableMetadata(image.Metadata);

                break;

            case StripImageMetadataMode.ThumbnailOnly:
                StripThumbnailMetadata(image.Metadata, ref changedMetadata);
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Resize

        bool sourceSupportsTransparency = image.PixelType.AlphaRepresentation is not PixelAlphaRepresentation.None;
        bool resultSupportsTransparency = sourceSupportsTransparency && formatOptions.ResultFormat.SupportsTransparency;

        if (Options.Resize is not null)
            Resize(image, Options.Resize, Options.BackgroundColor, swapDimensions, resultSupportsTransparency, ref changedData);

        cancellationToken.ThrowIfCancellationRequested();

        // BackgroundColor

        if (sourceSupportsTransparency && !Options.BackgroundColor.SkipIfTransparencySupported)
        {
            var bgColor = Options.BackgroundColor.ToLibColor();
            image.Mutate(x => x.BackgroundColor(bgColor));
            changedData = true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Encode/Reencode

        if (formatOptions.SourceFormat != formatOptions.ResultFormat)
            changedData = true;

        string resultExtension = formatOptions.ResultFormat.Extensions.First();

        int sourceQuality = formatOptions.SourceFormat.GetQuality(info.Metadata);
        int resultQuality = Math.Min(sourceQuality, formatOptions.QualityOverride ?? Options.Quality);
        bool reducedQuality = sourceQuality != resultQuality;
        bool resultCompressible = formatOptions.ResultFormat.SupportsCompression;

        (bool reencode, bool discardIfLarger) = (formatOptions.ReencodeOverride ?? Options.ReencodeBehavior) switch {
            ImageReencodeBehavior.DiscardLargerUnlessMetadataChanged => (changedData || changedMetadata || reducedQuality || resultCompressible, !changedData && !changedMetadata),
            ImageReencodeBehavior.DiscardLargerEvenIfMetadataChanged => (changedData || changedMetadata || reducedQuality || resultCompressible, !changedData),
            ImageReencodeBehavior.SkipUnlessMetadataOrQualityChanged => (changedData || changedMetadata || reducedQuality, !changedData && !changedMetadata),
            _ => throw new UnreachableException("Unexpected re-encode behavior."),
        };

        if (reencode)
        {
            // Estimate output size based on (result pixels / source pixels) and a 20% compression factor.
            long estimatedOutputLength = stream.Length * image.Width / info.Width * image.Height / info.Height * 4 / 5;
            var outputStream = new RecyclableMemoryStream(FileRepo.MemoryStreamManager, "FulcrumFS.Images", estimatedOutputLength);

            try
            {
                var resultCompressionLevel = formatOptions.CompressionLevelOverride ?? Options.CompressionLevel;
                var resultEncoder = formatOptions.ResultFormat.GetEncoder(resultCompressionLevel, resultQuality, Options.StripMetadata);
                image.Save(outputStream, resultEncoder);
            }
            catch (Exception)
            {
                outputStream.Dispose();
                throw;
            }

            if (discardIfLarger && outputStream.Length > stream.Length)
                outputStream.Dispose();
            else
                stream = outputStream;
        }

        stream.Position = 0;
        return FileProcessResult.Stream(stream, resultExtension);
    }

    private static (ushort Orientation, bool SwapDimensions) GetOrientationInfo(ImageMetadata metadata)
    {
        if (metadata.ExifProfile?.TryGetValue(ExifTag.Orientation, out var exifOrientation) is not true)
            return (1, false);

        return exifOrientation.Value switch {
            >= 5 and <= 8 and var v => (v, true), // Rotated 90 or 270 degrees.
            >= 1 and <= 4 and var v => (v, false), // Normal or flipped.
            _ => (1, false), // Default to normal orientation.
        };
    }

    private static void Validate(ImageInfo info, ImageSourceValidationOptions validation, bool swapDimensions)
    {
        (int width, int height) = swapDimensions ? (info.Height, info.Width) : (info.Width, info.Height);

        int pixels = info.Width * info.Height;

        if (width > validation.MaxWidth || height > validation.MaxHeight)
            throw new FileProcessException($"The image is too large. Maximum size is {validation.MaxWidth}x{validation.MaxHeight}.");

        if (pixels > validation.MaxPixels)
            throw new FileProcessException("The image is too large.");

        if (width < validation.MinWidth || height < validation.MinHeight)
            throw new FileProcessException($"The image is too small. Minimum size is {validation.MinWidth}x{validation.MinHeight}.");

        if (pixels < validation.MinPixels)
            throw new FileProcessException("The image is too small.");
    }

    private static void StripStandardMetadata(ImageMetadata metadata, ushort orientation, ref bool changedMetadata)
    {
        if (metadata.XmpProfile is not null ||
            metadata.IccProfile is not null ||
            metadata.CicpProfile is not null ||
            metadata.IptcProfile is not null)
        {
            metadata.XmpProfile = null;
            metadata.IccProfile = null;
            metadata.CicpProfile = null;
            metadata.IptcProfile = null;

            changedMetadata = true;
        }

        if (metadata.ExifProfile is not null)
        {
            if (orientation is 1)
            {
                // Orientation is normal so can be stripped as well since it's the default.
                metadata.ExifProfile = null;
                changedMetadata = true;
            }
            else if (metadata.ExifProfile.Values.Count > 1)
            {
                // Image has more than just non-default orientation, create a new profile with just the orientation.
                metadata.ExifProfile = new ExifProfile();
                metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);

                changedMetadata = true;
            }
        }
    }

    private static void StripThumbnailMetadata(ImageMetadata metadata, ref bool changedMetadata)
    {
        if (metadata.ExifProfile is not null)
        {
            if (metadata.ExifProfile.RemoveValue(ExifTag.JPEGInterchangeFormat) ||
                metadata.ExifProfile.RemoveValue(ExifTag.JPEGInterchangeFormatLength))
            {
                changedMetadata = true;
            }
        }
    }

    private static void Resize(
        Image image,
        ImageResizeOptions options,
        BackgroundColor fallbackBgColor,
        bool swapDimensions,
        bool supportsTransparency,
        ref bool changedData)
    {
        if (GetResizeInfo(image, options, swapDimensions) is not { } resizeInfo)
        {
            if (options.ThrowWhenSkipped)
                throw new ImageResizeSkippedException();

            return;
        }

        var padBgColor = options.PadColor ?? fallbackBgColor;
        var padColor = supportsTransparency && padBgColor.SkipIfTransparencySupported ? Color.Transparent : padBgColor.ToLibColor();

        var resizeOptions = new ResizeOptions {
            Size = new Size(resizeInfo.Width, resizeInfo.Height),
            PadColor = padColor,
            Mode = resizeInfo.Mode,
        };

        int beforeWidth = image.Width;
        int beforeHeight = image.Height;

        image.Mutate(x => x.Resize(resizeOptions));

        if (image.Width != beforeWidth || image.Height != beforeHeight)
            changedData = true;
    }

    private static (int Width, int Height, ResizeMode Mode)? GetResizeInfo(Image image, ImageResizeOptions options, bool swapDimensions)
    {
        (int resizeWidth, int resizeHeight) = swapDimensions ? (options.Height, options.Width) : (options.Width, options.Height);

        ResizeMode mode;

        if (options.Mode is ImageResizeMode.FitDown)
        {
            if (image.Width <= resizeWidth && image.Height <= resizeHeight)
                return null;

            double sizeByX = (double)image.Width / resizeWidth;
            double sizeByY = (double)image.Height / resizeHeight;

            if (sizeByX > sizeByY)
                resizeHeight = (int)Math.Round(image.Height / sizeByX);
            else
                resizeWidth = (int)Math.Round(image.Width / sizeByY);

            mode = ResizeMode.Max;
        }
        else if (options.Mode is ImageResizeMode.CropDown)
        {
            double srcAspectRatio = (double)image.Width / image.Height;
            double destAspectRatio = (double)resizeWidth / resizeHeight;

            int srcWidth, srcHeight;

            if (srcAspectRatio >= destAspectRatio)
            {
                srcHeight = image.Height;
                srcWidth = (int)Math.Round(srcHeight * destAspectRatio);
            }
            else
            {
                srcWidth = image.Width;
                srcHeight = (int)Math.Round(srcWidth / destAspectRatio);
            }

            if (srcWidth < resizeWidth)
            {
                resizeWidth = srcWidth;
                resizeHeight = srcHeight;
            }

            if (image.Width == resizeWidth && image.Height == resizeHeight)
                return null;

            mode = ResizeMode.Crop;
        }
        else if (options.Mode is ImageResizeMode.PadDown)
        {
            double srcAspectRatio = (double)image.Width / image.Height;
            double destAspectRatio = (double)resizeWidth / resizeHeight;

            if (srcAspectRatio >= destAspectRatio)
            {
                if (resizeWidth > image.Width)
                {
                    resizeWidth = image.Width;
                    resizeHeight = (int)Math.Round(resizeWidth / destAspectRatio);
                }
            }
            else
            {
                if (resizeHeight > image.Height)
                {
                    resizeHeight = image.Height;
                    resizeWidth = (int)Math.Round(resizeHeight * destAspectRatio);
                }
            }

            if (image.Width == resizeWidth && image.Height == resizeHeight)
                return null;

            mode = ResizeMode.Pad;
        }
        else
        {
            throw new UnreachableException("Unexpected resize mode.");
        }

        return (resizeWidth, resizeHeight, mode);
    }

    private static IEnumerable<string> GetAllowedFileExtensions(ImageProcessorOptions options) => options.Formats.SelectMany(format => format.SourceFormat.Extensions);
}
