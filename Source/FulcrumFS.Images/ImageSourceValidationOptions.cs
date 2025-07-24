namespace FulcrumFS.Images;

/// <summary>
/// Options for validating a source image during processing.
/// </summary>
public class ImageSourceValidationOptions
{
    /// <summary>
    /// Gets or initializes the maximum width of the source image in pixels.
    /// </summary>
    public int? MaxWidth
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxWidth));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum height of the source image in pixels.
    /// </summary>
    public int? MaxHeight
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxWidth));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum number of pixels in the source image.
    /// </summary>
    public int? MaxPixels
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxWidth));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum width of the source image in pixels.
    /// </summary>
    public int? MinWidth
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinWidth));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum height of the source image in pixels.
    /// </summary>
    public int? MinHeight
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinHeight));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum number of pixels in the source image.
    /// </summary>
    public int? MinPixels
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MinPixels));
            field = value;
        }
    }
}
