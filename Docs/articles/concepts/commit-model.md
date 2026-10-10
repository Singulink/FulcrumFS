<div class="article">

# Transactional Commit Model

FulcrumFS gives file storage database-aligned commit semantics without requiring distributed transactions. This article explains the transaction lifecycle, the commit ordering rule that coordinates a repository with a database, and the indeterminate state that makes crash recovery safe.

### The core idea

A file almost always belongs to a row in a database: a `Photos.FileId`, a `Documents.FileId`, an attachment id on a forum post. The hard problem is keeping the file and the row consistent when either store can fail independently. The classical answer is a two-phase commit coordinator, but those bring their own indeterminate post-crash states (an in-doubt transaction can block a database from reopening) and require the database to participate.

FulcrumFS solves the problem differently: a strict commit ordering plus a cleanup pass that reclaims orphans. The ordering pushes any uncertainty a crash leaves behind into a shape the cleaner can resolve safely, with no coordinator and no special database support.

## Guarantees

When your application follows the commit ordering rule (commit the database first, then the repository), FulcrumFS guarantees two things, even across process crashes, power loss and unreliable file systems:

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

## Staged Transactions

An ordinary transaction writes a marker for every file it deletes, flushes it to disk and holds it open until the transaction commits. (When it commits two or more deletions in a deferred delete mode on a repository of [format version](repository-layout.md#format-version) 2 or later, it records them in one delete journal, but by then it has already written, flushed and held a marker per file.) That suits the usual handful of files, but an application transaction that deletes thousands of files (closing an account, removing a large album) would hold that many file handles and pay that many flushes while its database transaction is open. A staged transaction, created with <xref:FulcrumFS.FileRepo.BeginStagedTransactionAsync*> and represented by <xref:FulcrumFS.FileRepoStagedTransaction>, records its deletions in memory instead and writes them all to a single journal file, flushed once and held open for the whole set. On a repository of [format version](repository-layout.md#format-version) 1, which has no journals, staging writes, flushes and holds a marker per deleted file instead, as an ordinary transaction does: the behavior is identical, and only the cost of staging grows with the number of files again.

A staged transaction commits in three calls instead of one:

1. <xref:FulcrumFS.FileRepoStagedTransaction.StageAsync*> writes the journal, flushes it to disk and returns a <xref:FulcrumFS.FileRepoStage>.
2. The application commits its database.
3. <xref:FulcrumFS.FileRepoStage.CommitAsync*> commits the stage, which commits the transaction.

```csharp
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

The [commit ordering rule](#the-commit-ordering-rule) is the same as for an ordinary transaction: the database commits first and the repository second. Staging only prepares the repository commit, so it comes before the database commit, and a failure to write the journal surfaces from <xref:FulcrumFS.FileRepoStagedTransaction.StageAsync*> while the database transaction can still be rolled back. A staged transaction has no commit method of its own; only the stage can commit it, so committing without staging cannot be written by mistake. When there is no database step between the two, <xref:FulcrumFS.FileRepoStagedTransaction.StageAndCommitAsync*> does both in one call.

Committing the stage renames the journal from staged to committed, a single atomic operation however many files it lists, and flushes the rename to disk. If that step fails, <xref:FulcrumFS.FileRepoStage.CommitAsync*> throws, no file has been committed or deleted and the call can be retried. Everything after it behaves like an ordinary commit: failures of individual files are reported through <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> and left for the cleaner. Once <xref:FulcrumFS.FileRepoStage.CommitAsync*> has started the commit the transaction is never rolled back, because the database has committed by then: if the commit failed and is not retried, disposing the transaction leaves its files for the cleaner to resolve (see [What a Failed Commit Means](#what-a-failed-commit-means)).

Files added in a staged transaction are tracked per file exactly as in an ordinary transaction and are committed or rolled back together with the deletions. Disposing the transaction before its stage commits rolls everything back. If the process ends between staging and committing, the cleaner treats the files listed in the journal like indeterminate files and resolves them through the resolution callback. See [Crash Recovery and Invariants](crash-recovery.md#delete-journals) for each state a journal can be in.

> [!NOTE]
> Other operations do not consult journals, except <xref:FulcrumFS.FileRepo.EnumerateFilesAsync*>: it reports a file whose deletion is staged as indeterminate, and a file listed in a committed journal as pending deletion. Another transaction can still delete such a file, as it can any file pending deletion, and the two deletions converge. Like any deleted main file, it stays readable until the cleaner removes it.

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

Failures are surfaced through the single <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> event (carrying a <xref:FulcrumFS.RepoTransactionCompletionFailureInfo> that identifies the failed <xref:FulcrumFS.RepoTransactionCompletionOperation> and the underlying exception) rather than thrown automatically, because the files involved are still readable; they are simply flagged for later resolution. A handler can log, alert, or choose to throw if it wants throwing behavior.

Indeterminate files are resolved during cleanup. <xref:FulcrumFS.FileRepo.CleanAsync*> accepts a callback that decides, per <xref:FulcrumFS.FileId>, whether to <xref:FulcrumFS.IndeterminateResolution.Keep> or <xref:FulcrumFS.IndeterminateResolution.Delete> each indeterminate file. This is where a process reconciles the repository against the source of truth (typically the owning database) after a crash.

> [!TIP]
> The indeterminate state is not a failure mode you need to handle in every request. It is a rare backstop for the worst case. In practice, the resolution callback is the same query the application would use to check whether a row exists, and the cleaner runs it for the small number of indeterminate files cleanup encounters.

## What a Failed Commit Means

The repository commits after the database has, so the application has to know what an exception from the repository commit says about its files. Unless the call was cancelled, it does not say that anything was rolled back:

| The commit call | What has happened to the files | What disposing the transaction then does |
| --- | --- | --- |
| returns | Each file is committed, or its commit failed: then it is indeterminate, still readable, and the failure was reported through <xref:FulcrumFS.FileRepo.TransactionCompletionFailed>. | Nothing. |
| throws what a <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> handler threw | The same. The event is raised once every file has been attempted, so the exception arrives after the fact. | Nothing. |
| throws <xref:System.ObjectDisposedException> because the repository was disposed | Whatever was committed before the repository was disposed stays committed. Every other file is left indeterminate. | Nothing: those files stay indeterminate. |
| is <xref:FulcrumFS.FileRepoStage.CommitAsync*?displayProperty=nameWithType> and throws because the journal could not be committed | No added file is committed and no file is deleted. The journal is still staged, or already committed if only the flush of its rename failed. The call can be retried. | Leaves the files to the cleaner: it resolves them through the resolution callback, or deletes the listed files without asking if the journal is committed. |
| throws <xref:System.OperationCanceledException> | Nothing: the commit did not start, and the transaction is as it was. | Rolls the transaction back. |

Three things follow for an application:

- **An exception from the commit is not a failed operation.** After the database commit, an added file is in the repository and a deleted file is gone or left for the cleaner, whatever the repository commit throws, with the one exception below. Log it; the next cleanup pass resolves what is indeterminate through the resolution callback, which keeps a file the database references and deletes one it does not.
- **Do not let the commit be cancelled.** A cancellation is the one case in which nothing was committed and disposing the transaction rolls it back, which after the database commit would discard files the database references, or the journal of deletions it has committed. Pass <xref:System.Threading.CancellationToken.None?displayProperty=nameWithType> to <xref:FulcrumFS.FileRepoTransaction.CommitAsync*?displayProperty=nameWithType> and <xref:FulcrumFS.FileRepoStage.CommitAsync*?displayProperty=nameWithType>. A <xref:FulcrumFS.FileRepo> observes the token only while the call waits for another call on the same transaction, and never once the commit has started.
- **A handler that throws does not undo anything.** It is the way to see a failure to commit a file as an exception at the call site, and what the caller must then assume is exactly what the event says: the affected files are indeterminate.

<xref:FulcrumFS.FileRepoStagedTransaction.StageAsync*> is different, because it comes before the database commit: when it throws, nothing has been staged, the database must not commit, and disposing the transaction rolls it back.

> [!NOTE]
> A <xref:FulcrumFS.FileRepo> leaves one failure unreported: if the indeterminate marker of an added file cannot be removed, which is what commits the add, the file is left indeterminate and the failure is only logged to the marker. In <xref:FulcrumFS.DeleteMode.Immediate> mode, a file that cannot be deleted at once is not reported, because it is not a failure: its deletion is committed all the same, with a delete marker that a cleanup pass carries out.

## Orphan Reclamation

Because the commit ordering rule can intentionally leave orphans, the repository expects a cleanup pass to run periodically. <xref:FulcrumFS.FileRepo.CleanAsync*> removes deferred deletions whose grace period has elapsed, sweeps stray markers, and resolves indeterminate files via the supplied callback. See [Repository Cleanup](../guides/cleanup.md) for how to schedule and configure it.

## Further Reading

- [Adding and Committing Files](../guides/adding-files.md) - The practical add and rollback API.
- [Crash Recovery and Invariants](crash-recovery.md) - What states a crash can leave and how they converge.
- [Repository Cleanup](../guides/cleanup.md) - Running the cleaner and resolving indeterminate files.

</div>
