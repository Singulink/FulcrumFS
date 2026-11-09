using System.Collections.Immutable;
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
    /// Gets an options instance that maps all supported image formats to themselves, preserving the original format of the source image. All remaining options
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
                throw new ArgumentException("The value is empty.", nameof(value));

            if (values.Any(f => f is null))
                throw new ArgumentException("The value contains null.", nameof(value));

            if (values.Select(f => f.SourceFormat).Distinct().Count() != value.Count)
                throw new ArgumentException("The value contains duplicate source formats.", nameof(value));

            field = EquatableArray.Create(values);
        }
    }

    /// <summary>
    /// Gets or initializes the options for validating the source image before processing. Default is <see langword="null"/>, which means the source image
    /// is not validated.
    /// </summary>
    /// <exception cref="ArgumentException">A minimum of the value is greater than its maximum. The message lists every problem.</exception>
    public ImageSourceValidationOptions? SourceValidation
    {
        get;
        init {
            if (value?.GetProblems() is { Count: > 0 } problems)
                throw new ArgumentException(string.Join(" ", problems), nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum number of frames to process in a multi-frame image (e.g., animated GIFs). Must be at least 1. Default is
    /// <see cref="int.MaxValue"/>, which means all frames will be processed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxFrames
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = int.MaxValue;

    /// <summary>
    /// Gets or initializes a value indicating if the image should be reoriented to normal orientation based on its EXIF data. Default is
    /// <see cref="ImageReorientMode.None"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="ImageReorientMode"/> value.</exception>
    public ImageReorientMode ReorientMode
    {
        get;
        init {
            value.ThrowIfNotDefined();
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the metadata stripping mode. Default is <see cref="ImageMetadataStrippingMode.ThumbnailOnly"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="ImageMetadataStrippingMode"/> value.</exception>
    public ImageMetadataStrippingMode MetadataStrippingMode
    {
        get;
        init {
            value.ThrowIfNotDefined();
            field = value;
        }
    } = ImageMetadataStrippingMode.ThumbnailOnly;

    /// <summary>
    /// Gets or initializes the options for resizing the image. A value of <see langword="null"/> indicates that the image will not be resized. Default is
    /// <see langword="null"/>.
    /// </summary>
    public ImageResizeOptions? Resize { get; init; }

    /// <summary>
    /// Gets or initializes the background color to use for the image. Default is white <c>(RGB: 255, 255, 255)</c> with
    /// <see cref="ImageBackgroundColor.SkipIfTransparencySupported"/> set to <see langword="true"/>.
    /// </summary>
    public ImageBackgroundColor BackgroundColor { get; init; } = new ImageBackgroundColor(255, 255, 255, true);

    /// <summary>
    /// Gets or initializes the compression level to use when encoding images. Default is <see cref="ImageCompressionLevel.High"/>.
    /// </summary>
    /// <remarks>
    /// This setting only affects the computational effort of compression (and thus the resulting file size), not the visual quality. The setting has no
    /// effect on formats that do not support compression.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="ImageCompressionLevel"/> value.</exception>
    public ImageCompressionLevel CompressionLevel
    {
        get;
        init {
            value.ThrowIfNotDefined();
            field = value;
        }
    } = ImageCompressionLevel.High;

    /// <summary>
    /// Gets or initializes the quality setting to use when encoding images. Default is <see cref="ImageQuality.High"/>.
    /// </summary>
    /// <remarks>
    /// This setting has no effect on lossless formats. When source image quality can be determined, the result quality is capped to the source quality to
    /// avoid unnecessarily increasing file size.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="ImageQuality"/> value.</exception>
    public ImageQuality Quality
    {
        get;
        init {
            value.ThrowIfNotDefined();
            field = value;
        }
    } = ImageQuality.High;

    /// <summary>
    /// Gets or initializes the mode for re-encoding images. Default is <see cref="ImageReencodeMode.SelectSmallest"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="ImageReencodeMode"/> value.</exception>
    public ImageReencodeMode ReencodeMode
    {
        get;
        init {
            value.ThrowIfNotDefined();
            field = value;
        }
    } = ImageReencodeMode.SelectSmallest;
}
