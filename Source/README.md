# FulcrumFS

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)

**FulcrumFS** is a high-performance file processing pipeline and storage engine that layers on top of any file system, transforming standard directories into transactional file repositories with a two-phase commit system and file variant management.

While it serves as a key component of our upcoming **FulcrumDB** database engine, it is also designed to function independently, bringing robust file handling to any application or database. FulcrumFS is especially well-suited for managing user-uploaded content such as documents, images and videos, offering strong guarantees around consistency and integrity.

Details of each component are provided below:

| Library | Status | Package |
| --- | --- | --- |
| **Singulink.FulcrumFS** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS/) |
| **Singulink.FulcrumFS.Core** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Core.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Core/) |
| **Singulink.FulcrumFS.Images** | Preview | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Images.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Images/) |

**Supported Runtimes**: .NET 9.0+

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

✔️ **Two-phase commit** enables consistency with transactional databases while keeping files decoupled from database storage  
✔️ Validate, pre-process, and post-process files during storage and retrieval  
✔️ Generate and manage file variants (e.g. alternate formats, resolutions, thumbnails)  
✔️ Operates reliably on any file system, including local disks, NAS, and network file systems  
✔️ Recovers gracefully from crashes, power failures and disconnected storage volumes  
✔️ Scales to **millions of files** per repository while maintaining good file system performance characteristics  
✔️ Provides **direct `FileStream` access** for efficient, low-overhead file I/O  
✔️ Stored files remain browsable in standard file managers (e.g. File Explorer, Finder)  
✔️ Fully compatible with file system features like encryption and compression  
✔️ Designed to work seamlessly with existing backup, redundancy, replication, and storage tools  

### FulcrumFS.Core

Core library that is mostly useful for access to the `FileId` type and repository paths from projects that do not need the full file processing capabilities of the main `FulcrumFS` library (e.g. in a client application that needs to convert file IDs received from a service to resulting path).

### FulcrumFS.Images

Optional extension that adds customizable image processing capabilities, including validation, thumbnail generation, resizing, format conversion and metadata stripping.

Image processing is provided by the fantastic [`ImageSharp`](https://github.com/SixLabors/ImageSharp) library.

## Further Reading

API documentation and additional usage information is coming soon.
