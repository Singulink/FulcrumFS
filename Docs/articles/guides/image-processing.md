<div class="article">

# Image Processing

The `Singulink.FulcrumFS.Images` package adds an image processor built on ImageSharp that validates, reformats, resizes, reorients, and strips metadata from images as they are stored. This guide covers the processor and its main options, with practical examples for the most common scenarios: storing user-uploaded photos, generating gallery thumbnails, and normalizing avatars.

### Where it fits

<xref:FulcrumFS.Images.ImageProcessor> is a <xref:FulcrumFS.FileProcessor>, so it slots into a pipeline like any other step and can drive variants such as thumbnails. Add `using FulcrumFS.Images;` alongside `using FulcrumFS;`.

> [!NOTE]
> The image processor is a superset of <xref:FulcrumFS.FileFormatValidationProcessor> for image formats: it validates the source by fully decoding it (rejecting malformed or hostile inputs in the process) before applying any transformation. There is no need (and no benefit) to chaining a <xref:FulcrumFS.FileFormatValidationProcessor> in front of it for image content. See [Validating File Formats](file-formats.md) for when the lightweight validator is the right choice.

## The Image Processor

Construct an <xref:FulcrumFS.Images.ImageProcessor> with an <xref:FulcrumFS.Images.ImageProcessingOptions> instance, then add it to a pipeline. The processor's <xref:FulcrumFS.Images.ImageProcessor.AllowedFileExtensions> is derived from the source formats in the options, so a <xref:FulcrumFS.FileProcessingPipelineSelector> routes only matching images to it.

```csharp
using FulcrumFS;
using FulcrumFS.Images;

// Accept images and store them in their original format with no transformation.
var processor = new ImageProcessor(ImageProcessingOptions.Preserve);

var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, processor.ToPipeline());
```

<xref:FulcrumFS.Images.ImageProcessingOptions.Preserve> maps every supported format to itself with all other options at their defaults, which is the simplest way to accept images and keep their original format. It is a good fit for a "store the original" main pipeline that pairs with a separate thumbnail variant.

## Format Mapping

The required <xref:FulcrumFS.Images.ImageProcessingOptions.Formats> collection lists which source formats are accepted and what each is converted to. Each entry is an <xref:FulcrumFS.Images.ImageFormatMapping> pairing a source <xref:FulcrumFS.Images.ImageFormat> with an optional result format; when the result is omitted, the source format is kept.

A common pattern is to normalize to JPEG so the server only has to serve a single format:

```csharp
// Accept PNG and JPEG uploads, store everything as JPEG.
var options = new ImageProcessingOptions {
    Formats = [
        new ImageFormatMapping(ImageFormat.Png, ImageFormat.Jpeg),
        new ImageFormatMapping(ImageFormat.Jpeg),
    ],
};
```

The built-in formats are <xref:FulcrumFS.Images.ImageFormat.Jpeg>, <xref:FulcrumFS.Images.ImageFormat.Png>, and <xref:FulcrumFS.Images.ImageFormat.Bmp>. Only source formats present in the collection are supported, and duplicate source formats are rejected.

> [!NOTE]
> Converting PNG (which supports transparency) to JPEG (which does not) flattens transparent pixels against a background color. Set <xref:FulcrumFS.Images.ImageProcessingOptions.BackgroundColor> when the default does not suit your design, for example to match the page background of a dark theme.

## Resizing

Set <xref:FulcrumFS.Images.ImageProcessingOptions.Resize> to an <xref:FulcrumFS.Images.ImageResizeOptions> to scale images. The constructor takes a mode, a target width and height, and an optional flag to match the source orientation.

A typical thumbnail configuration looks like this:

```csharp
// 256x256 thumbnails, scaled down to fit (preserving aspect ratio, no upscale).
var thumbnailOptions = new ImageProcessingOptions {
    Formats = [new ImageFormatMapping(ImageFormat.Jpeg)],
    Resize = new ImageResizeOptions(ImageResizeMode.FitDown, 256, 256),
};
```

The mode is an <xref:FulcrumFS.Images.ImageResizeMode>:

- <xref:FulcrumFS.Images.ImageResizeMode.FitDown> contains the image within the target box, preserving aspect ratio. Best for gallery thumbnails where you do not want to crop.
- <xref:FulcrumFS.Images.ImageResizeMode.CropDown> covers the box by cropping. Best for fixed-size avatars or tile thumbnails where the slot must be filled.
- <xref:FulcrumFS.Images.ImageResizeMode.PadDown> letterboxes using the configured pad color. Best when the full image must be visible and the slot has a fixed size.

> [!NOTE]
> None of the modes upscale. An image smaller than the target box is left at its original size, so the output never exceeds the target dimensions but also never invents pixels.

## Reorientation and Metadata

A couple of options control orientation and metadata:

#### EXIF reorientation

<xref:FulcrumFS.Images.ImageProcessingOptions.ReorientMode> (an <xref:FulcrumFS.Images.ImageReorientMode>) controls whether the image is rotated to its normal orientation based on EXIF data. The default leaves orientation untouched. Reorient when you want the stored bytes to render correctly in any viewer regardless of whether it honors EXIF orientation, which is the safer default for web delivery.

#### Metadata stripping

<xref:FulcrumFS.Images.ImageProcessingOptions.MetadataStrippingMode> (an <xref:FulcrumFS.Images.ImageMetadataStrippingMode>) controls how much metadata is removed. The default strips only the embedded thumbnail.

> [!CAUTION]
> EXIF data in user-uploaded photos often contains GPS coordinates, camera serial numbers, and software fingerprints. If you serve images to other users, consider stripping all metadata by setting a more aggressive <xref:FulcrumFS.Images.ImageMetadataStrippingMode> so you do not leak location and device information.

## Source Validation

Set <xref:FulcrumFS.Images.ImageProcessingOptions.SourceValidation> to an <xref:FulcrumFS.Images.ImageSourceValidationOptions> to reject images that exceed limits (such as maximum width and height) before processing. This rejects abusive inputs early, before any decode or resize work.

> [!IMPORTANT]
> Always configure <xref:FulcrumFS.Images.ImageProcessingOptions.SourceValidation> for endpoints that accept arbitrary uploads. A "decompression bomb" image (a tiny file that decodes to billions of pixels) can exhaust memory in the decoder long before any resize step gets a chance to bound it.

## Generating a Thumbnail Variant

Combine a main processor with a resized variant to produce a thumbnail alongside the original at add time. This is the typical photo-upload pipeline for a gallery:

```csharp
var pipeline = new ImageProcessor(ImageProcessingOptions.Preserve)
    .WithVariant(
        "thumbnail",
        new ImageProcessor(thumbnailOptions).ToPipeline(aliasWhenVariantSourceUnchanged: true));

// Upload handler.
var added = await txn.AddAsync(uploadStream, ".jpg", leaveOpen: true, pipeline);

// added.MainFile      -> full-resolution original
// added.VariantFiles  -> [ thumbnail @ 256x256 ]
```

Passing `aliasWhenVariantSourceUnchanged: true` when materializing the variant pipeline means an already-small upload (256x256 or less, in this case) does not store a duplicate copy of the same bytes under the thumbnail ID; the variant is recorded as a zero-byte alias that resolves transparently to the original. See [File Variants](file-variants.md) for the variant model and [Variant Aliasing](../concepts/variant-aliasing.md) for the alias mechanism.

> [!TIP]
> When you do not need to set pipeline-level switches like `aliasWhenVariantSourceUnchanged`, <xref:FulcrumFS.FileProcessingPipeline.WithVariant*> also accepts a bare <xref:FulcrumFS.FileProcessor> as a shorthand for a single-step variant pipeline.

## Next Steps

- [Video Processing](video-processing.md) - The video and thumbnail processors.
- [File Variants](file-variants.md) - Attaching variant pipelines.
- [Processing Pipelines](processing-pipelines.md) - Routing images with a selector.

</div>
