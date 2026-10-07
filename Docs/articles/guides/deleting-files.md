<div class="article">

# Deleting Files and Variants

Deletion in FulcrumFS is designed to be safe under concurrency and crashes. This guide covers deleting whole files inside a transaction (for example when a user deletes a document), retiring individual variants (for example dropping a thumbnail size that is no longer needed), and choosing when deletions take physical effect.

### Logical versus physical deletion

A deletion becomes *logically committed* the moment its delete marker is written. For a retired variant, fetches treat it as gone from that moment. A deleted main file is different, by design: it stays readable by its <xref:FulcrumFS.FileId> until the cleaner physically removes it, so a transaction or reader that still holds the ID, or a database snapshot taken before the delete, keeps working through the grace window. The bytes are reclaimed later by the cleaner. This separation is what lets concurrent readers, backups, and crash recovery all behave correctly: a stream a client started reading just before the delete keeps working until they close it, and a backup that ran during the grace window still captures the deleted content. See [Crash Recovery and Invariants](../concepts/crash-recovery.md).

## Deleting a Whole File

Deleting a file (its main file and all variants) happens inside a transaction with <xref:FulcrumFS.FileRepoTransaction.DeleteAsync*>. Pair it with the database operation that removes the owning row, committing the database first so a crash between the two leaves at worst a logically-deleted file with no row referencing it (which the cleaner reclaims) rather than a live row referencing a deleted file.

```csharp
// Handler for "DELETE /api/documents/{id}".
await using var txn = await repo.BeginTransactionAsync();
await using var dbTxn = await dbContext.Database.BeginTransactionAsync();

await txn.DeleteAsync(doc.FileId);

dbContext.Documents.Remove(doc);
await dbContext.SaveChangesAsync();
await dbTxn.CommitAsync();   // Database first.

await txn.CommitAsync();     // Repository second.
```

If the transaction rolls back instead of committing, the deletion is abandoned and the file remains.

#### Retrying and replaying deletes

<xref:FulcrumFS.FileRepoTransaction.DeleteAsync*> throws <xref:FulcrumFS.RepoFileNotFoundException> when the file is not there. That is deliberate: when the application believes a file exists, a missing one points to an application bug (such as a stale reference) or to a referenced file that has gone missing, and either should surface.

Deleting a file that is already pending deletion succeeds and converges: if another transaction has deleted the file and a cleanup pass has not removed it yet, the second deletion is accepted and both are carried out as one, in every delete mode and whichever kind of transaction made them. Only a file that is gone, or never existed, throws.

Work that may already have run is different. A queue of deletions replayed after a crash, or a retried request whose first attempt may have committed, needs to make sure a file is gone without failing when it already is. Use <xref:FulcrumFS.FileRepoTransaction.TryDeleteAsync*> for that: it deletes the file if it exists and returns whether it did. When the file is already gone it returns `false` without writing anything; a file that is pending deletion still exists, so it returns `true` and the deletions converge.

```csharp
// Replay deletions recorded in the database, for example after a crash.
foreach (var pending in await dbContext.PendingFileDeletions.ToListAsync())
{
    await using var txn = await repo.BeginTransactionAsync();

    if (!await txn.TryDeleteAsync(pending.FileId))
        logger.LogInformation("File {FileId} was already gone.", pending.FileId);

    dbContext.PendingFileDeletions.Remove(pending);
    await dbContext.SaveChangesAsync();   // Database first.

    await txn.CommitAsync();              // Repository second.
}
```

Use <xref:FulcrumFS.FileRepoTransaction.DeleteAsync*> whenever the application believes the file exists, so that a mismatch surfaces, and <xref:FulcrumFS.FileRepoTransaction.TryDeleteAsync*> only when replaying work that may already have run. Within a single transaction, deleting the same file a second time does nothing with either method.

#### Deleting many files at once

When one operation deletes many files, such as removing an album with thousands of photos, use a staged transaction from <xref:FulcrumFS.FileRepo.BeginStagedTransactionAsync*>. An ordinary transaction writes and flushes a marker and holds a file handle for every file it deletes until it commits. A <xref:FulcrumFS.FileRepoStagedTransaction> records the deletions in memory and writes them all to one journal file when it is staged, so the cost no longer grows with the number of files while the database transaction is open. On a repository of [format version](../concepts/repository-layout.md#format-version) 1, which has no journals, it writes a marker per file when it is staged instead, with identical behavior. It commits in two calls placed around the database commit:

```csharp
// Handler for "DELETE /api/albums/{id}".
await using var txn = await repo.BeginStagedTransactionAsync();
await using var dbTxn = await dbContext.Database.BeginTransactionAsync();

foreach (var photo in album.Photos)
    await txn.DeleteAsync(photo.FileId);

dbContext.Albums.Remove(album);
await dbContext.SaveChangesAsync();

var stage = await txn.StageAsync();   // Write the journal.
await dbTxn.CommitAsync();            // Database first.
await stage.CommitAsync();            // Repository second.
```

<xref:FulcrumFS.FileRepoStagedTransaction.DeleteAsync*> and <xref:FulcrumFS.FileRepoStagedTransaction.TryDeleteAsync*> behave like their ordinary counterparts, including the exception for a file that is not there. Once the stage is committed, the journal is the delete record of all of its files: in the deferred delete modes the cleaner deletes them once the journal is older than the delete delay, and in <xref:FulcrumFS.DeleteMode.Immediate> mode the commit deletes them straight away. See [Staged Transactions](../concepts/commit-model.md#staged-transactions) for the full model.

## Retiring Variants

Remove one or more variants from a single file group with <xref:FulcrumFS.FileRepo.DeleteVariantsAsync*>, which takes a <xref:FulcrumFS.FileId> and a span of variant IDs. A common use is dropping a thumbnail size that the front-end no longer renders. The call operates on the variants of one file, so a batch migration loops over the affected files:

```csharp
// Drop the old 128px thumbnails for every photo in a batch migration.
foreach (var photo in await dbContext.Photos.ToListAsync())
    await repo.DeleteVariantsAsync(photo.FileId, "thumbnail-128");
```

This call is not part of a <xref:FulcrumFS.FileRepoTransaction>: variant retirement does not need to coordinate with a database commit because the database does not reference variants. The commit ordering rule applies to main-file adds and deletes, not to variants.

Because it is not part of a transaction, the call commits on its own as it runs and cannot be ordered with a database commit. An application that does record variant IDs in its database must therefore tolerate a recorded variant being gone, for example by regenerating it on demand with <xref:FulcrumFS.FileRepo.GetOrAddVariantAsync*>, rather than expect the retirement and its database update to commit together.

Retirement is designed to be forgiving and predictable:

- Variant IDs that were never added or are already retired are silent no-ops, and an empty list does nothing. A migration script can therefore run safely against a mixed repository where some files already lack the variant being dropped.
- The call returns the IDs of the variants it actually retired, normalized and in ordinal order. The skipped IDs are not in the list, and neither are unlisted variants that were preserved, so an empty list means there was nothing to retire.
- The order of the IDs does not matter; inputs are deduplicated and sorted internally.
- A variant that is *not* listed but depends on one that *is* listed is preserved. The system promotes a survivor to hold the real data and re-points the surviving siblings at it.

> [!NOTE]
> The dependency-preservation rule means you cannot accidentally destroy a variant that another consumer still depends on without naming it explicitly. If you want to drop a "medium" thumbnail that the "small" thumbnail is internally aliased against, the "small" one keeps working: it gets promoted to a real file behind the scenes. See [Retiring Variants](file-variants.md#retiring-variants) for the survivor-promotion rule.

## Choosing a Delete Mode

When deletions take physical effect is governed by <xref:FulcrumFS.FileRepoOptions.DeleteMode>, set at repository construction:

#### Deferred until clean (default)

<xref:FulcrumFS.DeleteMode.DeferredUntilClean> records the deletion and defers the physical removal until a cleaner pass runs past the grace period. This is the recommended mode: it lets concurrent transactions keep reading a file that is being deleted, supports crash recovery, and gives scheduled backups a window to capture files before they vanish.

In this mode and in <xref:FulcrumFS.DeleteMode.DeferredFilesOnly> mode, on a repository of [format version](../concepts/repository-layout.md#format-version) 2 or later, a committing transaction records its deletions in one delete journal, flushed to disk once for all of them, when it is a staged transaction or an ordinary one that deletes two or more files. Otherwise each deleted file gets a delete marker of its own.

#### Immediate

<xref:FulcrumFS.DeleteMode.Immediate> removes files as soon as the operation completes or the containing transaction commits, with no marker and no grace period. Use it only when the deferral guarantees are not needed, for example a scratch repository used by tests or a short-lived processing cache that does not participate in backups.

> [!CAUTION]
> Immediate deletion forfeits the grace window that protects in-flight readers and backups. A concurrent stream of a file being deleted may fail mid-read, and a backup that runs after a delete will not capture the deleted bytes. Prefer the default unless you have a specific reason not to.

#### Deferred files only

<xref:FulcrumFS.DeleteMode.DeferredFilesOnly> defers main-file deletions but removes variants immediately. This is appropriate only when variants are excluded from backups (for example because they are regenerated on demand) or when force-removing variants on an offline, already-backed-up repository.

## Reclaiming Space

Deferred deletions occupy disk until a cleaner pass removes markers older than the configured delay. Schedule <xref:FulcrumFS.FileRepo.CleanAsync*> to reclaim that space, as described in [Repository Cleanup](cleanup.md).

> [!TIP]
> Pick the cleaner's delete delay (commonly 24 hours) to be longer than your backup interval. That way every deleted file is captured by at least one backup before its bytes are reclaimed, so a restore can recover content a user accidentally deleted.

## Next Steps

- [Repository Cleanup](cleanup.md) - Reclaiming deferred deletions and resolving indeterminate files.
- [Retiring Variants](file-variants.md#retiring-variants) - The variant retirement model.
- [Transactional Commit Model](../concepts/commit-model.md) - Commit ordering for deletions.

</div>
