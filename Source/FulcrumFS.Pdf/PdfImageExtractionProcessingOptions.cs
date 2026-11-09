namespace FulcrumFS.Pdf;

#pragma warning disable SA1513 // Closing brace should be followed by blank line (fires erroneously on property initializers after init accessors)
#pragma warning disable SA1623 // Property summary documentation should match accessors

/// <summary>
/// <para>
/// Specifies the options for extracting an image from a PDF document with a <see cref="PdfImageExtractionProcessor" />.</para>
/// <para>
/// The image is rendered from the page specified by <see cref="PageNumber" /> (the first page by default). If <see cref="Dpi" /> is specified, the page is
/// rendered at that resolution, capped so that the longest edge of the resulting image does not exceed <see cref="MaxPixelSize" /> (if specified).
/// Otherwise, the longest edge of the resulting image is sized to <see cref="MaxPixelSize" /> pixels. PDF pages are vector-based, so these options determine
/// the rasterization size rather than resizing an existing raster image.</para>
/// <para>
/// The extracted image format is PNG.</para>
/// <para>
/// Note: If no image is able to be extracted, then a <see cref="FileProcessingException" /> will be thrown when processing.</para>
/// </summary>
public sealed record PdfImageExtractionProcessingOptions
{
    /// <summary>
    /// Gets an options instance with standard PDF image extraction settings - extracts the first page with default settings.
    /// </summary>
    public static PdfImageExtractionProcessingOptions Standard { get; } = new();

    /// <summary>
    /// <para>
    /// Gets or initializes the 1-based page number of the PDF document to extract the image from.</para>
    /// <para>
    /// Must be at least <c>1</c>. Default is <c>1</c> (the first page).</para>
    /// </summary>
    /// <remarks>
    /// If the document contains fewer pages than this value, a <see cref="FileProcessingException" /> is thrown when processing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int PageNumber
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 1;

    /// <summary>
    /// <para>
    /// Gets or initializes the resolution in dots per inch (DPI) to render the page at, based on the page's physical size.</para>
    /// <para>
    /// Must be between <c>1</c> and <c>2400</c>. Default is <see langword="null" />, which means the render size is determined solely by
    /// <see cref="MaxPixelSize" />.</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// If <see cref="MaxPixelSize" /> is also specified, it caps the size of the rendered image, so that pages with large physical dimensions do not produce
    /// excessively large images.</para>
    /// <para>
    /// At least one of <see cref="Dpi" /> or <see cref="MaxPixelSize" /> must be specified.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or greater than 2400.</exception>
    public int? Dpi
    {
        get;
        init
        {
            if (value is not null)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 1, nameof(value));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Value, 2400, nameof(value));
            }

            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum size in pixels of the longest edge of the extracted image.</para>
    /// <para>
    /// Must be between <c>1</c> and <c>16384</c>. Default is <c>2048</c>.</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other edge is sized to preserve the page's aspect ratio.</para>
    /// <para>
    /// If <see cref="Dpi" /> is not specified, the longest edge of the image is always
    /// rendered at exactly this size; otherwise this acts as a cap on the size of the image rendered at <see cref="Dpi" />. Since PDF pages are
    /// vector-based, this determines the size the page is rasterized at - chain an image processor after this processor if further resizing or format
    /// conversion is needed.</para>
    /// <para>
    /// At least one of <see cref="Dpi" /> or <see cref="MaxPixelSize" /> must be specified.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or greater than 16384.</exception>
    public int? MaxPixelSize
    {
        get;
        init
        {
            if (value is not null)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 1, nameof(value));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Value, 16384, nameof(value));
            }

            field = value;
        }
    } = 2048;

    /// <summary>
    /// <para>
    /// Gets or initializes the background color rendered behind transparent regions of the page.</para>
    /// <para>
    /// Default is <see cref="PdfBackgroundColor.White" />.</para>
    /// </summary>
    public PdfBackgroundColor BackgroundColor { get; init; } = PdfBackgroundColor.White;

    /// <summary>
    /// <para>
    /// Gets or initializes the password used to open the PDF document.</para>
    /// <para>
    /// Default is <see langword="null" />, which means no password is used.</para>
    /// </summary>
    /// <remarks>
    /// If the document is password-protected and the correct password is not provided, a <see cref="FileProcessingException" /> is thrown when processing.
    /// </remarks>
    public string? Password { get; init; }

    /// <summary>
    /// <para>
    /// Gets or initializes a value indicating whether annotations are rendered in the extracted image.</para>
    /// <para>
    /// Default is <see langword="true" />.</para>
    /// </summary>
    public bool RenderAnnotations { get; init; } = true;

    /// <summary>
    /// <para>
    /// Gets or initializes a value indicating whether filled form fields are rendered in the extracted image.</para>
    /// <para>
    /// Default is <see langword="true" />.</para>
    /// </summary>
    public bool RenderFormFields { get; init; } = true;

    /// <summary>
    /// Gets the problems of the options, which the <see cref="PdfImageExtractionProcessor" /> constructor reports in one exception.
    /// </summary>
    internal List<string> GetProblems()
    {
        List<string> problems = [];

        if (Dpi is null && MaxPixelSize is null)
        {
            problems.Add(
                $"{nameof(PdfImageExtractionProcessingOptions)}.{nameof(Dpi)} and {nameof(MaxPixelSize)} are both null. At least one of them must be set.");
        }

        return problems;
    }
}
