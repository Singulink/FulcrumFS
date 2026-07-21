<div class="article">

# Processing Pipelines

A processing pipeline decides what happens to a source as it is added: validate it, transform it, leave it untouched, or fan it out into variants. This guide covers building pipelines, routing by file type so a single upload endpoint can accept images and videos, and the property switches that control buffering and change detection.

### The building blocks

A <xref:FulcrumFS.FileProcessingPipeline> is an ordered list of <xref:FulcrumFS.FileProcessor> steps. Each processor receives a <xref:FulcrumFS.FileProcessingContext> and returns a <xref:FulcrumFS.FileProcessingResult>. A pipeline both *provides* itself (<xref:FulcrumFS.IFileProcessingPipelineProvider>) and *selects* itself (<xref:FulcrumFS.IFileProcessingPipelineSelector>), so a single pipeline can be passed straight to <xref:FulcrumFS.FileRepoTransaction.AddAsync*> without wrapping it.

## The Empty Pipeline

<xref:FulcrumFS.FileProcessingPipeline.Empty> stores the source as-is with no processing. Use it when you only need durable, transactional storage and the content has already been validated or comes from a trusted source.

```csharp
// Storing a generated PDF report - already known to be a valid PDF.
var added = await txn.AddAsync(reportStream, ".pdf", leaveOpen: true, FileProcessingPipeline.Empty);
```

> [!CAUTION]
> Do not use <xref:FulcrumFS.FileProcessingPipeline.Empty> for untrusted uploads. Anything that originates from a user (or an external API) should go through a pipeline with at least a <xref:FulcrumFS.FileFormatValidationProcessor> so the bytes are confirmed to match the declared extension. See [Validating File Formats](file-formats.md).

## Building a Pipeline

Construct a pipeline from one or more processors. Processors run in order, each receiving the previous step's output, which is what lets you compose two transformations into a single step. A common case is producing a compact JPEG thumbnail of a video: <xref:FulcrumFS.Videos.VideoFrameExtractionProcessor> extracts a full-resolution PNG poster frame, then <xref:FulcrumFS.Images.ImageProcessor> resizes that frame and converts it to JPEG.

```csharp
// Extract a poster frame, then resize/convert it to a 256x256 JPEG thumbnail.
var pipeline = new FileProcessingPipeline(
    new VideoFrameExtractionProcessor(VideoFrameExtractionProcessingOptions.Standard),
    new ImageProcessor(new ImageProcessingOptions {
        Formats = [new ImageFormatMapping(ImageFormat.Png, ImageFormat.Jpeg)],
        Resize = new ImageResizeOptions(ImageResizeMode.FitDown, 256, 256),
    }));
```

A single processor can also produce a pipeline directly with <xref:FulcrumFS.FileProcessor.ToPipeline*>, which is convenient when there is exactly one step:

```csharp
var pipeline = new ImageProcessor(ImageProcessingOptions.Preserve).ToPipeline();
```

## Routing by File Type

When different extensions need different handling, build a <xref:FulcrumFS.FileProcessingPipelineSelector> from several pipeline providers. It routes each source to the first provider whose leading processor declares the file's extension in <xref:FulcrumFS.FileProcessor.AllowedFileExtensions>. At most one provider may act as a catch-all default.

This is the natural shape for a media upload endpoint that accepts both images and videos:

```csharp
var selector = new FileProcessingPipelineSelector(
    new ImageProcessor(imageOptions),
    new VideoProcessor(VideoProcessingOptions.StandardizedH264AACMP4));

// The selector picks the image pipeline for .jpg/.png and the video pipeline for .mp4/.mov.
var added = await txn.AddAsync(source, leaveOpen: true, selector);
```

If no provider matches the extension and there is no default, <xref:FulcrumFS.FileProcessingPipelineSelector.GetPipeline*> throws <xref:FulcrumFS.FileProcessingException>, which the upload handler can translate into a "file type not supported" error. See [Exception Handling](exception-handling.md).

## Writing a Custom Processor

Derive from <xref:FulcrumFS.FileProcessor>, declare the extensions it accepts in <xref:FulcrumFS.FileProcessor.AllowedFileExtensions>, and implement the protected `ProcessAsync` method. Return a <xref:FulcrumFS.FileProcessingResult> describing the output:

- <xref:FulcrumFS.FileProcessingResult.File*> when the output is a file on disk. Use this when the processor invokes an external tool that writes to a temp file.
- <xref:FulcrumFS.FileProcessingResult.Stream*> when the output is a stream, optionally with a new extension. Use this when the processor produces its result in memory.

Each result carries a `hasChanges` flag. Reporting `false` means the step did not alter the source, which feeds the change-detection switches below (alias-when-unchanged for variants, throw-when-unchanged for the main file).

The <xref:FulcrumFS.FileProcessingContext> exposes <xref:FulcrumFS.FileProcessingContext.FileId>, <xref:FulcrumFS.FileProcessingContext.VariantId>, <xref:FulcrumFS.FileProcessingContext.Extension>, <xref:FulcrumFS.FileProcessingContext.IsLastProcessStep>, and a <xref:FulcrumFS.FileProcessingContext.CancellationToken>.

> [!TIP]
> Custom processors are usually unnecessary because the built-in image and video processors cover most needs. Reach for one when integrating an external tool (a PDF flattener, an OCR step that embeds extracted text into the PDF, a virus scanner that fails the add on detection, etc.) that has to participate in the same transaction as the storage step.

## Buffering and Change Detection

A few pipeline properties tune behavior:

#### Source buffering

<xref:FulcrumFS.FileProcessingPipeline.SourceBufferingMode> controls whether the source is buffered before processing. The default is automatic, which buffers when a processor needs random access or rewinds. Override only if you know the upload stream is already seekable and want to skip the extra copy, or you want to force buffering for a forward-only source that an early processor needs to re-read.

#### Aliasing unchanged variants

On a variant pipeline, set <xref:FulcrumFS.FileProcessingPipeline.AliasWhenVariantSourceUnchanged> to `true` so that when processing reports no changes, an alias to the source is stored instead of a duplicate copy. This is what makes a "256x256 thumbnail" pipeline storage-efficient when the source image is already 256x256 or smaller. This property applies only to variant pipelines. See [Variant Aliasing](../concepts/variant-aliasing.md).

#### Rejecting unchanged main sources

On a main-file pipeline, set <xref:FulcrumFS.FileProcessingPipeline.ThrowWhenMainSourceUnchanged> to `true` to throw <xref:FulcrumFS.FileSourceUnchangedException> when processing leaves the main source unchanged. This is useful when an unchanged result indicates a caller error, for example a re-encode endpoint where seeing the same bytes out as in means the upload was already in the target format and the call should be rejected as a no-op rather than silently succeeding.

## Next Steps

- [Validating File Formats](file-formats.md) - The format validation processor.
- [Image Processing](image-processing.md) - The image processor and its options.
- [Video Processing](video-processing.md) - The video and thumbnail processors.
- [File Variants](file-variants.md) - Attaching variant pipelines.

</div>
