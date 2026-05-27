# FulcrumFS

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)

**FulcrumFS** is a high-performance file processing pipeline and storage engine that layers on top of any file system, transforming standard directories into transactional file repositories with database-aligned commit semantics and file variant management.

While it serves as a key component of our upcoming **FulcrumDB** database engine, it is also designed to function independently, bringing robust file handling to any application or database. FulcrumFS is especially well-suited for managing user-uploaded content such as documents, images and videos, offering strong guarantees around consistency and integrity.

Details of each component are provided below:

| Library | Status | Package |
| --- | --- | --- |
| **Singulink.FulcrumFS** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS/) |
| **Singulink.FulcrumFS.Core** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Core.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Core/) |
| **Singulink.FulcrumFS.Images** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Images.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Images/) |
| **Singulink.FulcrumFS.Videos** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Videos.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Videos/) |

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

Optional extension that adds customizable image processing capabilities, including validation, thumbnail generation, resizing, format conversion and metadata stripping.

Image processing is provided by [`ImageSharp`](https://github.com/SixLabors/ImageSharp).

#### FulcrumFS.Videos

Optional extension that adds customizable video processing capabilities, including validation, thumbnail generation, resizing, format conversion, metadata stripping, bitrate limiting, and audio stripping, for both original videos and their derived variants.

Video processing is provided by `FFmpeg` and `FFprobe`.

## Further Reading

API documentation and additional usage information is coming soon.
