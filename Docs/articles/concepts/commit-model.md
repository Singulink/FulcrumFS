<div class="article">

# Transactional Commit Model

FulcrumFS gives file storage database-aligned commit semantics without requiring distributed transactions. This article explains the transaction lifecycle, the commit ordering rule that coordinates a repository with a database, and the indeterminate state that makes crash recovery safe.

### The core idea

A file almost always belongs to a row in a database: a `Photos.FileId`, a `Documents.FileId`, an attachment id on a forum post. The hard problem is keeping the file and the row consistent when either store can fail independently. The classical answer is a two-phase commit coordinator, but those bring their own indeterminate post-crash states (an in-doubt transaction can block a database from reopening) and require the database to participate.

FulcrumFS solves the problem differently: a strict commit ordering plus a cleanup pass that reclaims orphans. The ordering pushes any uncertainty a crash leaves behind into a shape the cleaner can resolve safely, with no coordinator and no special database support.

## Guarantees

When your application follows the commit ordering rule (commit the database first, then the repository), FulcrumFS guarantees two things, even across process crashes and unreliable file systems:

1. **Every file the database references is accessible in the repository.** A committed database row can always open its file, including one whose repository commit had not finished when a crash occurred.
2. **Every file the database no longer references is eventually deleted.** Orphaned and deleted files are reclaimed by a cleanup pass, so storage does not leak.

The rest of this article explains the mechanisms that deliver these guarantees: the commit ordering rule, the indeterminate state, and orphan reclamation.

## Transaction Lifecycle

A repository transaction is created with <xref:FulcrumFS.FileRepo.BeginTransactionAsync*> and represented by <xref:FulcrumFS.FileRepoTransaction>, which is an <xref:System.IAsyncDisposable>. Within it you add files with <xref:FulcrumFS.FileRepoTransaction.AddAsync*> and remove them with <xref:FulcrumFS.FileRepoTransaction.DeleteAsync*>, then finalize with <xref:FulcrumFS.FileRepoTransaction.CommitAsync*>.

```csharp
await using var txn = await repo.BeginTransactionAsync();

var added = await txn.AddAsync(stream, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty);

await txn.CommitAsync();
```

If <xref:FulcrumFS.FileRepoTransaction.CommitAsync*> is never called, disposing the transaction rolls it back: tentatively added files are discarded and pending deletions are abandoned. You can also roll back explicitly with <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*>.

## The Commit Ordering Rule

When a file is owned by a database row, the two commits must happen in a fixed order:

1. Commit the **database** transaction first.
2. Commit the **repository** transaction second.

If the process crashes between the two, the database row references a repository file whose commit had not yet completed. This is safe: the repository keeps that file accessible exactly as if it had committed, flagging it as *indeterminate* so a later cleanup pass can resolve it against the database. Because the owning row exists, the file is kept and the reference is never left dangling.

The reverse ordering is what would be unsafe. Committing the repository first means a crash before the database commit leaves a fully committed file that no row references, an orphan that has to be reclaimed separately and is indistinguishable from a valid file without a separate audit. Committing the database first ensures any uncertainty a crash leaves behind surfaces as a referenced, indeterminate file that cleanup can confirm, rather than a silently orphaned one.

> [!IMPORTANT]
> The canonical pattern is therefore: call <xref:FulcrumFS.FileRepoTransaction.AddAsync*> to obtain the <xref:FulcrumFS.FileId>, write it onto the database row, commit the database, and only then commit the repository transaction. Code that commits the repository first will *appear* to work in tests but is the unsafe configuration.

## What Is in the Transactional Path

Not everything in a repository is transactional. The split is deliberate, and understanding it makes the rest of the API shape obvious.

#### Main files are transactional

A main file's <xref:FulcrumFS.FileId> is the unit that ties the repository to the database. Main files can only be added with <xref:FulcrumFS.FileRepoTransaction.AddAsync*> and deleted with <xref:FulcrumFS.FileRepoTransaction.DeleteAsync*>, both of which sit on <xref:FulcrumFS.FileRepoTransaction>. The commit ordering rule above applies to them precisely because they are what the database references.

Main files are also treated as **immutable** once added. There is no "replace" operation. To update the content a database row points at, add a new main file in the same transaction that deletes the old one and overwrite the row's <xref:FulcrumFS.FileId> column with the new one. The result is a clean swap with full transactional guarantees, and the old file is reclaimed by cleanup.

#### Variants are not transactional

A variant is auxiliary content attached to a main file, addressed by a string ID within the file group. The database does not typically reference variants directly (it only knows the main file's <xref:FulcrumFS.FileId>), so a variant add does not need to be coordinated with a database commit. That is why the variant add APIs sit on <xref:FulcrumFS.FileRepo> directly rather than on the transaction: each one commits on its own.

Variant lifetimes are bound to the main file, not the transaction:

- When a main file is deleted, all of its variants are deleted with it.
- Variants added through <xref:FulcrumFS.FileProcessingPipeline.WithVariant*> *during* an add transaction are part of that add and get rolled back along with the main file if the transaction does not commit.
- Variants added later through the variant add APIs are committed individually as they are written.

Like main files, variants are immutable once written. To swap a variant's content, give the new content a new variant ID rather than dropping and re-adding the same one. Content that needs to change repeatedly does not belong in a variant; it belongs in its own main file with its own transactional lifecycle. See [File Variants](../guides/file-variants.md) for the practical API.

## The Indeterminate State

Commit and rollback are not guaranteed to be instantaneous or infallible against an unreliable file system: a transient `IOException` from antivirus interference, a network share dropping mid-write, a full disk encountered partway through. Rather than throw and leave a caller guessing whether the file is now visible or not, FulcrumFS marks affected files as *indeterminate* when a commit or rollback fails, and keeps them accessible.

Failures are surfaced through the <xref:FulcrumFS.FileRepo.CommitFailed> and <xref:FulcrumFS.FileRepo.RollbackFailed> events rather than thrown automatically, because the files involved are still readable; they are simply flagged for later resolution. A handler can log, alert, or choose to throw if it wants throwing behavior.

Indeterminate files are resolved during cleanup. <xref:FulcrumFS.FileRepo.CleanAsync*> accepts a callback that decides, per <xref:FulcrumFS.FileId>, whether to <xref:FulcrumFS.IndeterminateResolution.Keep> or <xref:FulcrumFS.IndeterminateResolution.Delete> each indeterminate file. This is where a process reconciles the repository against the source of truth (typically the owning database) after a crash.

> [!TIP]
> The indeterminate state is not a failure mode you need to handle in every request. It is a rare backstop for the worst case. In practice, the resolution callback is the same query the application would use to check whether a row exists, and the cleaner runs it for the small number of indeterminate files cleanup encounters.

## Orphan Reclamation

Because the commit ordering rule can intentionally leave orphans, the repository expects a cleanup pass to run periodically. <xref:FulcrumFS.FileRepo.CleanAsync*> removes deferred deletions whose grace period has elapsed, sweeps stray markers, and resolves indeterminate files via the supplied callback. See [Repository Cleanup](../guides/cleanup.md) for how to schedule and configure it.

## Further Reading

- [Adding and Committing Files](../guides/adding-files.md) - The practical add and rollback API.
- [Crash Recovery and Invariants](crash-recovery.md) - What states a crash can leave and how they converge.
- [Repository Cleanup](../guides/cleanup.md) - Running the cleaner and resolving indeterminate files.

</div>
