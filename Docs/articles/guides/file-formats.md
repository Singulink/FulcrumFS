<div class="article">

# Validating File Formats

FulcrumFS can verify that a file's actual content matches its claimed type before it is stored, rejecting mismatched or malformed uploads. This guide covers the format validation processor and the standalone <xref:FulcrumFS.FileFormat> API.

### Why validate content

An extension or a client-supplied MIME type is just a label and can be wrong or malicious. A user might rename `malware.exe` to `document.pdf`, or a browser might mislabel a download. <xref:FulcrumFS.FileFormat> inspects the actual bytes (magic numbers and structural checks) to confirm the format, so a `.pdf` that is really something else is caught at the boundary rather than ending up in storage, in a backup, and eventually back out to another user.

> [!CAUTION]
> Never rely on the extension or MIME type alone for security-sensitive decisions. A pipeline that stores user uploads without verifying the content is trusting a value the client controls. For images and videos, use <xref:FulcrumFS.Images.ImageProcessor> or <xref:FulcrumFS.Videos.VideoProcessor> (which both validate as part of decoding); for other content types, add a <xref:FulcrumFS.FileFormatValidationProcessor> to the pipeline.

### When to reach for the validation processor

<xref:FulcrumFS.FileFormatValidationProcessor> is a lightweight check based on file headers and structural sanity. It is the right choice when:

- You accept content types that no FulcrumFS processor handles (PDFs, Office documents, ZIPs, plain text, etc.) and just need to confirm the bytes match the declared extension before storing.
- You want a cheap, dependency-free guard in a project that does not need full ImageSharp or FFmpeg processing.

It is *not* needed alongside <xref:FulcrumFS.Images.ImageProcessor> or <xref:FulcrumFS.Videos.VideoProcessor>: those processors are supersets of the validator for their respective formats. They fully decode the source (which is a much stronger check than a header sanity scan) and reject malformed inputs as part of normal processing.

## The Validation Processor

Add a <xref:FulcrumFS.FileFormatValidationProcessor> to a pipeline to reject content that does not match an allowed format. Configure it with <xref:FulcrumFS.FileFormatValidationOptions>, passing the formats you accept.

```csharp
// Document upload endpoint: accept PDFs and DOCX, reject anything else.
var pipeline = new FileProcessingPipeline(
    new FileFormatValidationProcessor(
        new FileFormatValidationOptions(FileFormat.Pdf, FileFormat.Docx)));

var added = await txn.AddAsync(source, ".pdf", leaveOpen: true, pipeline);
```

When the content matches none of the allowed formats, the processor fails the add with a <xref:FulcrumFS.FileProcessingException>, which an upload handler can translate into an HTTP 400. See [Exception Handling](exception-handling.md).

## Built-In Formats

<xref:FulcrumFS.FileFormat> exposes static singletons for common types:

- **Images:** <xref:FulcrumFS.FileFormat.Jpeg>, <xref:FulcrumFS.FileFormat.Png>, <xref:FulcrumFS.FileFormat.Gif>, <xref:FulcrumFS.FileFormat.WebP>, <xref:FulcrumFS.FileFormat.Bmp>, <xref:FulcrumFS.FileFormat.Tiff>, <xref:FulcrumFS.FileFormat.Heic>, <xref:FulcrumFS.FileFormat.Heif>, <xref:FulcrumFS.FileFormat.Avif>.
- **Video and audio:** <xref:FulcrumFS.FileFormat.Mp4>, <xref:FulcrumFS.FileFormat.Mov>, <xref:FulcrumFS.FileFormat.Mkv>, <xref:FulcrumFS.FileFormat.WebM>, <xref:FulcrumFS.FileFormat.M4a>, plus the 3GPP family (<xref:FulcrumFS.FileFormat.Tgp>, <xref:FulcrumFS.FileFormat.Tg2>) and <xref:FulcrumFS.FileFormat.Mj2>.
- **Documents:** <xref:FulcrumFS.FileFormat.Pdf>, the OOXML family (<xref:FulcrumFS.FileFormat.Docx>, <xref:FulcrumFS.FileFormat.Xlsx>, <xref:FulcrumFS.FileFormat.Pptx>), the OpenDocument family (<xref:FulcrumFS.FileFormat.Odt>, <xref:FulcrumFS.FileFormat.Ods>), and the legacy <xref:FulcrumFS.FileFormat.Doc> binary format.
- **Archives:** <xref:FulcrumFS.FileFormat.Zip>.

Each declares its <xref:FulcrumFS.FileFormat.Name>, its <xref:FulcrumFS.FileFormat.Extensions>, and a <xref:FulcrumFS.FileFormat.PrimaryExtension>, which is useful when you want to normalize the stored extension to a canonical form regardless of how the uploader spelled it (for example, accepting both `.jpg` and `.jpeg` and storing as `.jpg`).

#### Text and catch-all factories

For content that is not a binary format, factory methods produce formats on demand: <xref:FulcrumFS.FileFormat.AnyContent*> accepts any bytes, <xref:FulcrumFS.FileFormat.TextAscii*> accepts ASCII text, <xref:FulcrumFS.FileFormat.TextUnicode*> accepts Unicode text, and <xref:FulcrumFS.FileFormat.TextEncoding*> accepts text in a specific <xref:System.Text.Encoding>. Each takes the extensions it should apply to. These are useful for validating uploads that should be plain text, for example a CSV import:

```csharp
var csvValidator = new FileFormatValidationProcessor(
    new FileFormatValidationOptions(FileFormat.TextAscii(".csv")));
```

## Standalone Validation

The validation API lives in `Singulink.FulcrumFS.Core` and does not require a repository, so a client or front-end project can validate before upload. Call <xref:FulcrumFS.FileFormat.ValidateAsync*> on a stream to get a <xref:FulcrumFS.FileFormatValidationResult>.

```csharp
// In a desktop client, check the file before uploading it.
await using var stream = File.OpenRead(path);
var result = await FileFormat.Png.ValidateAsync(stream);

if (!result.IsValid)
{
    MessageBox.Show("Please choose a valid PNG image.");
    return;
}
```

This makes it possible to share the same format definitions between client-side pre-checks (for a snappier user experience) and server-side enforcement (for the actual trust boundary).

> [!IMPORTANT]
> Client-side validation is a convenience, not a substitute for server-side validation. Always validate again on the server, since a malicious client can simply skip the check.

## Next Steps

- [Processing Pipelines](processing-pipelines.md) - Where validation fits in a pipeline.
- [Image Processing](image-processing.md) - Image-specific validation and transformation.
- [Exception Handling](exception-handling.md) - Handling validation failures.

</div>
