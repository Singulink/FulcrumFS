<div class="article">

# Document PDF Conversion

The `Singulink.FulcrumFS.Documents` package adds document to PDF conversion built on LibreOffice. It converts word processing documents, spreadsheets and presentations (e.g. `.docx`, `.xlsx`, `.pptx` and their legacy/OpenDocument counterparts) to PDF, which is typically stored as a variant of the original document so it can be previewed in a browser or archived in a stable format. This guide covers installing and configuring LibreOffice, the conversion processor, and a practical end-to-end example for a document upload endpoint.

### Where it fits

<xref:FulcrumFS.Documents.DocumentPdfConversionProcessor> is a <xref:FulcrumFS.FileProcessor>, so it composes into pipelines and variants. Add `using FulcrumFS.Documents;` alongside `using FulcrumFS;`.

> [!NOTE]
> Document conversion shells out to LibreOffice, which can take several seconds per file and can consume a significant amount of memory per process. For high-volume upload endpoints, consider generating the PDF variant from a queued background service rather than during the upload request.

## Installing LibreOffice

Conversion runs LibreOffice's `soffice` executable in headless mode, so the host needs a LibreOffice installation, but no display or desktop session. LibreOffice 7 or later is recommended. Only the Writer, Calc and Impress components are used, for word processing documents, spreadsheets and presentations respectively, and each is only needed for the formats it opens.

| Platform | Install with | Directory to configure |
| --- | --- | --- |
| Windows | The installer from [libreoffice.org](https://www.libreoffice.org/download/download-libreoffice/), or `winget install --id TheDocumentFoundation.LibreOffice` | `C:\Program Files\LibreOffice\program` |
| Debian, Ubuntu | `sudo apt-get install --no-install-recommends libreoffice-writer libreoffice-calc libreoffice-impress` | `/usr/lib/libreoffice/program` |
| macOS | The installer from libreoffice.org, or `brew install --cask libreoffice` | `/Applications/LibreOffice.app/Contents/MacOS` |

On other Linux distributions, install the distribution's LibreOffice packages and configure the directory containing `soffice` (or `/usr/bin`, if the packages put a link to it there).

Documents are rendered with the fonts installed on the host, and LibreOffice substitutes other fonts for any that are missing, which changes the layout of the documents that use them. Servers, and Linux containers in particular, often have few fonts installed. Metric-compatible replacements for the most common Microsoft Office fonts keep the layout of most documents intact; on Debian and Ubuntu they are in the `fonts-liberation` (Arial, Times New Roman, Courier New), `fonts-crosextra-carlito` (Calibri) and `fonts-crosextra-caladea` (Cambria) packages. Install any other fonts your users' documents commonly use as well.

In a container image based on a Debian or Ubuntu .NET image, install LibreOffice in the final image, which adds several hundred megabytes to it:

```dockerfile
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-writer libreoffice-calc libreoffice-impress \
       fonts-liberation fonts-crosextra-carlito fonts-crosextra-caladea \
    && rm -rf /var/lib/apt/lists/*
```

## Configuring LibreOffice

Conversion requires a LibreOffice installation on the host, so you must point the library at the directory containing the `soffice` executable once at startup, before constructing any processor. Call <xref:FulcrumFS.Documents.DocumentPdfConversionProcessor.ConfigureWithLibreOffice*> with the directory and optional configuration options.

```csharp
using FulcrumFS;
using FulcrumFS.Documents;
using Singulink.IO;

// In Program.cs or your composition root.
var libreOfficeDir = DirectoryPath.ParseAbsolute(@"C:\Program Files\LibreOffice\program");
DocumentPdfConversionProcessor.ConfigureWithLibreOffice(libreOfficeDir, new() { MaxConcurrentProcesses = 2 });
```

On Windows this is the LibreOffice `program` directory (containing `soffice.com` / `soffice.exe`); on Linux and macOS it is the directory containing the `soffice` executable (e.g. `/usr/bin`, `/usr/lib/libreoffice/program`, or `/Applications/LibreOffice.app/Contents/MacOS`).

The optional `MaxConcurrentProcesses` configuration option caps how many LibreOffice processes run at once, which protects a server from being overwhelmed when many users upload documents at the same time. Each conversion runs in an isolated LibreOffice user profile, so conversions can run concurrently without contending on shared state - but each process can consume a significant amount of memory, so consider a conservative cap on memory-constrained hosts.

There are also additional configuration options that can be used:
- `ProcessorAffinity` (only Windows and Linux) sets the CPU processor / hardware thread affinity mask for the LibreOffice processes - see `Process.ProcessorAffinity` for more information.
- `ProcessPriorityClass` sets the priority class for the LibreOffice processes, to ensure that other tasks have higher priority for example (by setting it to `BelowNormal`) - see `Process.ProcessPriorityClass` for more information.

> [!IMPORTANT]
> Constructing a <xref:FulcrumFS.Documents.DocumentPdfConversionProcessor> before configuring the LibreOffice path throws, so a misconfigured deployment fails fast at startup rather than on the first upload.

## The Conversion Processor

Construct a <xref:FulcrumFS.Documents.DocumentPdfConversionProcessor> with a <xref:FulcrumFS.Documents.DocumentPdfConversionProcessingOptions>. The predefined <xref:FulcrumFS.Documents.DocumentPdfConversionProcessingOptions.Standard> options accept all the built-in word processing, spreadsheet and presentation formats:

```csharp
var processor = new DocumentPdfConversionProcessor(DocumentPdfConversionProcessingOptions.Standard);
```

To restrict the accepted source formats, initialize `SourceFormats` with the formats you want to allow. The processor's allowed file extensions are derived from the source formats, so anything else is rejected up front with a <xref:FulcrumFS.FileProcessingException>:

```csharp
// Only accept Word documents.
var processor = new DocumentPdfConversionProcessor(new() {
    SourceFormats = [FileFormat.Doc, FileFormat.Docx],
});
```

The processor does not validate the content of source files - it only converts them. Chain a <xref:FulcrumFS.FileFormatValidationProcessor> before it (or validate the main file when it is added) when uploads cannot be trusted.

## Storing an Upload with a PDF Variant

The typical pattern stores the uploaded document as the main file (validated against its declared format) and generates the PDF as a variant. Chaining a <xref:FulcrumFS.Pdf.PdfImageExtractionProcessor> and <xref:FulcrumFS.Images.ImageProcessor> onto the converted PDF also produces a thumbnail, mirroring how PDF uploads are usually handled:

```csharp
// Validate that the upload really is a Word document.
var validationPipeline = new FileFormatValidationProcessor(
    new FileFormatValidationOptions(FileFormat.Doc, FileFormat.Docx)).ToPipeline();

// PDF variant with a nested 256x256 JPEG thumbnail variant rendered from it.
var pdfPipeline = new DocumentPdfConversionProcessor(DocumentPdfConversionProcessingOptions.Standard)
    .ToPipeline()
    .WithVariant("thumbnail", new FileProcessingPipeline(
        new PdfImageExtractionProcessor(new() { MaxPixelSize = 512 }),
        new ImageProcessor(new ImageProcessingOptions {
            Formats = [new ImageFormatMapping(ImageFormat.Png, ImageFormat.Jpeg)],
            Resize = new ImageResizeOptions(ImageResizeMode.FitDown, 256, 256),
        })));

await using var txn = await repo.BeginTransactionAsync();

var added = await txn.AddAsync(source, ".docx", leaveOpen: true, validationPipeline);
await repo.AddVariantAsync(added.FileId, "pdf", pdfPipeline);

// added.FileId main file -> original .docx
// "pdf" variant          -> converted PDF
// "thumbnail" variant    -> 256x256 JPEG preview of the first page

await txn.CommitAsync();
```

The application can then serve the original document for download, the PDF variant for in-browser preview, and the thumbnail for gallery tiles, with no extra processing at request time. See [File Variants](file-variants.md).

> [!TIP]
> Conversion fidelity depends on LibreOffice's import filters. Documents that rely on fonts not installed on the host are rendered with substituted fonts, so install the fonts your users' documents commonly use for best results (see [Installing LibreOffice](#installing-libreoffice)).

## Next Steps

- [Validating File Formats](file-formats.md) - Validating uploads against their declared format.
- [File Variants](file-variants.md) - Producing alternate renditions alongside the main file.
- [Processing Pipelines](processing-pipelines.md) - Routing and composing processors.

</div>
