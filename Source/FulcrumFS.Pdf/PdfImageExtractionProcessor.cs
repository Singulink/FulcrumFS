using System.Runtime.Versioning;
using Microsoft.IO;
using PDFtoImage;
using SkiaSharp;

namespace FulcrumFS.Pdf;

#pragma warning disable SA1642 // Constructor summary documentation should begin with standard text

/// <summary>
/// Provides functionality to extract images from PDF documents (e.g. to generate thumbnails) by rendering a page to a PNG image using the PDFium renderer.
/// </summary>
[SupportedOSPlatform("Windows")]
[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("macOS")]
[SupportedOSPlatform("iOS13.6")]
[SupportedOSPlatform("MacCatalyst13.5")]
[SupportedOSPlatform("Android31.0")]
public sealed class PdfImageExtractionProcessor : FileProcessor
{
    private const int MaxInMemoryCopySize = 20 * 1024 * 1024;

    /// <summary>
    /// <para>
    /// Initializes a new instance of the <see cref="PdfImageExtractionProcessor"/> class with the specified options.</para>
    /// <para>
    /// Note: if you want to do source PDF validation, you need to use a <see cref="FileFormatValidationProcessor" /> configured with <see
    /// cref="FileFormat.Pdf" /> first and chain this after it, as this class does not perform any validation itself, it just extracts an image from the
    /// provided PDF document.</para>
    /// <para>
    /// Note: PDF rendering is serialized process-wide because the underlying PDFium library is not thread-safe, so only one PDF page can be rendered at a
    /// time within the process.</para>
    /// </summary>
    public PdfImageExtractionProcessor(PdfImageExtractionProcessingOptions options)
    {
        if (options.Dpi is null && options.MaxPixelSize is null)
        {
            throw new ArgumentException(
                $"At least one of {nameof(options.Dpi)} or {nameof(options.MaxPixelSize)} must be specified in the options.", nameof(options));
        }

        Options = options;
    }

    /// <summary>
    /// Gets the options used to configure this <see cref="PdfImageExtractionProcessor" />.
    /// </summary>
    public PdfImageExtractionProcessingOptions Options { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<string> AllowedFileExtensions { get; } = [".pdf"];

    /// <inheritdoc/>
    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        var pdfStream = await context.GetSourceAsSeekableStreamAsync(preferInMemory: false, MaxInMemoryCopySize).ConfigureAwait(false);

        context.CancellationToken.ThrowIfCancellationRequested();

        // PDFium rendering is synchronous (and serialized process-wide by PDFtoImage since PDFium is not thread-safe), so run it on the thread pool.
        var outputStream = await Task.Run(() => ExtractImage(pdfStream), context.CancellationToken).ConfigureAwait(false);

        if (outputStream.Length is 0)
        {
            await outputStream.DisposeAsync().ConfigureAwait(false);
            throw new FileProcessingException("Failed to render an image from the PDF document (no image data was written).");
        }

        outputStream.Position = 0;
        return FileProcessingResult.Stream(outputStream, ".png", hasChanges: true);
    }

    private RecyclableMemoryStream ExtractImage(Stream pdfStream)
    {
        int pageIndex = Options.PageNumber - 1;
        System.Drawing.SizeF pageSize;

        try
        {
            pdfStream.Position = 0;
            int pageCount = Conversion.GetPageCount(pdfStream, leaveOpen: true, password: Options.Password);

            if (pageIndex >= pageCount)
            {
                throw new FileProcessingException(
                    $"Cannot extract an image from page {Options.PageNumber} of the PDF document because it only contains {pageCount} page(s).");
            }

            pdfStream.Position = 0;
            pageSize = Conversion.GetPageSize(pdfStream, page: pageIndex, leaveOpen: true, password: Options.Password);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FileProcessingException)
        {
            throw new FileProcessingException("Failed to read PDF document information.", ex);
        }

        // Page sizes are in PDF points (1/72 inch). Pages are vector-based, so determine the rasterization size: render at Dpi if specified (capped by
        // MaxPixelSize if also specified), otherwise size the longest edge to MaxPixelSize. When sizing by pixels, the renderer calculates the other edge to
        // preserve the page's aspect ratio.
        double aspectRatio = pageSize.Width > 0 && pageSize.Height > 0 ? pageSize.Width / pageSize.Height : 1;

        int? width = null;
        int? height = null;
        long estimatedWidth;
        long estimatedHeight;

        if (Options.Dpi is int dpi &&
            (Options.MaxPixelSize is not int maxPixelSize || double.Max(pageSize.Width, pageSize.Height) * dpi / 72.0 <= maxPixelSize))
        {
            // Render at the specified DPI (either uncapped or within the cap):
            estimatedWidth = (long)double.Round(pageSize.Width * dpi / 72.0);
            estimatedHeight = (long)double.Round(pageSize.Height * dpi / 72.0);
        }
        else
        {
            // Size the longest edge to MaxPixelSize (either as the primary sizing mode or as the cap on DPI rendering):
            int maxSize = Options.MaxPixelSize!.Value;

            if (pageSize.Width >= pageSize.Height)
                width = maxSize;
            else
                height = maxSize;

            estimatedWidth = width ?? (long)double.Round(maxSize * aspectRatio);
            estimatedHeight = height ?? (long)double.Round(maxSize / aspectRatio);
        }

        var renderOptions = new RenderOptions {
            Dpi = Options.Dpi ?? 300, // Ignored when Width/Height are specified.
            Width = width,
            Height = height,
            WithAspectRatio = width is not null || height is not null,
            WithAnnotations = Options.RenderAnnotations,
            WithFormFill = Options.RenderFormFields,
            BackgroundColor = new SKColor(Options.BackgroundColor.R, Options.BackgroundColor.G, Options.BackgroundColor.B),
        };

        // Estimate output length from the output pixel dimensions with a ~1 byte/pixel PNG compression factor.
        long estimatedOutputLength = estimatedWidth * estimatedHeight;

        var outputStream = new RecyclableMemoryStream(FileRepo.MemoryStreamManager, "FulcrumFS.Pdf", estimatedOutputLength);

        try
        {
            pdfStream.Position = 0;
            Conversion.SavePng(outputStream, pdfStream, page: pageIndex, leaveOpen: true, password: Options.Password, options: renderOptions);
        }
        catch (Exception ex)
        {
            outputStream.Dispose();

            if (ex is OperationCanceledException or FileProcessingException)
                throw;

            throw new FileProcessingException("Failed to render an image from the PDF document.", ex);
        }

        return outputStream;
    }
}
