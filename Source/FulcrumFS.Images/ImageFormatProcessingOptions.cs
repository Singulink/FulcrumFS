using Singulink.Enums;

namespace FulcrumFS.Images;

/// <summary>
/// Provides options that control how a specific source image format is processed and the result format that is produced.
/// </summary>
public sealed class ImageFormatProcessingOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImageFormatProcessingOptions"/> class with the specified source and result image formats. If <paramref
    /// name="resultFormat"/> is <see langword="null"/>, <paramref name="sourceFormat"/> will be used as the result format.
    /// </summary>
    /// <param name="sourceFormat">The source format to use.</param>
    /// <param name="resultFormat">The result format to convert to, or <see langword="null"/> to keep the source format.</param>
    public ImageFormatProcessingOptions(ImageFormat sourceFormat, ImageFormat? resultFormat = null)
    {
        SourceFormat = sourceFormat;
        ResultFormat = resultFormat ?? sourceFormat;
    }

    /// <summary>
    /// Gets the source image format for this mapping.
    /// </summary>
    public ImageFormat SourceFormat { get; }

    /// <summary>
    /// Gets the result image format for this mapping.
    /// </summary>
    public ImageFormat ResultFormat { get; }

    /// <summary>
    /// Gets or initializes an optional compression level override for the image processor's <see cref="ImageProcessorOptions.CompressionLevel"/> setting when
    /// processing images in the configured source format.
    /// </summary>
    public ImageCompressionLevel? CompressionLevelOverride {
        get;
        init {
            value?.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes an optional quality override for the image processor's <see cref="ImageProcessorOptions.Quality"/> setting when
    /// processing images in the configured source format.
    /// </summary>
    public int? QualityOverride
    {
        get;
        init {
            if (value is int v)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(v, 1, nameof(value));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(v, 100, nameof(value));
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes an optional re-encode behavior override for the image processor's <see cref="ImageProcessorOptions.ReencodeBehavior"/> setting when
    /// processing images in the configured source format.
    /// </summary>
    public ImageReencodeBehavior? ReencodeOverride { get; init; }
}
