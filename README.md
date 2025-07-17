# Singulink FulcrumFS

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)

**FulcrumFS** is a high-performance file processing pipeline and storage engine that layers on top of any file system, transforming a standard directory into a transactional file repository with a two-phase commit protocol.

It serves as a foundational component of our upcoming **FulcrumDB** database engine but is also designed to function independently, bringing robust file handling to any application or database. FulcrumFS is especially well-suited for managing user-uploaded content such as documents, images and videos, offering strong guarantees around consistency and integrity.

Details of each component are provided below:

| Library | Status | Package |
| --- | --- | --- |
| **FulcrumFS** | Internal | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS/) |
| **FulcrumFS.Images** | Internal | [![View nuget package](https://img.shields.io/nuget/v/Singulink.FulcrumFS.Images.svg)](https://www.nuget.org/packages/Singulink.FulcrumFS.Images/) |

**Supported Platforms**: .NET 8.0+

Libraries may be in the following states:
- Internal: Source code (and possibly a nuget package) is available to the public but the library is intended to be used internally until further development.
- Preview: The library is available for public preview but the APIs may not be fully documented and the API surface is subject to change without notice.
- Public: The library is intended for public use with a fully documented and stable API surface.

You are free to use any libraries or code in this repository that you find useful and feedback/contributions are welcome regardless of library state.

API documentation and additional information is coming soon.

### About Singulink

We are a small team of engineers and designers dedicated to building beautiful, functional and well-engineered software solutions. We offer very competitive rates as well as fixed-price contracts and welcome inquiries to discuss any custom development / project support needs you may have.

This package is part of our **Singulink Libraries** collection. Visit https://github.com/Singulink to see our full list of publicly available libraries and other open-source projects.

## Components

### FulcrumFS

The core library that enables transactional file storage and processing, providing a foundation for building reliable file repositories on top of any file system.

**Features**:

✔️ **Two-phase commit** ensures consistency with transactional databases while keeping files decoupled from database storage  
✔️ Validate, pre-process, and post-process files during storage and retrieval  
✔️ Generate and manage file variants (e.g., alternate formats, resolutions, thumbnails)  
✔️ Operates on any file system, including local disks, NAS, and network shares  
✔️ Scales to **millions of files** per repository without degrading file system performance  
✔️ Provides **direct `FileStream` access** for efficient, low-overhead file I/O  
✔️ Stored files remain browsable in standard file managers (e.g., File Explorer)  
✔️ Fully compatible with file system features like encryption and compression  
✔️ Works seamlessly with existing backup, redundancy, replication, and storage tools  

### FulcrumFS.Images

An optional extension that adds customizable image processing capabilities, including validation, thumbnail generation, resizing, format conversion and metadata stripping, for both original images and their derived variants.

Image processing is provided by the excellent [`ImageSharp`](https://github.com/SixLabors/ImageSharp) library.

## Further Reading

API documentation and additional usage information is coming soon.
