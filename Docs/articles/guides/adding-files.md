<div class="article">

# Adding and Committing Files

Files enter a repository through a transaction. This guide covers the add APIs, the transaction lifecycle, rollback behavior, and how to coordinate a repository commit with a database commit so a user-uploaded file and the database row that references it stay consistent.

### When to use a transaction

Every add goes through a <xref:FulcrumFS.FileRepoTransaction>. A transaction can add multiple files and is the unit that commits or rolls back as a whole, which is what lets you align a batch of file operations with a single database transaction. Even a single-file upload should be wrapped in one so the add can be rolled back if the surrounding database work fails.

## Beginning a Transaction

Create a transaction with <xref:FulcrumFS.FileRepo.BeginTransactionAsync*>. The returned <xref:FulcrumFS.FileRepoTransaction> is an <xref:System.IAsyncDisposable>, so an `await using` block guarantees it is finalized.

```csharp
await using var txn = await repo.BeginTransactionAsync();
```

If the block exits without a successful <xref:FulcrumFS.FileRepoTransaction.CommitAsync*>, disposal rolls the transaction back automatically. This means an exception thrown anywhere inside the block cleans up any tentatively added files without further effort on your part.

## Adding Files

<xref:FulcrumFS.FileRepoTransaction.AddAsync*> has two overloads. One takes a <xref:System.IO.FileStream> and infers the extension from it; the other takes any <xref:System.IO.Stream> together with an explicit extension. Both take a `leaveOpen` flag and an <xref:FulcrumFS.IFileProcessingPipelineSelector> that decides how the source is processed.

```csharp
// Storing a user-uploaded PDF attachment with no processing.
await using var source = uploadedFile.OpenReadStream();

var added = await txn.AddAsync(
    source,
    extension: ".pdf",
    leaveOpen: true,
    FileProcessingPipeline.Empty);

FileId fileId = added.FileId;
```

The result is a <xref:FulcrumFS.RepoFileGroupInfo>. Its <xref:FulcrumFS.RepoFileGroupInfo.FileId> is the handle you persist on the owning database row (a `Documents.FileId` column, say); its <xref:FulcrumFS.RepoFileGroupInfo.MainFile> and <xref:FulcrumFS.RepoFileGroupInfo.VariantFiles> describe what was produced when the pipeline generates variants such as thumbnails.

> [!TIP]
> Pass `leaveOpen: true` when the calling code is responsible for disposing the source stream (the common case with HTTP upload streams owned by the request scope). Pass `leaveOpen: false` only when you want the transaction to take ownership of the stream.

#### Passing a pipeline or a selector

When every source should be processed the same way, pass a single <xref:FulcrumFS.FileProcessingPipeline> directly, since it implements <xref:FulcrumFS.IFileProcessingPipelineSelector>. When different file types need different handling (for example an attachment endpoint that accepts both PDFs and images), pass a <xref:FulcrumFS.FileProcessingPipelineSelector> that routes by extension. See [Processing Pipelines](processing-pipelines.md).

#### Adding several files in one transaction

A single <xref:FulcrumFS.FileRepoTransaction> can carry any number of adds (and deletes). All of them commit or roll back together, which is exactly what you want when a database operation creates multiple rows that each reference a file (a forum post with several attachments, an import that produces a document plus generated previews, etc.):

```csharp
await using var txn = await repo.BeginTransactionAsync();

var added = new List<RepoFileGroupInfo>();

foreach (var upload in uploadedFiles)
{
    await using var source = upload.OpenReadStream();
    added.Add(await txn.AddAsync(source, Path.GetExtension(upload.FileName), leaveOpen: true, pipeline));
}

// ... persist each added.FileId on its owning row, commit the database, then:
await txn.CommitAsync();
```

## Committing

Call <xref:FulcrumFS.FileRepoTransaction.CommitAsync*> to make the adds durable.

```csharp
await txn.CommitAsync();
```

When the file is owned by a database row, commit the database transaction first and the repository transaction second. A crash between the two leaves at worst a harmless orphan file that the cleaner reclaims later. See [Transactional Commit Model](../concepts/commit-model.md) for why this ordering is required.

A complete upload handler therefore looks like this:

```csharp
await using var source = uploadedFile.OpenReadStream();
await using var txn = await repo.BeginTransactionAsync();
await using var dbTxn = await dbContext.Database.BeginTransactionAsync();

var added = await txn.AddAsync(source, ".pdf", leaveOpen: true, pipeline);

dbContext.Documents.Add(new Document { Id = documentId, FileId = added.FileId });
await dbContext.SaveChangesAsync();
await dbTxn.CommitAsync();   // 1. Database first.

await txn.CommitAsync();     // 2. Repository second.
```

> [!IMPORTANT]
> A failed commit does not throw. Affected files are marked indeterminate and remain fully readable and usable, and the failure is reported through the <xref:FulcrumFS.FileRepo.CommitFailed> event. Subscribe to it if you need to log, alert, or surface commit failures to operators. See [Exception Handling](exception-handling.md).

## Rolling Back

To abandon a transaction explicitly, call <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*>. Otherwise, disposing without committing rolls back automatically. Tentatively added files are discarded; if rollback itself cannot complete cleanly, the affected files become indeterminate and the <xref:FulcrumFS.FileRepo.RollbackFailed> event fires.

```csharp
await using var txn = await repo.BeginTransactionAsync();
var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline);

if (!await TrySaveDocumentRowAsync(added.FileId))
{
    // The database refused the row (for example a unique-constraint violation).
    // Roll back so the just-added file does not become an orphan.
    await txn.RollbackAsync();
    return Results.Conflict();
}

await txn.CommitAsync();
```

> [!NOTE]
> The explicit rollback call is rarely needed in practice; letting the `await using` block dispose the transaction is enough. Reach for <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*> when you want to release the tentative state before later code runs, or to make the abandonment intent obvious at the call site.

## Next Steps

- [Fetching Files](fetching-files.md) - Read files and variants back out.
- [Processing Pipelines](processing-pipelines.md) - Validate and transform during add.
- [Exception Handling](exception-handling.md) - Commit and rollback failure events.

</div>
