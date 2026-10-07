<div class="article">

# Coding Against the Repository Interface

<xref:FulcrumFS.FileRepo> stores a repository in a local directory and implements <xref:FulcrumFS.IFileRepo>, an interface that covers everything an application does with a repository once it exists. This article explains why application code should depend on the interface, what the interface covers and what stays on the local class, and what any other implementation must guarantee.

### Why depend on an interface

A local directory is one way to store a repository. Others are possible without changing the commit model: a repository server that applications reach over the network, for example, or a replicated pair of repositories in which a secondary takes over when the primary fails. Code that takes an <xref:FulcrumFS.IFileRepo> keeps working when such an implementation replaces <xref:FulcrumFS.FileRepo>; only the place where the repository is created changes.

The interface also helps with testing. A service that depends on <xref:FulcrumFS.IFileRepo> can be given a fake, or an implementation that wraps the real one to record which files a request added or to inject failures.

### Package layout

The interfaces are in the `Singulink.FulcrumFS` package, together with the processing pipeline and the types that every implementation shares, such as <xref:FulcrumFS.RepoFileEntry>, <xref:FulcrumFS.TaskWithProgress`1>, the exceptions and the event payloads. Each implementation is a package of its own that references it: <xref:FulcrumFS.FileRepo> and the other local types are in `Singulink.FulcrumFS.Local`. Of the FulcrumFS packages, an implementation references only `Singulink.FulcrumFS`, and it uses only the public members of that package; the local implementation is built the same way as any other. Only the project that creates the repository references an implementation package. A project that uses a repository only through the interfaces, such as a library of services or the client of a repository server, references `Singulink.FulcrumFS` alone and does not carry the local implementation with it. Every package uses the `FulcrumFS` namespace, so the same `using` directive works whichever package a type comes from.

A few public members of `Singulink.FulcrumFS` exist only for implementations and are hidden from IntelliSense and from the API reference; their XML documentation describes them for implementers. They are the constructors of <xref:FulcrumFS.FileProcessingContext> and its `ReleaseSourceAsync` method, `ExecuteAsync` and `AllVariants` on <xref:FulcrumFS.FileProcessingPipeline>, the constructor of <xref:FulcrumFS.TaskWithProgress`1> with its `TaskFactory` delegate and its `Upcast` method, the constructors of <xref:FulcrumFS.RepoFileEntry>, <xref:FulcrumFS.DanglingAliasInfo>, <xref:FulcrumFS.RepoCorruptionInfo> and <xref:FulcrumFS.RepoTransactionCompletionFailureInfo>, and the file helpers in `FulcrumFS.Utilities.IOExtensions`. Application code does not need them.

## What the Interfaces Cover

The repository and the objects it returns each have an interface, implemented by the local type:

| Interface | Local implementation | Role |
|---|---|---|
| <xref:FulcrumFS.IFileRepo> | <xref:FulcrumFS.FileRepo> | Transactions, fetches, variants, enumeration, cleanup and events. |
| <xref:FulcrumFS.IFileRepoTransaction> | <xref:FulcrumFS.FileRepoTransaction> | Adds and deletes main files, committed or rolled back as a whole. |
| <xref:FulcrumFS.IFileRepoStagedTransaction> | <xref:FulcrumFS.FileRepoStagedTransaction> | A transaction for many deletions, committed in two calls. |
| <xref:FulcrumFS.IFileRepoStage> | <xref:FulcrumFS.FileRepoStage> | The second of those two calls. |
| <xref:FulcrumFS.IRepoFileGroup> | <xref:FulcrumFS.RepoFileGroupInfo> | A main file together with its variants. |
| <xref:FulcrumFS.IRepoFile> | <xref:FulcrumFS.RepoFileInfo> | A main file or a variant, read as a stream. |

<xref:FulcrumFS.IFileRepo> extends <xref:FulcrumFS.IFileRepoCleaner>, so cleanup passes run through the same reference. Processing pipelines are used as before: an add takes a source stream, its extension and an <xref:FulcrumFS.IFileProcessingPipelineSelector>, and returns a <xref:FulcrumFS.TaskWithProgress`1> whose progress can be observed.

#### Files are read as streams

A file stored by another implementation may have no local path, so <xref:FulcrumFS.IRepoFile> exposes its content only through <xref:FulcrumFS.IRepoFile.OpenReadAsync*>, and <xref:FulcrumFS.IFileRepo.OpenAsync*?displayProperty=nameWithType> and <xref:FulcrumFS.IFileRepo.OpenVariantAsync*?displayProperty=nameWithType> return a <xref:System.IO.Stream> rather than a <xref:System.IO.FileStream>. The stream is read-only, starts at the beginning of the content, and belongs to the caller, who must dispose it.

The stream is always seekable: <xref:System.IO.Stream.CanSeek> is `true`, and <xref:System.IO.Stream.Length>, <xref:System.IO.Stream.Position> and <xref:System.IO.Stream.Seek*> work, so code that seeks, such as a handler that serves byte ranges, works with every implementation. The stream may support only asynchronous reads, though. Read it with <xref:System.IO.Stream.ReadAsync*> or <xref:System.IO.Stream.CopyToAsync*> and do not rely on <xref:System.IO.Stream.Read*>: the local implementation returns a <xref:System.IO.FileStream>, which supports both, but a stream that reads over a network may not.

#### Opening a stream takes an access pattern

Every method that opens a stream takes a <xref:FulcrumFS.FileAccessPattern>: <xref:FulcrumFS.FileAccessPattern.Sequential> when the content is read from start to end, and <xref:FulcrumFS.FileAccessPattern.Random> when the caller seeks within it. The pattern is a hint. The stream is seekable and returns the same content either way, but an implementation can read more efficiently when it knows what to expect: the local one passes the pattern to the operating system as <xref:System.IO.FileOptions.SequentialScan> or <xref:System.IO.FileOptions.RandomAccess>, and one that reads over a network can prefetch ahead of a sequential reader but fetch only the ranges that a random reader asks for.

The parameter is required and has no default value, because the caller always knows which pattern it is going to use, and a default would be wrong in exactly the case the hint exists for: a network implementation that assumed sequential reading would prefetch content that a caller about to seek then skips.

#### Sizes are read on demand

A fetch does not read the size of a file: it finds the file by its name alone, and reading the size as well would cost a separate metadata read for every fetched file and every member of a group, which a remote implementation would pay for on every request whether or not anyone wants the size. When you need it, call <xref:FulcrumFS.IRepoFile.GetLengthAsync*?displayProperty=nameWithType>. This is why the size comes from a method rather than a property: a property would make every implementation produce the size at fetch time, while the method costs nothing until it is called.

The instance caches the size once it is known. Stored files never change, so later calls return it without I/O, and an implementation that already knows the size, for example from the metadata a remote fetch returned, answers the first call without I/O as well. Once the content is open, the <xref:System.IO.Stream.Length> of the stream is the size too. The local <xref:FulcrumFS.RepoFileInfo> also keeps its <xref:FulcrumFS.RepoFileInfo.Length> property, which answers from the same cached size.

Files returned by an add are the exception: they know their size from the start. The add has just written the content, so the size is known where that happened, and the common pattern of adding a file and then recording its size would otherwise pay for reading it back. <xref:FulcrumFS.IRepoFile.GetLengthAsync*> therefore completes synchronously, with no storage access or network round trip, for every file returned by <xref:FulcrumFS.IFileRepoTransaction.AddAsync*?displayProperty=nameWithType>, <xref:FulcrumFS.IFileRepoStagedTransaction.AddAsync*?displayProperty=nameWithType>, <xref:FulcrumFS.IFileRepo.AddVariantAsync*?displayProperty=nameWithType> and <xref:FulcrumFS.IFileRepo.TryAddVariantAsync*?displayProperty=nameWithType>. <xref:FulcrumFS.IFileRepo.GetOrAddVariantAsync*?displayProperty=nameWithType> gives the same guarantee for the variants it added; the ones that already existed are returned as they are and may read their size on demand, like fetched files.

#### The local types keep their members

<xref:FulcrumFS.FileRepo> and the types it returns keep all of their members. Where a member returns a local type, such as <xref:FulcrumFS.FileRepo.GetAsync*?displayProperty=nameWithType> returning a <xref:FulcrumFS.RepoFileInfo>, the interface member is implemented explicitly and forwards to it, so the interface hands out the local objects typed as interfaces. Code that holds a <xref:FulcrumFS.FileRepo> can pass it to code that takes an <xref:FulcrumFS.IFileRepo>.

## What Stays on the Local Class

Creating and configuring a repository depends on where it is stored, so these members are only on <xref:FulcrumFS.FileRepo> and its local types:

- The constructors and <xref:FulcrumFS.FileRepo.Options>.
- <xref:FulcrumFS.FileRepo.EnsureCreated*> and <xref:FulcrumFS.FileRepo.EnsureInitialized*>, which create, repair and open the directory.
- <xref:FulcrumFS.FileRepo.FilesDirectory> and <xref:FulcrumFS.RepoFileInfo.Path>, the local paths, and <xref:FulcrumFS.RepoFileInfo.Open*>, which returns a <xref:System.IO.FileStream>.
- <xref:FulcrumFS.RepoFileInfo.Length>, which reads the size once and keeps it (use <xref:FulcrumFS.RepoFileInfo.Path> for a live read), while <xref:FulcrumFS.IRepoFile.GetLengthAsync*?displayProperty=nameWithType> reads it once and caches it.
- <xref:FulcrumFS.FileRepo.Dispose*>, which releases the repository lock. The interface is not disposable, because code that uses a repository does not own it.

<xref:FulcrumFS.IFileRepo.DeleteMode?displayProperty=nameWithType>, <xref:FulcrumFS.IFileRepo.FileIdMode?displayProperty=nameWithType> and <xref:FulcrumFS.IFileRepo.IsReadOnly?displayProperty=nameWithType> are on the interface even though they come from the options, because applications decide behavior on them: whether a deleted file can still be read until a cleanup pass removes it, whether file IDs can be shown to untrusted parties, or whether the repository can be written to at all (see [Read-Only Repositories](../guides/read-only-repositories.md)).

## Using the Interface

Create and configure the repository where the application is composed, and give the rest of the application the interface. With dependency injection, register the local class as a singleton and the interface as the same instance:

```csharp
builder.Services.AddSingleton(_ => {
    var baseDir = DirectoryPath.ParseAbsolute(@"C:\AppData\PhotoApp\Files");

    var repo = new FileRepo(baseDir, RepoFormatVersion.Version2, options => {
        options.DeleteMode = DeleteMode.DeferredUntilClean;
    });

    repo.EnsureCreated();
    return repo;
});

builder.Services.AddSingleton<IFileRepo>(services => services.GetRequiredService<FileRepo>());
```

A service then takes an <xref:FulcrumFS.IFileRepo> and works with the interfaces throughout:

```csharp
public sealed class PhotoService(IFileRepo repo, PhotoDbContext dbContext)
{
    public async Task<FileId> UploadAsync(Stream source, string extension, CancellationToken cancellationToken)
    {
        await using var txn = await repo.BeginTransactionAsync();
        await using var dbTxn = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        IRepoFileGroup added = await txn.AddAsync(source, extension, leaveOpen: true, PhotoPipelines.Upload, cancellationToken);

        dbContext.Photos.Add(new Photo { FileId = added.FileId });
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbTxn.CommitAsync(cancellationToken);   // Database first.

        await txn.CommitAsync(cancellationToken);     // Repository second.
        return added.FileId;
    }

    public async Task<Stream> OpenThumbnailAsync(FileId fileId, CancellationToken cancellationToken)
    {
        var variants = await repo.GetOrAddVariantAsync(fileId, "thumbnail", PhotoPipelines.Thumbnail, cancellationToken);
        return await variants[0].OpenReadAsync(FileAccessPattern.Sequential, cancellationToken);
    }
}
```

Nothing in this service depends on the files being stored in a directory.

> [!NOTE]
> Disposing the repository is the job of whatever created it. The dependency injection container disposes the <xref:FulcrumFS.FileRepo> singleton when the application stops; services that receive the interface never dispose it.

## Requirements for Implementations

Another implementation must behave as the documentation of each interface member describes, so that applications do not need to know which implementation they have. In particular:

- **The commit model holds.** A file whose add has returned is durable, committing the database before the repository is safe, and a file whose commit or rollback did not finish stays accessible until a cleanup pass resolves it through the resolution callback. Failures of individual files during a commit or rollback are reported through <xref:FulcrumFS.IFileRepo.TransactionCompletionFailed> rather than thrown. See [Transactional Commit Model](commit-model.md).
- **An instance is safe for concurrent use**, since one instance serves the whole application.
- **Open streams do not block deletion.** A reader holding a stream from <xref:FulcrumFS.IRepoFile.OpenReadAsync*> must not prevent the file from being deleted or cleaned up.
- **Streams are seekable, whatever the access pattern.** The stream must report the size of the file as its <xref:System.IO.Stream.Length> and support <xref:System.IO.Stream.Seek*>, and it must return the same content for either <xref:FulcrumFS.FileAccessPattern>. It may support only asynchronous reads. An implementation that reads over a network can provide seeking with range requests.
- **Sizes are cached once known.** <xref:FulcrumFS.IRepoFile.GetLengthAsync*> returns the same size as the <xref:System.IO.Stream.Length> of an opened stream, and once an instance knows it, later calls on that instance return it without I/O, even after the file has been deleted.
- **Files returned by an add know their size.** For every file in the result of an add, and for the variants that <xref:FulcrumFS.IFileRepo.GetOrAddVariantAsync*> added, <xref:FulcrumFS.IRepoFile.GetLengthAsync*> completes synchronously and requires no storage access or network round trip. The add knows the size of what it has just written; a remote implementation returns it in the add response.

#### File IDs from uncoordinated generators

> [!IMPORTANT]
> A replicated or remote implementation must keep enough random bits in the file IDs it generates that two generators that are never coordinated, such as a primary and a secondary promoted during a fail-over, cannot collide.

A single repository could make its IDs unique by construction, for example with a counter. A replicated implementation cannot rely on that: after a fail-over, the promoted secondary generates IDs without knowing which IDs the old primary generated in its last moments, and applications may already have stored some of those. Uniqueness then rests only on the random part of each ID.

The version 7 IDs that FulcrumFS generates in <xref:FulcrumFS.FileIdMode.Sequential> mode keep 70 random bits for this reason (see <xref:FulcrumFS.FileIdGenerator>), and the version 4 IDs of <xref:FulcrumFS.FileIdMode.Secure> mode are random apart from their version and variant bits. The simplest way for an implementation to meet the requirement is to generate its IDs the same way, with <xref:FulcrumFS.FileId.CreateSecure*> or a <xref:FulcrumFS.FileIdGenerator>.

## Further Reading

These articles describe the behavior that every implementation keeps:

- [Transactional Commit Model](commit-model.md) - Commit ordering, staged transactions and the indeterminate state.
- [Crash Recovery and Invariants](crash-recovery.md) - What the cleaner finishes after a crash.
- [Fetching Files](../guides/fetching-files.md) - Reading files and variants with the local types.

</div>
