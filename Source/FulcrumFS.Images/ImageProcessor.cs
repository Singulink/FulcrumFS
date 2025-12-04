using System.Diagnostics;
using Microsoft.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FulcrumFS.Images;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides functionality to process image files with specified options.
/// </summary>
public sealed class ImageProcessor : FileProcessor
{
    /// <inheritdoc/>
    public override IReadOnlyList<string> AllowedFileExtensions => field ??= [.. Options.Formats.SelectMany(format => format.SourceFormat.Extensions)];

    /// <summary>
    /// Gets the options used by this image processor.
    /// </summary>
    public ImageProcessingOptions Options { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProcessor"/> class using the specified options.
    /// </summary>
    public ImageProcessor(ImageProcessingOptions options)
    {
        Options = options;
    }

    /// <inheritdoc/>
    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        const int MaxInMemoryCopySize = 20 * 1024 * 1024; // 20 MB
        var stream = await context.GetSourceAsSeekableStreamAsync(preferInMemory: true, MaxInMemoryCopySize).ConfigureAwait(false);

        var sourceFormat = Image.DetectFormat(stream);

        if (Options.Formats.FirstOrDefault(fo => fo.SourceFormat.LibFormat == sourceFormat) is not { } formatMapping)
        {
            string allowedFormats = string.Join(", ", Options.Formats.Select(f => f.SourceFormat.Name));
            throw new FileProcessingException($"Image format '{sourceFormat.Name}' is not allowed. Allowed formats: {allowedFormats}");
        }

        stream.Position = 0;
        var info = Image.Identify(stream);

        (ushort orientation, bool swapDimensions) = GetOrientationInfo(info.Metadata);

        if (Options.SourceValidation is not null)
            Validate(info, Options.SourceValidation, swapDimensions);

        stream.Position = 0;

        // Load 1 extra frame so we can check if we had to remove any frames later.

        uint maxLoadFrames;

        if (!formatMapping.ResultFormat.SupportsMultipleFrames)
            maxLoadFrames = 2;
        else
            maxLoadFrames = (uint)(Options.MaxFrames < int.MaxValue ? Options.MaxFrames + 1 : Options.MaxFrames);

        using var image = Image.Load(new DecoderOptions { MaxFrames = maxLoadFrames }, stream);
        bool hasRequiredChange = false;

        // Max frames

        if (Options.MaxFrames < int.MaxValue && image.Frames.Count > Options.MaxFrames)
        {
            image.Frames.RemoveFrame(image.Frames.Count - 1);
            hasRequiredChange = true;
        }

        // Reorientation

        if (Options.ReorientMode is not ImageReorientMode.None && orientation is not 1)
        {
            image.Mutate(x => x.AutoOrient());
            image.Metadata.ExifProfile!.SetValue(ExifTag.Orientation, orientation = 1);

            swapDimensions = false;
            hasRequiredChange = Options.ReorientMode is ImageReorientMode.RequireNormal;
        }

        // Metadata stripping

        switch (Options.MetadataStrippingMode)
        {
            case ImageMetadataStrippingMode.None:
                break;
            case ImageMetadataStrippingMode.ThumbnailOnly:
                StripThumbnailMetadata(image.Metadata);
                break;
            default:
                bool changed = StripAllMetadata(image.Metadata, orientation);

                if (Options.MetadataStrippingMode is ImageMetadataStrippingMode.Required)
                    hasRequiredChange |= changed || formatMapping.SourceFormat.HasExtraStrippableMetadata(image.Metadata);

                break;
        }

        context.CancellationToken.ThrowIfCancellationRequested();

        // Transparency support

        bool sourceSupportsTransparency = image.PixelType.AlphaRepresentation is not PixelAlphaRepresentation.None;
        bool resultSupportsTransparency = sourceSupportsTransparency && formatMapping.ResultFormat.SupportsTransparency;

        // Resize

        if (Options.Resize is not null)
            hasRequiredChange |= Resize(image, Options.Resize, Options.BackgroundColor, swapDimensions, resultSupportsTransparency);

        context.CancellationToken.ThrowIfCancellationRequested();

        // Background color

        if (sourceSupportsTransparency && !Options.BackgroundColor.SkipIfTransparencySupported)
        {
            image.Mutate(x => x.BackgroundColor(Options.BackgroundColor.ToLibColor()));
            hasRequiredChange = true;
        }

        context.CancellationToken.ThrowIfCancellationRequested();

        // Re-encode

        if (formatMapping.SourceFormat != formatMapping.ResultFormat)
            hasRequiredChange = true;

        string resultExtension = formatMapping.ResultFormat.Extensions.First();

        int sourceQuality = formatMapping.SourceFormat.GetJpegEquivalentQuality(info.Metadata);
        int resultQuality = Math.Min(sourceQuality, Options.Quality.ToJpegEquivalentQuality());

        (bool reencode, bool discardIfLarger) = Options.ReencodeMode switch {
            ImageReencodeMode.Always => (true, false),
            ImageReencodeMode.SelectSmallest => (true, !hasRequiredChange),
            ImageReencodeMode.AvoidReencoding => (hasRequiredChange, !hasRequiredChange),
            _ => throw new UnreachableException("Unexpected re-encode behavior."),
        };

        bool hasChanges = false;

        if (reencode)
        {
            // Estimate output size based on (result pixels / source pixels) and a 20% compression factor.
            long estimatedOutputLength = stream.Length * image.Width / info.Width * image.Height / info.Height * 4 / 5;
            var outputStream = new RecyclableMemoryStream(FileRepo.MemoryStreamManager, "FulcrumFS.Images", estimatedOutputLength);

            try
            {
                var resultCompressionLevel = Options.CompressionLevel;
                var resultEncoder = formatMapping.ResultFormat.GetEncoder(resultCompressionLevel, resultQuality, Options.MetadataStrippingMode);
                image.Save(outputStream, resultEncoder);
            }
            catch (Exception)
            {
                outputStream.Dispose();
                throw;
            }

            if (discardIfLarger && outputStream.Length > stream.Length)
            {
                outputStream.Dispose();
            }
            else
            {
                stream = outputStream;
                hasChanges = true;
            }
        }

        stream.Position = 0;
        return FileProcessingResult.Stream(stream, resultExtension, hasChanges);
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
            throw new FileProcessingException($"The image is too large. Maximum size is {validation.MaxWidth}x{validation.MaxHeight}.");

        if (pixels > validation.MaxPixels)
            throw new FileProcessingException("The image is too large.");

        if (width < validation.MinWidth || height < validation.MinHeight)
            throw new FileProcessingException($"The image is too small. Minimum size is {validation.MinWidth}x{validation.MinHeight}.");

        if (pixels < validation.MinPixels)
            throw new FileProcessingException("The image is too small.");
    }

    private static bool StripAllMetadata(ImageMetadata metadata, ushort orientation)
    {
        bool changed = false;

        if (metadata.XmpProfile is not null ||
            metadata.IccProfile is not null ||
            metadata.CicpProfile is not null ||
            metadata.IptcProfile is not null)
        {
            metadata.XmpProfile = null;
            metadata.IccProfile = null;
            metadata.CicpProfile = null;
            metadata.IptcProfile = null;

            changed = true;
        }

        if (metadata.ExifProfile is not null)
        {
            if (orientation is 1)
            {
                // Orientation is normal so can be stripped as well since it's the default.
                metadata.ExifProfile = null;
                changed = true;
            }
            else if (metadata.ExifProfile.Values.Count > 1)
            {
                // Image has more than just non-default orientation, create a new profile with just the orientation.
                metadata.ExifProfile = new ExifProfile();
                metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);

                changed = true;
            }
        }

        return changed;
    }

    private static void StripThumbnailMetadata(ImageMetadata metadata)
    {
        if (metadata.ExifProfile is not null)
        {
            metadata.ExifProfile.RemoveValue(ExifTag.JPEGInterchangeFormat);
            metadata.ExifProfile.RemoveValue(ExifTag.JPEGInterchangeFormatLength);
        }
    }

    private static bool Resize(
        Image image,
        ImageResizeOptions options,
        ImageBackgroundColor fallbackBgColor,
        bool swapDimensions,
        bool supportsTransparency)
    {
        if (GetResizeInfo(image, options, swapDimensions) is not { } resizeInfo)
            return false;

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
        return image.Width != beforeWidth || image.Height != beforeHeight;
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
}
