<div class="article">

# Video Processing

The `Singulink.FulcrumFS.Videos` package adds video processing built on FFmpeg and FFprobe. It can transcode uploads to a standardized, web-friendly format and extract poster-frame thumbnails. This guide covers configuring the executables, the video processor, and the thumbnail processor, with a practical end-to-end example for a video upload endpoint.

### Where it fits

<xref:FulcrumFS.Videos.VideoProcessor> and <xref:FulcrumFS.Videos.VideoFrameExtractionProcessor> are both <xref:FulcrumFS.FileProcessor> types, so they compose into pipelines and variants. Add `using FulcrumFS.Videos;` alongside `using FulcrumFS;`.

> [!NOTE]
> Video transcoding is CPU-intensive, often taking many seconds (or minutes) per file. A synchronous upload handler that transcodes during the request will tie up a worker thread for the duration. For large videos, consider queueing the add to a background service so the upload endpoint returns quickly.

## Configuring FFmpeg

Video processing shells out to FFmpeg, so you must point the library at the directory containing the executables once at startup, before constructing any processor. Call <xref:FulcrumFS.Videos.VideoProcessor.ConfigureWithFFmpegExecutables*> with the directory and optional configuration options.

```csharp
using FulcrumFS;
using FulcrumFS.Videos;
using Singulink.IO;

// In Program.cs or your composition root.
var ffmpegDir = DirectoryPath.ParseAbsolute(@"C:\Tools\ffmpeg\bin");
VideoProcessor.ConfigureWithFFmpegExecutables(ffmpegDir, new() { MaxConcurrentProcesses = 2 });
```

The optional `MaxConcurrentProcesses` configuration option caps how many FFmpeg processes run at once, which protects a server from being overwhelmed when many users upload videos at the same time. A good starting value is roughly the number of physical CPU cores divided by two, since each FFmpeg process is itself multi-threaded.

There are also additional configuration options that can be used:
- `ProcessorAffinity` (only Windows and Linux) sets the CPU processor / hardware thread affinity mask to run all of the `ffmpeg`/`ffprobe` processes on to ensure they only can consume up to a certain limit of the computer's CPU resources - see `Process.ProcessorAffinity` for more information.
- `ThreadLimit` sets an approximate thread limit (per activity, not per process) for `ffmpeg`.
- And `ProcessPriorityClass` can be used to set the priority class for `ffmpeg`/`ffprobe` processes, to ensure that other tasks have higher priority for example (by setting the `ffmpeg`/`ffprobe` ones to `BelowNormal`) - see `Process.ProcessPriorityClass` for more information.

> [!IMPORTANT]
> Constructing a <xref:FulcrumFS.Videos.VideoProcessor> or <xref:FulcrumFS.Videos.VideoFrameExtractionProcessor> before configuring the executables throws. The constructor also verifies that the configured FFmpeg build supports the codecs, demuxers, and encoders the options require, and throws <xref:System.NotSupportedException> if something is missing, so a misconfigured deployment fails fast at startup rather than on the first upload.

## The Video Processor

Construct a <xref:FulcrumFS.Videos.VideoProcessor> with a <xref:FulcrumFS.Videos.VideoProcessingOptions>. The simplest path is a predefined configuration:

```csharp
var processor = new VideoProcessor(VideoProcessingOptions.StandardizedH264AACMP4);

// On upload, the source is re-encoded to a web-friendly MP4.
var added = await txn.AddAsync(source, ".mov", leaveOpen: true, processor.ToPipeline());
```

<xref:FulcrumFS.Videos.VideoProcessingOptions.StandardizedH264AACMP4> always re-encodes to H.264 video and AAC audio in an MP4 container with conservative, broadly compatible limits (60 fps cap, 8 bits per channel, 4:2:0 chroma subsampling, SDR, square pixels, progressive frames, stereo audio at up to 48 kHz). This is the format that plays in every browser without a polyfill, which is why it is the default recommendation for user-uploaded video. <xref:FulcrumFS.Videos.VideoProcessingOptions.StandardizedHEVCAACMP4> is the HEVC equivalent for environments where the smaller file size matters more than the slightly narrower playback compatibility. Both strip thumbnail metadata by default and do not preserve unrecognized streams.

The predefined options expose all their settings through `init` properties, so you can derive a customized variant with a `with` expression when you need to adjust limits such as resolution:

```csharp
// Same as StandardizedH264AACMP4, but capped at 1080p.
var options = VideoProcessingOptions.StandardizedH264AACMP4 with {
    ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1920, 1080),
};
```

## Hardware Acceleration

Transcoding in software is slow, and it is the single biggest cost of a video upload endpoint. If the machine doing the processing has a GPU (or a CPU with an integrated media engine), <xref:FulcrumFS.Videos.VideoProcessingOptions.HardwareAccelerationKind> can offload decoding and some filtering (such as scaling and deinterlacing) to it for a measurable speed improvement. Encoding is still always done in software.

Hardware acceleration is **disabled by default** (<xref:FulcrumFS.Videos.HardwareAccelerationKind.None>), because it introduces considerably more variation in the output. Opt in by setting the option:

```csharp
// Let the library pick whatever acceleration the machine offers.
var options = VideoProcessingOptions.StandardizedH264AACMP4 with {
    HardwareAccelerationKind = HardwareAccelerationKind.Auto,
};
```

### Choosing a mode

<xref:FulcrumFS.Videos.HardwareAccelerationKind.Auto> detects what the configured FFmpeg build and the current system support and picks a mode for you, preferring VideoToolbox, then CUDA, then AMF, then QSV, then D3D12. Note that this order of selection is an implementation detail, and could be changed at any time if issues arise.

The two vendor modes worth calling out are **AMF** (AMD) and **QSV** (Intel), because `Auto` deliberately deprioritizes them. Both are frequently reported by a CPU's *integrated* media engine rather than a discrete GPU, and picking an integrated engine over a discrete GPU that is also present would be the wrong trade. `Auto` therefore only falls back to them once the other modes that are more likely to map to a real GPU have been ruled out, and it prefers AMF over QSV, since AMD discrete GPUs are more common than Intel discrete GPUs while Intel CPUs are very common (meaning QSV would be matching a CPU in most scenarios).

So if the processing machine genuinely has an AMD or Intel GPU you want used, select it explicitly rather than relying on `Auto`:

```csharp
// Machine has an AMD GPU - use it rather than letting Auto deprioritize AMF.
var amdOptions = VideoProcessingOptions.StandardizedH264AACMP4 with {
    HardwareAccelerationKind = HardwareAccelerationKind.Amf,
};

// Machine has an Intel GPU (e.g. Arc) - use it rather than letting Auto deprioritize QSV.
var intelOptions = VideoProcessingOptions.StandardizedH264AACMP4 with {
    HardwareAccelerationKind = HardwareAccelerationKind.Qsv,
};
```

Note that if the requested acceleration mode is not available, it will fall back to `Auto`.

<xref:FulcrumFS.Videos.HardwareAccelerationKind.DecodeOnly> is a useful middle ground: decoding is accelerated, but all filtering stays in software, so most of the output quality characteristics of a pure software run are preserved while still cutting a meaningful (but smaller) amount of time off each transcode - however `DecodeOnly` can still introduce differences in some cases, so if you run into any issues, first compare the result with `None` to see if it's a hardware acceleration specific issue.

| Mode | Hardware | Platforms |
| --- | --- | --- |
| <xref:FulcrumFS.Videos.HardwareAccelerationKind.VideoToolbox> | Apple silicon / Intel Mac media engine | macOS |
| <xref:FulcrumFS.Videos.HardwareAccelerationKind.Cuda> | NVIDIA GPUs | Windows, Linux |
| <xref:FulcrumFS.Videos.HardwareAccelerationKind.Amf> | AMD GPUs (discrete or integrated) | Windows, Linux |
| <xref:FulcrumFS.Videos.HardwareAccelerationKind.Qsv> | Intel Quick Sync (usually an integrated GPU) | Windows, Linux |
| <xref:FulcrumFS.Videos.HardwareAccelerationKind.D3D12> | Any vendor, via Direct3D 12 | Windows |

### Requirements and fallback

The mode you request is a *preference*, not a guarantee. The FFmpeg build you configured must actually have been compiled with support for the mode, and the system must be able to initialize it. If a requested mode is unavailable, the library selects the best available alternative (or plain software processing), and if an accelerated run fails partway through, it is automatically retried with hardware filtering turned off, leaving only opportunistic hardware decoding. Certain inputs also disable acceleration on a per-file basis where the hardware path would not currently produce a correct result due to lacking explicit handling, or is unlikely to work anyway. In every case, the file still processes successfully - you never need to handle "hardware acceleration was not available" yourself.

> [!IMPORTANT]
> Processed output is never guaranteed to be identical across devices, even in pure software - different FFmpeg builds and CPUs can produce slightly different results. Hardware acceleration widens that gap considerably: hardware decoders and filters differ between vendors, driver versions and hardware generations, and the difference is sometimes visible (for example, noticeably softer scaling, different colours when scaling, slightly different video length when de-interlacing, etc.). Weigh that against the speed gain, and prefer leaving acceleration disabled if higher consistency between devices matters to you.

## Extracting a Thumbnail

<xref:FulcrumFS.Videos.VideoFrameExtractionProcessor> extracts a poster frame as a PNG, which is what a gallery view needs so the user can see what each video looks like without playing it. It performs no validation itself, so when validation matters, run a <xref:FulcrumFS.Videos.VideoProcessor> first and chain the thumbnail step after it. Configure it with <xref:FulcrumFS.Videos.VideoFrameExtractionProcessingOptions>, and use the predefined <xref:FulcrumFS.Videos.VideoFrameExtractionProcessingOptions.Standard> for typical needs.

A full-resolution PNG poster frame is usually larger than a gallery tile needs, so a real thumbnail variant chains the extractor with an <xref:FulcrumFS.Images.ImageProcessor> that resizes it down and converts to JPEG:

```csharp
// Resize the extracted poster frame down to a 256x256 JPEG.
var thumbnailPipeline = new FileProcessingPipeline(
    new VideoFrameExtractionProcessor(VideoFrameExtractionProcessingOptions.Standard),
    new ImageProcessor(new ImageProcessingOptions {
        Formats = [new ImageFormatMapping(ImageFormat.Png, ImageFormat.Jpeg)],
        Resize = new ImageResizeOptions(ImageResizeMode.FitDown, 256, 256),
    }));

// On upload: produce a standardized MP4 and the resized JPEG thumbnail variant.
var pipeline = new VideoProcessor(VideoProcessingOptions.StandardizedH264AACMP4)
    .WithVariant("thumbnail", thumbnailPipeline);

var added = await txn.AddAsync(source, ".mov", leaveOpen: true, pipeline);

// added.MainFile      -> standardized MP4
// added.VariantFiles  -> [ thumbnail (256x256 JPEG) ]
```

The gallery view can then serve the thumbnail variant for the preview tile and the main file for playback, with no extra processing at request time. See [File Variants](file-variants.md).

> [!TIP]
> Chaining processors like this is the idiomatic way to combine capabilities from different packages. The video package is responsible for getting a frame out; the image package is responsible for shaping it. Each does one thing well, and the pipeline composes them.

## Routing Mixed Uploads

When an endpoint accepts images and videos together (a typical "media" upload endpoint), route by extension with a <xref:FulcrumFS.FileProcessingPipelineSelector>:

```csharp
var selector = new FileProcessingPipelineSelector(
    new ImageProcessor(imageOptions),
    new VideoProcessor(videoOptions));

// The selector picks the image pipeline for images and the video pipeline for videos.
var added = await txn.AddAsync(source, leaveOpen: true, selector);
```

See [Processing Pipelines](processing-pipelines.md) for routing details.

## Next Steps

- [Image Processing](image-processing.md) - The image processor and its options.
- [File Variants](file-variants.md) - Producing thumbnails and alternate renditions.
- [Processing Pipelines](processing-pipelines.md) - Routing and composing processors.

</div>
