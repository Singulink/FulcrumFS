<div align="center">
<picture>
    <source media="(prefers-color-scheme: dark)" srcset="/Resources/Fulcrum%20Logo%20366x128%20Dark.png">
    <source media="(prefers-color-scheme: light)" srcset="/Resources/Fulcrum%20Logo%20366x128%20Light.png">
    <img src="/Resources/Fulcrum%20Logo%20400x150 LightBg.png" alt="Singulink Fulcrum Logo"/>
</picture>
</div>

# FulcrumFS

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)
[![Build and Test](https://github.com/Singulink/FulcrumFS/workflows/build%20and%20test/badge.svg)](https://github.com/Singulink/FulcrumFS/actions?query=workflow%3A%22build+and+test%22)

**FulcrumFS** is a high-performance file processing pipeline and storage engine that layers on top of any file system, transforming standard directories into transactional file repositories with database-aligned commit semantics and file variant management.

While it serves as a key component of our upcoming **FulcrumDB** database engine, it is also designed to function independently, bringing robust file handling to any application or database. FulcrumFS is especially well-suited for managing user-uploaded content such as documents, images and videos, offering strong guarantees around consistency and integrity.

When your application commits its database transaction before the repository transaction, FulcrumFS guarantees that every file the database references stays accessible in the repository (even after a crash), and every file the database no longer references is eventually deleted so storage never leaks.

Details of each component are provided below:

|| Library | Status | Package |
| --- | --- | --- | --- |
| <img src="/Resources/FulcrumFS%20Icon%20128x128.png" alt="FulcrumFS Icon" width="32" height="32"/> | **Singulink.FulcrumFS** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS/) |
| <img src="/Resources/FulcrumFS%20Icon%20128x128.png" alt="FulcrumFS Icon" width="32" height="32"/> | **Singulink.FulcrumFS.Core** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Core.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Core/) |
| <img src="/Resources/FulcrumFS%20Icon%20128x128.png" alt="FulcrumFS Icon" width="32" height="32"/> | **Singulink.FulcrumFS.Images** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Images.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Images/) |
| <img src="/Resources/FulcrumFS%20Icon%20128x128.png" alt="FulcrumFS Icon" width="32" height="32"/> | **Singulink.FulcrumFS.Pdf** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Pdf.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Pdf/) |
| <img src="/Resources/FulcrumFS%20Icon%20128x128.png" alt="FulcrumFS Icon" width="32" height="32"/> | **Singulink.FulcrumFS.Videos** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Videos.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Videos/) |

**Supported Runtimes**: .NET 10.0+

Libraries may be in the following states:
- Internal: Source code (and possibly a nuget package) is available but the library is intended for internal use at this time.
- Preview: Library is available for public preview but the APIs may not be fully documented and the API surface is subject to change without notice.
- Public: Library is intended for public use with a fully documented and stable API surface.

You are welcome to use any libraries or code in this repository that you find useful and feedback/contributions are appreciated regardless of library state.

API documentation and additional information is coming soon.

### About Singulink

We are a small team of engineers and designers dedicated to building beautiful, functional and well-engineered software solutions. We offer very competitive rates as well as fixed-price contracts and welcome inquiries to discuss any custom development / project support needs you may have.

This package is part of our **Singulink Libraries** collection. Visit https://github.com/Singulink to see our full list of publicly available libraries and other open-source projects.

## Components

### FulcrumFS

Main library that enables transactional file storage and processing, providing a foundation for building reliable file repositories on top of any file system.

**Features**:

✔️ **Commit-then-cleanup coordination** with transactional databases - orphaned files from failed commits are reclaimed automatically
✔️ **No distributed transactions required** - avoids 2PC coordinator overhead and indeterminate post-crash database states that block reopening
✔️ Validate, pre-process, and post-process files during storage and retrieval
✔️ Generate and manage file variants (e.g. alternate formats, resolutions, thumbnails)
✔️ Operates reliably on any file system, including local disks, NAS, and network file systems
✔️ Recovers gracefully from crashes, power failures and disconnected storage volumes
✔️ Scales to **millions of files** per repository while maintaining good file system performance characteristics
✔️ Provides **direct `FileStream` access** for efficient, low-overhead file I/O
✔️ Stored files remain browsable in standard file managers (e.g. File Explorer, Finder)
✔️ Fully compatible with file system features like encryption and compression
✔️ Designed to work seamlessly with existing backup, redundancy, replication, and storage tools

#### FulcrumFS.Core

A lightweight library that exposes the `FileId` type, repository paths, and the standalone **`FileFormat`** content validation API. It can be used independently of the main `FulcrumFS` library; for example, in a client application that needs to convert file IDs received from a service to a resulting path, or to pre-validate user-selected files in a front-end **before** uploading them to a service that hosts a FulcrumFS repository.

The `FileFormat` API ships with built-in singletons for common formats (`Jpeg`, `Png`, `Pdf`, `Mp4`, `Mkv`, `Docx`, `Zip`, and many more), factory methods for text formats (`TextAscii`, `TextUnicode`, `TextEncoding`) and content-agnostic types (`AnyContent`), and is extensible so you can derive your own `FileFormat` for custom formats. Validation returns a `FileFormatValidationResult` (valid or invalid with an error message) rather than throwing, so callers can handle outcomes however they prefer.

The main `FulcrumFS` library uses `FileFormat` via the `FileFormatValidationProcessor`, which integrates validation into a processing pipeline and converts invalid results to `FileProcessingException`s.

#### FulcrumFS.Images

Extension package that adds customizable image processing capabilities to file processing pipelines, including validation, thumbnail generation, resizing, format conversion and metadata stripping.

Image processing is provided by the fantastic [ImageSharp](https://github.com/SixLabors/ImageSharp) library.

#### FulcrumFS.Pdf

Extension package that adds PDF image extraction capabilities to file processing pipelines, rendering PDF pages to images (e.g. for thumbnail generation).

PDF rendering is provided by [PDFium](https://pdfium.googlesource.com/pdfium/) via the [PDFtoImage](https://github.com/sungaila/PDFtoImage) library.

#### FulcrumFS.Videos

Extension package that adds customizable video processing capabilities to file processing pipelines, including validation, thumbnail generation, resizing, format conversion, metadata stripping, bitrate limiting and audio stripping.

Video processing is provided by `FFmpeg` and `FFprobe`.

## Usage

The canonical pattern: open a database transaction and a file-repository transaction, process and add the file, save the resulting `FileId` on the database row that owns it, then commit the database **first** and the file repository **second**.

The pipeline below uses `FileProcessingPipelineSelector` to route by extension - PDFs are validated, images are re-encoded with a 256x256 JPEG thumbnail variant, and videos are normalized to H.264/AAC MP4 with a JPEG thumbnail variant. Any other extension is rejected up front by the selector.

```csharp
using FulcrumFS;
using Singulink.IO;

var baseDir = DirectoryPath.ParseAbsolute(@"C:\AppData\MyApp\Files");
using var repo = new FileRepo(baseDir);

// PDFs: validate the content matches the extension.
var pdfPipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Pdf));

// Shared 256x256 JPEG thumbnail processor (accepts JPEG/PNG sources, always outputs JPEG).
var thumbnailProcessor = new ImageProcessor(new ImageProcessingOptions {
    Formats = [
        new(ImageFormat.Jpeg),
        new(ImageFormat.Png, resultFormat: ImageFormat.Jpeg),
    ],
    Resize = new(ImageResizeMode.FitDown, 256, 256),
});

// Images: accept JPEG/PNG and produce the shared thumbnail variant.
var imagePipeline = new ImageProcessor(new ImageProcessingOptions {
        Formats = [new(ImageFormat.Jpeg), new(ImageFormat.Png)],
    })
    .WithVariant("thumbnail", thumbnailProcessor);

// Videos: normalize to H.264/AAC MP4 and produce a thumbnail variant by chaining
// the video frame extractor into the shared thumbnail processor.
var videoPipeline = new VideoProcessor(VideoProcessingOptions.StandardizedH264AACMP4)
    .WithVariant("thumbnail", new FileProcessingPipeline(
        new VideoFrameExtractionProcessor(VideoFrameExtractionProcessingOptions.Standard),
        thumbnailProcessor,
    ));

// Route by file type. Unsupported extensions are rejected with a FileProcessingException.
var pipeline = new FileProcessingPipelineSelector(pdfPipeline, imagePipeline, videoPipeline);

// Open transactions for both the database and the file repository.
await using var dbTxn = await myDb.BeginTransactionAsync();
await using var fileTxn = await repo.BeginTransactionAsync();

// Process and add the file to the repo.
using var stream = File.OpenRead(@"C:\incoming\upload.png");
var added = await fileTxn.AddAsync(stream, leaveOpen: false, pipeline);

// Save the FileId on the database row that owns the file.
await myDb.Documents.AddAsync(new Document {
    Name = "upload.png",
    FileId = added.FileId, // FileId type implicitly converts to Guid
});

// Commit the database FIRST, then the file repository.
await dbTxn.CommitAsync();
await fileTxn.CommitAsync();

// Read the main file (and enumerate any auto-generated variants) at any time using the stored FileId.
var info = await repo.GetAsync(added.FileId);
using var readStream = info.MainFile.Open();
// info.VariantFiles contains the "thumbnail" entry for images and videos.
```

The commit ordering is what guarantees consistency. If a crash occurs after `dbTxn.CommitAsync()` but before `fileTxn.CommitAsync()`, the added file is still on disk and reachable through the FileId stored in the database. If a crash occurs before `dbTxn.CommitAsync()`, the file is left in an indeterminate state on disk with no database reference to it, and the next clean operation will reclaim it.

## Repository Layout

Files are stored in standard directories under the repository base directory and remain fully browsable in any file manager:

```
{base}/
    fulcrumfs.info               Repository marker / version info
    fulcrumfs.lock               Held by the active FileRepo instance
    files/
        {yy}/{zz}/{file-id}/     Sharded by FileId bytes
            $main.pdf            Main file
            thumbnail.jpg        Optional variants
            scaled.jpg
    temp/                        In-progress work files
    cleanup/                     Pending delete / indeterminate markers
```

Each `FileId` maps deterministically to a sharded subdirectory under `files/`, keeping any one directory at a manageable file count even when the repository contains millions of entries. The main file is always stored as `$main` plus its original extension and variants live alongside it as named siblings (`thumbnail.jpg`, `scaled.mp4`, etc.).

Periodic `FileRepoCleaner.CleanAsync` calls (e.g. on a nightly schedule) reclaim files marked indeterminate by failed or interrupted commits, along with files marked for deferred deletion past their grace period.

## Why Not Just Store Files Directly?

A few common alternatives and how FulcrumFS compares:

**Storing blobs in the database.** Simple to reason about, but blob storage inflates database size, complicates backups, and limits I/O patterns to whatever the database driver supports. FulcrumFS keeps file bytes out of the database and gives callers a direct `FileStream` for reads and processing.

**Writing files to the file system directly and tracking them in the database.** Works until something fails midway. Files orphan on disk when an insert rolls back, or database rows reference files that were never flushed when a crash occurs between the two writes. FulcrumFS handles both cases: orphans from failed commits are cleaned up automatically, and a committed database row is guaranteed to find its file on disk.

**Distributed transactions (e.g. MSDTC, XA).** Solves the consistency problem but at significant cost: coordinator overhead on every commit, extra crash-recovery state on the database that must be resolved before the database can be reopened, and a hard dependency on every participating resource manager supporting the protocol. FulcrumFS reaches the same end-to-end guarantee using only a defined commit order and a periodic cleanup pass, with no coordinator and no crash-recovery state to resolve outside of normal database recovery.

## Further Reading

Please head over to the [project documentation site](https://www.singulink.com/Docs/FulcrumFS/index.html) to view articles, examples and the fully documented API.
