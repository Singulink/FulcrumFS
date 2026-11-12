<div class="article">

# Adding and Committing Files

Files enter a repository through a transaction. This guide covers the add APIs, the transaction lifecycle, rollback behavior, and how to coordinate a repository commit with a database commit so a user-uploaded file and the database row that references it stay consistent.

### When to use a transaction

Every add goes through a <xref:FulcrumFS.FileRepoTransaction>. A transaction can add multiple files and is the unit that commits or rolls back as a whole, which is what lets you align a batch of file operations with a single database transaction. Even a single-file upload should be wrapped in one so the add can be rolled back if the surrounding database work fails.

## Beginning a Transaction

Create a transaction with <xref:FulcrumFS.FileRepo.BeginTransactionAsync*>. The returned <xref:FulcrumFS.FileRepoTransaction> is an <xref:System.IAsyncDisposable>, so an `await using` block guarantees that it lets go of its files.

```csharp
await using var txn = await repo.BeginTransactionAsync();
```

A transaction ends with <xref:FulcrumFS.FileRepoTransaction.CommitAsync*> or <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*>. If the block exits without either, for example because an exception was thrown inside it, disposal does not roll the transaction back: the files it added are left indeterminate, still readable, and the next cleanup pass that is given a resolution callback deletes the ones that no database row references. Disposal cannot discard them itself, because it cannot know whether your database committed before the exception was thrown. When you know that it did not, call <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*> to discard them at once (see [Rolling Back](#rolling-back)).

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

The result is a <xref:FulcrumFS.RepoFileGroupInfo>. Its <xref:FulcrumFS.RepoFileGroupInfo.FileId> is the handle you persist on the owning database row (a `Documents.FileId` column, say); its <xref:FulcrumFS.RepoFileGroupInfo.MainFile> and <xref:FulcrumFS.RepoFileGroupInfo.VariantFiles> describe what was produced when the pipeline generates variants such as thumbnails. Every data file an add stores is flushed to disk before <xref:FulcrumFS.FileRepoTransaction.AddAsync*> returns, so the add survives a power loss; that flush per file is the cost of the guarantee (see [Durability Across Power Loss](../concepts/crash-recovery.md#durability-across-power-loss)). The files in the result already know their sizes, which the add took from the files it stored, so reading <xref:FulcrumFS.RepoFileInfo.Length> to record the size of an upload on its row costs no further I/O.

> [!TIP]
> Pass `leaveOpen: true` when the calling code is responsible for disposing the source stream (the common case with HTTP upload streams owned by the request scope). Pass `leaveOpen: false` only when you want the transaction to take ownership of the stream: the add then closes it whether it succeeds or fails.

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

Call <xref:FulcrumFS.FileRepoTransaction.CommitAsync*> to make the adds permanent.

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
> A failed commit does not throw. Affected files are marked indeterminate and remain fully readable and usable, and the failure is reported through the <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> event (with <xref:FulcrumFS.RepoTransactionCompletionFailureInfo.Operation> set to <xref:FulcrumFS.RepoTransactionCompletionOperation.Commit>). Subscribe to it if you need to log, alert, or surface commit failures to operators. See [Exception Handling](exception-handling.md).

## Rolling Back

To abandon a transaction, call <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*>. The tentatively added files are discarded at once; if rollback itself cannot complete cleanly, the affected files become indeterminate and the same <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> event fires with <xref:FulcrumFS.RepoTransactionCompletionFailureInfo.Operation> set to <xref:FulcrumFS.RepoTransactionCompletionOperation.Rollback>. Roll back when you know that the database did not commit, which you do whenever the failure comes before the database commit is attempted:

```csharp
await using var txn = await repo.BeginTransactionAsync();
var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline);

if (!await TrySaveDocumentRowAsync(added.FileId))
{
    // The database refused the row (for example a unique-constraint violation),
    // so nothing references the just-added file: discard it now.
    await txn.RollbackAsync();
    return Results.Conflict();
}

await txn.CommitAsync();
```

#### Disposing without a commit or a rollback

Disposing a transaction that was neither committed nor rolled back is not a rollback. The files it added are left indeterminate and readable, and the next cleanup pass with a resolution callback deletes those that no database row references (see [Resolving Indeterminate Files](cleanup.md#resolving-indeterminate-files)). This is deliberate. When an exception skips the repository commit, the transaction cannot tell a database commit that never happened from one that took effect although its call threw, for example because the connection was lost before the server's answer arrived. Discarding the files in the second case would leave a committed row pointing at a file that is gone. See [A Database Commit with an Unknown Outcome](../concepts/commit-model.md#a-database-commit-with-an-unknown-outcome).

So an upload handler with error handling rolls back around the work that precedes the database commit, and not around the database commit itself:

```csharp
await using var source = uploadedFile.OpenReadStream();
await using var txn = await repo.BeginTransactionAsync();
await using var dbTxn = await dbContext.Database.BeginTransactionAsync();

try
{
    var added = await txn.AddAsync(source, ".pdf", leaveOpen: true, pipeline);

    dbContext.Documents.Add(new Document { Id = documentId, FileId = added.FileId });
    await dbContext.SaveChangesAsync();
}
catch
{
    // The database commit has not been attempted, so no row can reference
    // the added file: discard it now instead of leaving it for cleanup.
    await txn.RollbackAsync();
    throw;
}

await dbTxn.CommitAsync();   // 1. Database first. If this throws, its outcome is unknown: no rollback.
await txn.CommitAsync();     // 2. Repository second.
```

> [!IMPORTANT]
> Do not call <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*> in a handler that an exception from the database commit can reach. Let the transaction be disposed instead: cleanup then decides from what the database actually contains.

> [!NOTE]
> Rolling back is never required for correctness. A transaction that is only disposed leaves its added files for cleanup, which removes them if no row references them; the explicit rollback reclaims them immediately instead of at the next cleanup pass. Such a pass needs a resolution callback, so make sure one is scheduled (see [Repository Cleanup](cleanup.md)): a repository that never runs cleanup with a callback keeps these files.

## Next Steps

- [Fetching Files](fetching-files.md) - Read files and variants back out.
- [Processing Pipelines](processing-pipelines.md) - Validate and transform during add.
- [Exception Handling](exception-handling.md) - Commit and rollback failure events.

</div>
