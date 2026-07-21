<div class="article">

# FulcrumFS

**FulcrumFS** is a high-performance file processing pipeline and storage engine that layers on top of any file system, transforming standard directories into transactional file repositories with database-aligned commit semantics and file variant management.

While it serves as a key component of the upcoming **FulcrumDB** database engine, it is also designed to function independently, bringing robust file handling to any application or database. FulcrumFS is especially well-suited for managing user-uploaded content such as documents, images and videos, offering strong guarantees around consistency and integrity.

When your application commits its database transaction before the repository transaction, FulcrumFS guarantees that:

- Every file the database references is accessible in the repository, even after a crash.
- Every file the database no longer references is eventually deleted, so storage never leaks.

See the [Transactional Commit Model](articles/concepts/commit-model.md) for how these guarantees are achieved without distributed transactions.

**FulcrumFS** is part of the **Singulink Libraries** collection. Visit https://github.com/Singulink/ to see our full list of publicly available libraries and other open-source projects.

### Installation

The libraries are available on NuGet. Install the packages that match your needs:

- `Singulink.FulcrumFS` - the main transactional storage and processing engine.
- `Singulink.FulcrumFS.Core` - the lightweight `FileId`, repository paths, and `FileFormat` validation API (usable standalone, including in client apps).
- `Singulink.FulcrumFS.Images` - image processing pipeline steps (powered by ImageSharp).
- `Singulink.FulcrumFS.Pdf` - PDF image extraction pipeline steps (powered by PDFium / PDFtoImage).
- `Singulink.FulcrumFS.Videos` - video processing pipeline steps (powered by FFmpeg / FFprobe).

**Supported Runtimes**: .NET 10.0+

## Information and Links

Here are some additional links to get you started:

- [Getting Started](articles/guides/getting-started.md) - Visit here first for a quick walkthrough and entry point to additional guides.
- [Guides](articles/guides/toc.yml) - In-depth articles on every aspect of the libraries.
- [Concepts](articles/concepts/toc.yml) - How the repository, pipelines, variants and crash recovery work under the hood.
- [API Documentation](api/index.md) - Browse the fully documented API here.
- [Chat on Discord](https://discord.gg/EkQhJFsBu6) - Have questions or want to discuss the library? This is the place for all Singulink project discussions.
- [Github Repo](https://github.com/Singulink/FulcrumFS) - File issues, contribute pull requests or check out the code for yourself!

</div>
