using Singulink.Enums;

namespace FulcrumFS.Images;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides configuration options for processing images in a file repository.
/// </summary>
public class ImageProcessorOptions
{
    /// <summary>
    /// Gets or initializes the collection of per-format image processing options. The default value contains an entry for every supported image format and maps
    /// each source format to itself (no format conversion).
    /// </summary>
    /// <remarks>
    /// Each entry in the collection specifies how to handle a specific image format during processing. Individual entries can optionally override the image
    /// processor's <see cref="Quality"/>, <see cref="CompressionLevel"/>, and <see cref="ReencodeBehavior"/> settings for their specific source format. The collection
    /// must not be empty and cannot contain duplicate source formats. Only source formats included in the collection are supported by the image processor.
    /// </remarks>
    public IReadOnlyList<ImageFormatProcessingOptions> Formats
    {
        get;
        init {
            IReadOnlyList<ImageFormatProcessingOptions> v = [.. value];

            if (v.Count is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (v.Any(f => f is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            if (v.Select(f => f.SourceFormat).Distinct().Count() != value.Count)
                throw new ArgumentException("Formats cannot contain duplicate source formats.", nameof(value));

            field = value;
        }
    } = [.. ImageFormat.AllFormats.Select(f => new ImageFormatProcessingOptions(f))];

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

#pragma warning disable SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether the image should be rotated to normal orientation based on its EXIF data. If set to <see
    /// langword="true"/> and a rotation was performed, the EXIF orientation metadata is adjusted accordingly. Default is <see langword="false"/>.
    /// </summary>
    public bool OrientToNormal { get; init; }

#pragma warning restore SA1623

    /// <summary>
    /// Gets or initializes the metadata stripping mode. Default is <see cref="StripImageMetadataMode.ThumbnailOnly"/>.
    /// </summary>
    public StripImageMetadataMode StripMetadata
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = StripImageMetadataMode.ThumbnailOnly;

    /// <summary>
    /// Gets or initializes the options for resizing the image. If set to <see langword="null"/>, the image will not be resized.
    /// </summary>
    public ImageResizeOptions? Resize { get; init; }

    /// <summary>
    /// Gets or initializes the background color to use for the image. Default is white <c>(RGB: 255, 255, 255)</c> with <see
    /// cref="BackgroundColor.SkipIfTransparencySupported"/> set to <see langword="true"/>.
    /// </summary>
    public BackgroundColor BackgroundColor { get; init; } = new BackgroundColor(255, 255, 255, true);

    /// <summary>
    /// Gets or initializes the compression level to use for resulting images. This setting only affects the computational effort of compression (and thus the
    /// resulting file size), not the visual quality. The setting has no effect on formats that do not support compression. Can be overridden per-format by
    /// setting <see cref="ImageFormatProcessingOptions.CompressionLevelOverride"/> in the <see cref="Formats"/> collection. The default setting is <see
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
    /// Gets or initializes the quality to use for resulting images. Valid values are <c>1</c> to <c>100</c> (inclusive). This setting has no effect on lossless
    /// formats. When source image quality can be determined, the result quality is capped to the source quality to avoid unnecessarily increasing file size.
    /// Default setting is <c>82</c>.
    /// </summary>
    public int Quality
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100, nameof(value));
            field = value;
        }
    } = 82;

    /// <summary>
    /// Gets or initializes the behavior for re-encoding images. Default setting is <see cref="ImageReencodeBehavior.DiscardLargerUnlessMetadataChanged"/>.
    /// </summary>
    public ImageReencodeBehavior ReencodeBehavior
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ImageReencodeBehavior.DiscardLargerUnlessMetadataChanged;
}
