<div class="article">

# Read-Only Repositories

A <xref:FulcrumFS.FileRepo> opened read-only reads files from a repository without ever writing to it, while another process uses the same repository as its writer. This guide covers when to use read-only mode, what it allows, and what a reader has to expect from files that the writer deletes.

### When to use read-only mode

Only one <xref:FulcrumFS.FileRepo> with full access can use a repository at a time: it holds the repository lock for as long as it is in use. Read-only mode takes no lock, so it suits three situations:

- **Reading a live repository from another process.** A static file host or download service can serve the files that the main application stores, without going through the application and without stopping it.
- **Reading a repository without changing it.** A read-only instance never creates or upgrades a repository, whatever <xref:FulcrumFS.FileRepoOptions.MinimumFormatVersion> is configured, so a newer library can read repositories that older processes still write to, without moving them to a format those processes cannot open.
- **Reading a repository whose format is newer than the library supports for full access.** A library refuses full access to a repository with a newer [format version](../concepts/repository-layout.md#format-version), but a format change that only adds new kinds of entries keeps reading compatible. A read-only instance opens any repository whose `read-compat-version` the library supports.

## Opening a Repository Read-Only

Set <xref:FulcrumFS.FileRepoOptions.ReadOnly> when constructing the repository:

```csharp
var baseDir = DirectoryPath.ParseAbsolute(@"C:\AppData\PhotoApp\Files");

var repo = new FileRepo(baseDir, RepoFormatVersion.Version2, options => {
    options.ReadOnly = true;
});

repo.EnsureCreated();
```

The minimum format version is required as for any repository, but it has no effect here: a read-only instance never creates or upgrades a repository. <xref:FulcrumFS.FileRepo.EnsureCreated*> behaves exactly as <xref:FulcrumFS.FileRepo.EnsureInitialized*> does and only verifies and opens the existing repository. Both throw a <xref:System.IO.DirectoryNotFoundException> if there is no repository at the base directory or the repository is incomplete, since only an instance with full access can create or repair it.

Opening the repository reads its info file and checks that the directories exist, once. A read-only instance takes no lock, does not touch the `temp` directory that the writer works in, and runs no health checks, because those write to the lock file. If the storage goes away while the instance is open, fetches fail or report files as not found until it is back.

<xref:FulcrumFS.FileRepo.FormatVersion?displayProperty=nameWithType> reports the format version the repository had when it was opened. It is not updated if the writer later upgrades the repository.

## What Works and What Throws

#### Reading

Everything that only reads works as it does with full access:

- <xref:FulcrumFS.FileRepo.GetAsync*>, <xref:FulcrumFS.FileRepo.GetGroupAsync*> and <xref:FulcrumFS.FileRepo.GetVariantAsync*>.
- <xref:FulcrumFS.FileRepo.OpenAsync*> and <xref:FulcrumFS.FileRepo.OpenVariantAsync*>.
- <xref:FulcrumFS.FileRepo.EnumerateFilesAsync*>.
- <xref:FulcrumFS.FileRepo.GetOrAddVariantAsync*> when every variant it asks for already exists, which it returns.
- The <xref:FulcrumFS.FileRepo.CorruptionDetected> event, which fetches raise as usual. Fetches never repair anything on disk, so nothing is skipped in read-only mode.

#### Writing

Every member that would write throws an <xref:System.InvalidOperationException>:

- <xref:FulcrumFS.FileRepo.BeginTransactionAsync*> and <xref:FulcrumFS.FileRepo.BeginStagedTransactionAsync*>.
- <xref:FulcrumFS.FileRepo.AddVariantAsync*> and <xref:FulcrumFS.FileRepo.TryAddVariantAsync*>, and <xref:FulcrumFS.FileRepo.GetOrAddVariantAsync*> when a variant would have to be added.
- <xref:FulcrumFS.FileRepo.DeleteVariantsAsync*>.
- <xref:FulcrumFS.FileRepo.CleanAsync*>. Clean the repository from the writer or with a separate <xref:FulcrumFS.FileRepoCleaner> (see [Repository Cleanup](cleanup.md)).

Code that works with the <xref:FulcrumFS.IFileRepo> interface can check <xref:FulcrumFS.IFileRepo.IsReadOnly?displayProperty=nameWithType> before it tries to write.

## Files That Vanish Under a Reader

The writer keeps adding and deleting files while a reader is open, and a reader sees each change as soon as it is on disk. A deleted main file stays readable until it is physically removed: in the deferred delete modes that happens when a cleanup pass runs after the delete delay, and in <xref:FulcrumFS.DeleteMode.Immediate> mode it happens when the deletion commits. A retired variant can no longer be fetched as soon as it is retired.

Once a file has been removed, fetching it throws a <xref:FulcrumFS.RepoFileNotFoundException>, and opening a <xref:FulcrumFS.RepoFileInfo> that was fetched before the removal, or reading its <xref:FulcrumFS.RepoFileInfo.Length>, throws an <xref:System.IO.IOException>. A stream that was already open is not affected: repository streams allow the file to be deleted while they are open, so the reader keeps reading the file to the end.

> [!TIP]
> A file host that runs as a reader should treat a <xref:FulcrumFS.RepoFileNotFoundException> as a normal "not found" response at any time, including for a file it found moments earlier.

## Next Steps

These articles cover the rest of what a reader interacts with:

- [Fetching Files](fetching-files.md) - Fetching files and variants and opening their content.
- [Repository Layout](../concepts/repository-layout.md) - The format version and what each version contains.
- [Deleting Files and Variants](deleting-files.md) - When deleted files are physically removed.

</div>
