namespace FulcrumFS.Images;

/// <summary>
/// Options for validating a source image during processing.
/// </summary>
public sealed record ImageSourceValidationOptions
{
    /// <summary>
    /// Gets or initializes the maximum width of the source image in pixels. Must be at least 1. Default is <see langword="null"/>, which means no maximum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxWidth
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum height of the source image in pixels. Must be at least 1. Default is <see langword="null"/>, which means no maximum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxHeight
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum number of pixels in the source image. Must be at least 1. Default is <see langword="null"/>, which means no maximum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxPixels
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum width of the source image in pixels. Must be at least 1. Default is <see langword="null"/>, which means no minimum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MinWidth
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum height of the source image in pixels. Must be at least 1. Default is <see langword="null"/>, which means no minimum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MinHeight
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the minimum number of pixels in the source image. Must be at least 1. Default is <see langword="null"/>, which means no minimum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MinPixels
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets the problems of the options, where a minimum is greater than its maximum, which <see cref="ImageProcessingOptions"/> reports in one exception.
    /// </summary>
    internal List<string> GetProblems()
    {
        List<string> problems = [];

        if (MinWidth > MaxWidth)
            problems.Add($"{nameof(ImageSourceValidationOptions)}.{nameof(MinWidth)} ({MinWidth}) is greater than {nameof(MaxWidth)} ({MaxWidth}).");

        if (MinHeight > MaxHeight)
            problems.Add($"{nameof(ImageSourceValidationOptions)}.{nameof(MinHeight)} ({MinHeight}) is greater than {nameof(MaxHeight)} ({MaxHeight}).");

        if (MinPixels > MaxPixels)
            problems.Add($"{nameof(ImageSourceValidationOptions)}.{nameof(MinPixels)} ({MinPixels}) is greater than {nameof(MaxPixels)} ({MaxPixels}).");

        return problems;
    }
}
