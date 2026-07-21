<div class="article">

# Deleting Files and Variants

Deletion in FulcrumFS is designed to be safe under concurrency and crashes. This guide covers deleting whole files inside a transaction (for example when a user deletes a document), retiring individual variants (for example dropping a thumbnail size that is no longer needed), and choosing when deletions take physical effect.

### Logical versus physical deletion

A deletion becomes *logically committed* the moment its delete marker is written, at which point fetches treat the file as gone. The bytes may be reclaimed later by the cleaner. This separation is what lets concurrent readers, backups, and crash recovery all behave correctly: a stream a client started reading just before the delete keeps working until they close it, and a backup that ran during the grace window still captures the deleted content. See [Crash Recovery and Invariants](../concepts/crash-recovery.md).

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

## Retiring Variants

Remove one or more variants from a single file group with <xref:FulcrumFS.FileRepo.DeleteVariantsAsync*>, which takes a <xref:FulcrumFS.FileId> and a span of variant IDs. A common use is dropping a thumbnail size that the front-end no longer renders. The call operates on the variants of one file, so a batch migration loops over the affected files:

```csharp
// Drop the old 128px thumbnails for every photo in a batch migration.
foreach (var photo in await dbContext.Photos.ToListAsync())
    await repo.DeleteVariantsAsync(photo.FileId, "thumbnail-128");
```

This call is not part of a <xref:FulcrumFS.FileRepoTransaction>: variant retirement does not need to coordinate with a database commit because the database does not reference variants. The commit ordering rule applies to main-file adds and deletes, not to variants.

Retirement is designed to be forgiving and predictable:

- Variant IDs that were never added or are already retired are silent no-ops, and an empty list does nothing. A migration script can therefore run safely against a mixed repository where some files already lack the variant being dropped.
- The order of the IDs does not matter; inputs are deduplicated and sorted internally.
- A variant that is *not* listed but depends on one that *is* listed is preserved. The system promotes a survivor to hold the real data and re-points the surviving siblings at it.

> [!NOTE]
> The dependency-preservation rule means you cannot accidentally destroy a variant that another consumer still depends on without naming it explicitly. If you want to drop a "medium" thumbnail that the "small" thumbnail is internally aliased against, the "small" one keeps working: it gets promoted to a real file behind the scenes. See [Retiring Variants](file-variants.md#retiring-variants) for the survivor-promotion rule.

## Choosing a Delete Mode

When deletions take physical effect is governed by <xref:FulcrumFS.FileRepoOptions.DeleteMode>, set at repository construction:

#### Deferred until clean (default)

<xref:FulcrumFS.DeleteMode.DeferredUntilClean> writes a delete marker and defers the physical removal until a cleaner pass runs past the grace period. This is the recommended mode: it lets concurrent transactions keep reading a file that is being deleted, supports crash recovery, and gives scheduled backups a window to capture files before they vanish.

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
