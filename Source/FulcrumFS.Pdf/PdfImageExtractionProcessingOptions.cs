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
    /// Default is <c>1</c> (the first page). If the document contains fewer pages than this value, a <see cref="FileProcessingException" /> is thrown when
    /// processing.</para>
    /// </summary>
    public int PageNumber
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(PageNumber));
            field = value;
        }
    } = 1;

    /// <summary>
    /// <para>
    /// Gets or initializes the resolution in dots per inch (DPI) to render the page at, based on the page's physical size.</para>
    /// <para>
    /// Default is <see langword="null" />, meaning the render size is determined solely by <see cref="MaxPixelSize" />. Must be between <c>1</c> and
    /// <c>2400</c> (inclusive) when specified. If <see cref="MaxPixelSize" /> is also specified, it caps the size of the rendered image, so that pages with
    /// large physical dimensions do not produce excessively large images.</para>
    /// <para>
    /// Note: at least one of <see cref="Dpi" /> or <see cref="MaxPixelSize" /> must be specified.</para>
    /// </summary>
    public int? Dpi
    {
        get;
        init
        {
            if (value is not null)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 1, nameof(Dpi));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Value, 2400, nameof(Dpi));
            }

            field = value;
        }
    }

    /// <summary>
    /// <para>
    /// Gets or initializes the maximum size in pixels of the longest edge of the extracted image. The other edge is sized to preserve the page's aspect
    /// ratio.</para>
    /// <para>
    /// Default is <c>2048</c>. Must be between <c>1</c> and <c>16384</c> (inclusive) when specified. If <see cref="Dpi" /> is not specified, the longest
    /// edge of the image is always rendered at exactly this size; otherwise this acts as a cap on the size of the image rendered at <see cref="Dpi" />.
    /// Since PDF pages are vector-based, this determines the size the page is rasterized at - chain an image processor after this processor if further
    /// resizing or format conversion is needed.</para>
    /// <para>
    /// Note: at least one of <see cref="Dpi" /> or <see cref="MaxPixelSize" /> must be specified.</para>
    /// </summary>
    public int? MaxPixelSize
    {
        get;
        init
        {
            if (value is not null)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 1, nameof(MaxPixelSize));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Value, 16384, nameof(MaxPixelSize));
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
    /// Default is <see langword="null" />, meaning no password is used. If the document is password-protected and the correct password is not provided, a
    /// <see cref="FileProcessingException" /> is thrown when processing.</para>
    /// </summary>
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
}
