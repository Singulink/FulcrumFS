<div class="article">

# Getting Started

FulcrumFS turns an ordinary directory into a transactional file repository with database-aligned commit semantics, content validation, and file variant management. This guide walks through installing the packages, creating a repository, storing a user-uploaded photo inside a transaction, and reading it back to serve over HTTP.

### Packages

Install the packages that match what you need:

- `Singulink.FulcrumFS.Local` is the local file system repository implementation, <xref:FulcrumFS.FileRepo>. This is what a server-side app will install, and it brings in `Singulink.FulcrumFS` and `Singulink.FulcrumFS.Core` with it.
- `Singulink.FulcrumFS` contains the interfaces, processing pipeline and shared types, such as <xref:FulcrumFS.IFileRepo> and <xref:FulcrumFS.FileProcessingPipeline>. Install it directly in a project that only uses a repository through <xref:FulcrumFS.IFileRepo> or that defines processors, without creating a local repository.
- `Singulink.FulcrumFS.Core` provides the lightweight <xref:FulcrumFS.FileId> type, repository path helpers, and the standalone <xref:FulcrumFS.FileFormat> validation API. The main packages reference it, so you only install it directly when you want validation or path mapping in a project that does not host a repository (for example, a desktop client that pre-validates uploads before sending them to the server).
- `Singulink.FulcrumFS.Images` adds image processing pipeline steps (powered by ImageSharp). Install this to resize images, generate thumbnails, strip EXIF data, etc.
- `Singulink.FulcrumFS.Pdf` adds PDF image extraction pipeline steps (powered by PDFium via PDFtoImage). Install this to render PDF pages to images, e.g. for thumbnails.
- `Singulink.FulcrumFS.Videos` adds video processing pipeline steps (powered by FFmpeg and FFprobe). Install this to transcode uploads to web-friendly formats and extract poster frames.
- `Singulink.FulcrumFS.Documents` adds document to PDF conversion pipeline steps (powered by LibreOffice). Install this to generate PDF previews of word processing documents, spreadsheets and presentations.

**Supported Runtimes**: .NET 10.0+

### Prerequisites

FulcrumFS layers on top of a normal file system, so the only hard requirement is a directory the process can read and write. The library uses <xref:Singulink.IO.IAbsoluteDirectoryPath> and <xref:Singulink.IO.IAbsoluteFilePath> from Singulink.IO.FileSystem for all path handling, so a `using Singulink.IO;` is usually present alongside `using FulcrumFS;`.

> [!TIP]
> Pick a base directory that lives on the same volume as your application's data and is excluded from any file indexer or antivirus on-access scanner. The repository writes lots of small marker files during normal operation, and a busy scanner can noticeably slow things down.

## Creating a Repository

A repository is represented by the <xref:FulcrumFS.FileRepo> class. Construct one with a base directory, the minimum repository format version and an optional configuration callback, then call <xref:FulcrumFS.FileRepo.EnsureCreated*> once. That one call creates the repository if it does not exist yet (or repairs a partially created one) and opens it: the instance takes the repository lock and upgrades an older repository to the minimum format version.

```csharp
using FulcrumFS;
using Singulink.IO;

var baseDir = DirectoryPath.ParseAbsolute(@"C:\AppData\PhotoApp\Files");

var repo = new FileRepo(baseDir, RepoFormatVersion.Version2, options => {
    options.DeleteMode = DeleteMode.DeferredUntilClean;
});

repo.EnsureCreated(); // Creates the repository if needed and opens it.
```

A <xref:FulcrumFS.FileRepo> is intended to live for the lifetime of the application, so register it as a singleton in your DI container rather than constructing one per request. The instance is thread-safe and supports many concurrent transactions. Code that only uses the repository can depend on the <xref:FulcrumFS.IFileRepo> interface that it implements instead; see [Coding Against the Repository Interface](../concepts/repository-abstraction.md).

Configuration is exposed through <xref:FulcrumFS.FileRepoOptions>. The most commonly set option is <xref:FulcrumFS.FileRepoOptions.DeleteMode>, which controls whether deletions take effect immediately or are deferred until a cleanup pass runs (see the [Deleting Files and Variants](deleting-files.md) guide).

#### Choosing the format version

<xref:FulcrumFS.FileRepoOptions.MinimumFormatVersion> has no default, because the right version depends on which processes use the repository. It is the lowest version the repository has once it is open: a new repository is created at the version you choose, an existing one with an older version is upgraded to it when <xref:FulcrumFS.FileRepo.EnsureCreated*> opens it, and one with a newer version keeps it. Choose it as follows:

| Version | Use it when |
|---|---|
| <xref:FulcrumFS.RepoFormatVersion.Version1> | Any process that uses or cleans the repository, including a separately deployed cleanup job, still runs FulcrumFS 1.0.x. |
| <xref:FulcrumFS.RepoFormatVersion.Version2> | Every such process runs FulcrumFS 2.0 or later. This is the right choice for a new application. |
| <xref:FulcrumFS.RepoFormatVersion.Latest> | The application owns the repository exclusively and should always use the newest format of the library. |

The API works the same with every version. See [Format Version](../concepts/repository-layout.md#format-version) for what each version contains and how to upgrade repositories that several processes share.

#### Reading from another process

A second process can read the repository while the application uses it, for example a static file host serving the stored files, by opening it with <xref:FulcrumFS.FileRepoOptions.ReadOnly> set. See [Read-Only Repositories](read-only-repositories.md).

> [!IMPORTANT]
> Call <xref:FulcrumFS.FileRepo.EnsureCreated*> once at application startup, before any other repository operation. Because it also opens the repository, a problem such as a format this library cannot use surfaces at startup rather than on the first request. It does not wait for the repository lock: if another process still holds the repository, for example the previous instance of the application while it shuts down, it throws an <xref:System.IO.IOException>.

## Adding and Committing a File

Adds happen inside a transaction. The pattern is: begin a repository transaction, add the source through a processing pipeline, persist the returned <xref:FulcrumFS.FileId> on whatever database row owns the file, commit the database, then commit the repository.

```csharp
// Handler for a "POST /api/photos" upload endpoint.

await using var source = uploadedFile.OpenReadStream();
await using var txn = await repo.BeginTransactionAsync();

var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty);

// Persist added.FileId on the owning database row.
photo.FileId = added.FileId;
await dbContext.SaveChangesAsync();

await txn.CommitAsync();
```

<xref:FulcrumFS.FileProcessingPipeline.Empty> stores the file as-is with no processing. To validate, transform, or generate variants (for example, to confirm an upload really is a JPEG and produce a 256x256 thumbnail), pass a configured pipeline instead. See [Processing Pipelines](processing-pipelines.md) and [Image Processing](image-processing.md).

#### The commit ordering rule

When a file belongs to a database row, commit the database transaction **first** and the repository transaction **second**. If the process crashes between the two commits, the database row references a repository file whose commit had not finished; that file is still accessible and is resolved safely on a later cleanup pass, so the reference is never left dangling. Committing the repository first risks the opposite: a committed file that the database never recorded, with no clean way to tell it apart from a valid one.

> [!IMPORTANT]
> Always commit the database before the repository. This single rule is what lets FulcrumFS guarantee that every file the database references stays accessible, and every file the database no longer references is eventually deleted by cleanup, all without any distributed transaction coordinator. See [Transactional Commit Model](../concepts/commit-model.md) for the full rationale.

## Reading a File Back

Fetch a file by its <xref:FulcrumFS.FileId>. <xref:FulcrumFS.FileRepo.GetAsync*> returns a <xref:FulcrumFS.RepoFileInfo>; call <xref:FulcrumFS.RepoFileInfo.Open*> to get a read-only <xref:System.IO.FileStream> opened with the repository's recommended sharing options. Its <xref:FulcrumFS.FileAccessPattern> argument says whether you are going to read the file from start to end or seek within it (see [Fetching Files](fetching-files.md#choosing-the-access-pattern)).

```csharp
// Handler for a "GET /api/photos/{id}" download endpoint.

var photo = await dbContext.Photos.FindAsync(id);
var info = await repo.GetAsync(photo.FileId);

return Results.File(info.Open(FileAccessPattern.Sequential), contentType: "image/jpeg", fileDownloadName: photo.Name);
```

For a one-line open when you do not need the metadata, <xref:FulcrumFS.FileRepo.OpenAsync*> returns the stream directly.

> [!WARNING]
> Always read through <xref:FulcrumFS.RepoFileInfo.Open*> or <xref:FulcrumFS.FileRepo.OpenAsync*> rather than opening <xref:FulcrumFS.RepoFileInfo.Path> yourself. The repository relies on specific file sharing semantics to coordinate cleanup with concurrent readers; an ad-hoc open can interfere with deletion under the deferred delete model and is not guaranteed to remain accessible.

## Next Steps

Now that you can create a repository and round-trip a file, explore the rest of the library:

- [Adding and Committing Files](adding-files.md) - Transaction lifecycle, rollback, and the indeterminate state.
- [Processing Pipelines](processing-pipelines.md) - Validate and transform files as they are stored.
- [File Variants](file-variants.md) - Generate thumbnails and alternate formats alongside the main file.
- [Transactional Commit Model](../concepts/commit-model.md) - Why commit ordering matters and how crash recovery works.
- [Coding Against the Repository Interface](../concepts/repository-abstraction.md) - Depend on the repository interface so that another implementation can replace the local one.
- [Read-Only Repositories](read-only-repositories.md) - Read a repository from another process while the application uses it.
- [API Documentation](../../api/index.md) - Browse the full API surface.

</div>
