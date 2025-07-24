using System.Collections.Immutable;
using Singulink.Enums;

namespace FulcrumFS.Images;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Options for processing images in a file repository.
/// </summary>
public class ImageProcessOptions
{
    internal ImmutableArray<ImageFormatOptions> FormatsInternal { get; private init; } =
        ImageFormat.All.Select(f => new ImageFormatOptions(f)).ToImmutableArray();

    /// <summary>
    /// Gets or initializes the image format options for the formats that the processor will process. Defaults to all supported formats with default options.
    /// </summary>
    public IReadOnlyList<ImageFormatOptions> Formats
    {
        get => FormatsInternal;
        init {
            var array = value.ToImmutableArray();

            if (array.Length is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (array.Any(f => f is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            FormatsInternal = array;
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

#pragma warning disable SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether the image should be rotated to normal orientation based on its EXIF data. If set to <see
    /// langword="true"/> and a rotation was performed, the EXIF orientation metadata is adjusted accordingly. Default is <see langword="false"/>.
    /// </summary>
    public bool OrientToNormal { get; init; }

#pragma warning restore SA1623

    /// <summary>
    /// Gets or initializes the metadata stripping mode. Default is <see cref="StripMetadataMode.ThumbnailOnly"/>.
    /// </summary>
    public StripMetadataMode StripMetadata
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = StripMetadataMode.ThumbnailOnly;

    /// <summary>
    /// Gets or initializes the options for resizing the image. If set to <see langword="null"/>, the image will not be resized.
    /// </summary>
    public ImageResizeOptions? Resize { get; init; }

    /// <summary>
    /// Gets or initializes the background color to use for the image. Default is white <c>(RGB: 255, 255, 255)</c> with <see
    /// cref="BackgroundColor.SkipIfTransparencySupported"/> set to <see langword="true"/>.
    /// </summary>
    public BackgroundColor BackgroundColor { get; init; } = new BackgroundColor(255, 255, 255, true);
}
