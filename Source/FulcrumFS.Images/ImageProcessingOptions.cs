using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Singulink.Collections;
using Singulink.Enums;

namespace FulcrumFS.Images;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Specifies the options for processing images with an <see cref="ImageProcessor"/>.
/// </summary>
public sealed record ImageProcessingOptions
{
    /// <summary>
    /// Gets an options instance that maps all supported image formats to themselves, preserving the original format the source image. All remaining options
    /// are left to their default values.
    /// </summary>
    public static ImageProcessingOptions Preserve { get; } = new() {
        Formats = [..ImageFormat.AllFormats.Select(f => new ImageFormatMapping(f))],
    };

    /// <summary>
    /// Gets or initializes the collection of image format mappings.
    /// </summary>
    /// <remarks>
    /// Each entry in the collection specifies how source image formats should be mapped to result formats. The collection must not be empty and cannot
    /// contain duplicate source formats. Only source formats included in the collection are supported by the image processor.
    /// </remarks>
    public required IReadOnlyList<ImageFormatMapping> Formats
    {
        get;
        init {
            ImmutableArray<ImageFormatMapping> values = [.. value];

            if (values.Length is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (values.Any(f => f is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            if (values.Select(f => f.SourceFormat).Distinct().Count() != value.Count)
                throw new ArgumentException("Formats cannot contain duplicate source formats.", nameof(value));

            field = EquatableArray.Create(values);
        }
    }

    /// <summary>
    /// Gets or initializes the options for validating the source image before processing.
    /// </summary>
    public ImageSourceValidationOptions? SourceValidation { get; init; }

    /// <summary>
    /// Gets or initializes the maximum number of frames to process in a multi-frame image (e.g., animated GIFs). Default is <see cref="int.MaxValue"/>, meaning
    /// all frames will be processed.
    /// </summary>
    public int MaxFrames
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxFrames));
            field = value;
        }
    } = int.MaxValue;

    /// <summary>
    /// Gets or initializes a value indicating if the image should be reoriented to normal orientation based on its EXIF data. Default is <see
    /// cref="ImageReorientMode.None"/>.
    /// </summary>
    public ImageReorientMode ReorientMode { get; init; }

    /// <summary>
    /// Gets or initializes the metadata stripping mode. Default is <see cref="ImageMetadataStrippingMode.ThumbnailOnly"/>.
    /// </summary>
    public ImageMetadataStrippingMode MetadataStrippingMode
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ImageMetadataStrippingMode.ThumbnailOnly;

    /// <summary>
    /// Gets or initializes the options for resizing the image. A value of <see langword="null"/> indicates that the image will not be resized. Default is <see
    /// langword="null"/>.
    /// </summary>
    public ImageResizeOptions? Resize { get; init; }

    /// <summary>
    /// Gets or initializes the background color to use for the image. Default is white <c>(RGB: 255, 255, 255)</c> with <see
    /// cref="ImageBackgroundColor.SkipIfTransparencySupported"/> set to <see langword="true"/>.
    /// </summary>
    public ImageBackgroundColor BackgroundColor { get; init; } = new ImageBackgroundColor(255, 255, 255, true);

    /// <summary>
    /// Gets or initializes the compression level to use when encoding images. This setting only affects the computational effort of compression (and thus the
    /// resulting file size), not the visual quality. The setting has no effect on formats that do not support compression. The default setting is <see
    /// cref="ImageCompressionLevel.High"/>.
    /// </summary>
    public ImageCompressionLevel CompressionLevel
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ImageCompressionLevel.High;

    /// <summary>
    /// Gets or initializes the quality setting to use when encoding images. This setting has no effect on lossless formats. When source image quality can be
    /// determined, the result quality is capped to the source quality to avoid unnecessarily increasing file size. The default setting is <see
    /// cref="ImageQuality.High"/>.
    /// </summary>
    public ImageQuality Quality
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ImageQuality.High;

    /// <summary>
    /// Gets or initializes the mode for re-encoding images. Default setting is <see cref="ImageReencodeMode.SelectSmallest"/>.
    /// </summary>
    public ImageReencodeMode ReencodeMode
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ImageReencodeMode.SelectSmallest;
}
