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

A transaction ends with <xref:FulcrumFS.FileRepoTransaction.CommitAsync*> or with <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*>, which discards the tentatively added files and abandons the pending deletions. Disposing a transaction that was neither committed nor rolled back does not roll it back: the files it added and the files it deleted are left [indeterminate](#the-indeterminate-state) for the cleaner to resolve, because the transaction cannot know whether the database committed before it was disposed. See [A Database Commit with an Unknown Outcome](#a-database-commit-with-an-unknown-outcome).

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

Committing the stage renames the journal from staged to committed, a single atomic operation however many files it lists, and flushes the rename to disk. If that step fails, <xref:FulcrumFS.FileRepoStage.CommitAsync*> throws, no file has been committed or deleted and the call can be retried. After it, the files the transaction added are committed as in an ordinary commit: a failure to commit one is reported through <xref:FulcrumFS.FileRepo.TransactionCompletionFailed>, and the file is left for the cleaner. The deletions are committed by that step, so none of them is reported afterwards: a file that cannot be deleted at once stays recorded in the journal, and a cleanup pass deletes it. Once <xref:FulcrumFS.FileRepoStage.CommitAsync*> has started the commit the transaction is never rolled back, because the database has committed by then: if the commit failed and is not retried, disposing the transaction leaves its files for the cleaner to resolve (see [What a Failed Commit Means](#what-a-failed-commit-means)).

Files added in a staged transaction are tracked per file exactly as in an ordinary transaction and are committed or rolled back together with the deletions. <xref:FulcrumFS.FileRepoStagedTransaction.RollbackAsync*> rolls everything back, before or after staging. If the process ends between staging and committing, the cleaner treats the files listed in the journal like indeterminate files and resolves them through the resolution callback. Disposing the transaction before its stage commits leaves the same state and rolls nothing back: the staged journal and the markers of the added files stay in place with nobody holding them (see [A Database Commit with an Unknown Outcome](#a-database-commit-with-an-unknown-outcome)). A transaction that is disposed before it was staged has recorded its deletions only in memory, so nothing happens to those files and they are not indeterminate; the files it added are. See [Crash Recovery and Invariants](crash-recovery.md#delete-journals) for each state a journal can be in.

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
- Variants added through <xref:FulcrumFS.FileProcessingPipeline.WithVariant*> *during* an add transaction are part of that add and share the fate of the main file: they are committed with it, and discarded with it if the transaction is rolled back.
- Variants added later through the variant add APIs are committed individually as they are written.

Like main files, variants are immutable once written. To swap a variant's content, give the new content a new variant ID rather than dropping and re-adding the same one. Content that needs to change repeatedly does not belong in a variant; it belongs in its own main file with its own transactional lifecycle. See [File Variants](../guides/file-variants.md) for the practical API.

## The Indeterminate State

Commit and rollback are not guaranteed to be instantaneous or infallible against an unreliable file system: a transient `IOException` from antivirus interference, a network share dropping mid-write, a full disk encountered partway through. Rather than throw and leave a caller guessing whether the file is now visible or not, FulcrumFS marks affected files as *indeterminate* when a commit or rollback fails, and keeps them accessible. The files of a transaction that ended without a commit or a rollback are in the same state: after a crash, and after the transaction was disposed without either (see [A Database Commit with an Unknown Outcome](#a-database-commit-with-an-unknown-outcome)).

Failures are surfaced through the single <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> event (carrying a <xref:FulcrumFS.RepoTransactionCompletionFailureInfo> that identifies the failed <xref:FulcrumFS.RepoTransactionCompletionOperation> and the underlying exception) rather than thrown automatically, because the files involved are still readable; they are simply flagged for later resolution. A handler can log, alert, or choose to throw if it wants throwing behavior.

Indeterminate files are resolved during cleanup. <xref:FulcrumFS.FileRepo.CleanAsync*> accepts a callback that decides, per <xref:FulcrumFS.FileId>, whether to <xref:FulcrumFS.IndeterminateResolution.Keep> or <xref:FulcrumFS.IndeterminateResolution.Delete> each indeterminate file. This is where a process reconciles the repository against the source of truth (typically the owning database) after a crash.

> [!TIP]
> The indeterminate state is not a failure mode you need to handle in every request. It is a rare backstop for the worst case. In practice, the resolution callback is the same query the application would use to check whether a row exists, and the cleaner runs it for the small number of indeterminate files cleanup encounters.

## A Database Commit with an Unknown Outcome

The ordering rule covers a database commit that succeeds (the repository commits next) and a process that ends between the two commits. There is a third case: the database commit throws, and the application does not know whether the database committed. That happens when the connection is lost after the server has committed and before its answer arrives, when a command timeout expires at that moment, or when a fail-over interrupts the call. The exception skips the repository commit, and the `await using` block disposes the repository transaction:

```csharp
await using var txn = await repo.BeginTransactionAsync();
await using var dbTxn = await dbContext.Database.BeginTransactionAsync();

var added = await txn.AddAsync(stream, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty);

dbContext.Photos.Add(new Photo { FileId = added.FileId });
await dbContext.SaveChangesAsync();
await dbTxn.CommitAsync();   // The server commits, the connection drops, and this throws...

await txn.CommitAsync();     // ...so this never runs, and txn is disposed without a commit.
```

The repository cannot tell this from a database commit that did not happen, so disposal does not guess. **Disposing a transaction that was neither committed nor rolled back rolls nothing back.** It releases what the transaction holds and leaves every file the transaction added, and every file it deleted, indeterminate, which is exactly the state that a process which ended at that point leaves. The added file stays readable through the <xref:FulcrumFS.FileId> that the committed row holds, and a file the transaction deleted stays in place with its deletion still on record. Rolling back instead would delete a file that a committed row references, and abandon a deletion that the database has committed.

What follows is what follows a crash:

- **The next cleanup pass with a resolution callback resolves the files.** It asks the callback about each of them, keeps the ones the database references and deletes the others. If the database did not commit after all, the files the transaction added are removed then, and the files it deleted are kept. If it did, the added files become ordinary committed files and the deletions are carried out.
- **A pass without a callback leaves them alone**, as it leaves every indeterminate file. A repository that never runs cleanup with a callback therefore keeps the files of every transaction that was disposed without a commit: that costs disk space, but never a file that a row references. <xref:FulcrumFS.FileRepo.EnumerateFilesAsync*> reports such files as indeterminate.
- **An application that knows that its database did not commit can discard the files at once** by calling <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*> before the transaction is disposed. It knows that when the failure came before the database commit was attempted: a rejected upload, a validation error, a constraint violation from saving the changes.

```csharp
try
{
    dbContext.Photos.Add(new Photo { FileId = added.FileId });
    await dbContext.SaveChangesAsync();
}
catch (DbUpdateException)
{
    // The database commit has not been attempted, so no row can reference the file.
    await txn.RollbackAsync();
    throw;
}

await dbTxn.CommitAsync();   // Not inside the try: an exception from here says nothing about the outcome.
await txn.CommitAsync();
```

> [!IMPORTANT]
> Never call <xref:FulcrumFS.FileRepoTransaction.RollbackAsync*> in a handler that an exception from the database commit itself can reach. A rollback discards the added files and abandons the deletions immediately, which is only safe when the database certainly did not commit. When in doubt, let the transaction be disposed: the worst that leaves is a file for the next cleanup pass to remove.

A staged transaction behaves the same way. Disposed after staging, it leaves its staged journal and the files it added for the cleaner; <xref:FulcrumFS.FileRepoStagedTransaction.RollbackAsync*> discards them at once.

## What a Failed Commit Means

The repository commits after the database has, so the application has to know what an exception from the repository commit says about its files. It never says that anything was rolled back:

| The commit call | What has happened to the files | What disposing the transaction then does |
| --- | --- | --- |
| returns | Each file is committed, or its commit failed: then it is indeterminate, still readable, and the failure was reported through <xref:FulcrumFS.FileRepo.TransactionCompletionFailed>. A deletion that a committed [delete journal](crash-recovery.md#delete-journals) records is committed and is never reported, whatever went wrong with it afterwards: the cleaner carries it out. | Nothing. |
| throws what a <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> handler threw | The same. The event is raised once every file has been attempted, so the exception arrives after the fact. | Nothing. |
| throws <xref:System.ObjectDisposedException> because the repository was disposed | Whatever was committed before the repository was disposed stays committed. Every other file is left indeterminate. | Nothing: those files stay indeterminate. |
| is <xref:FulcrumFS.FileRepoStage.CommitAsync*?displayProperty=nameWithType> and throws because the journal could not be committed | No added file is committed and no file is deleted. The journal is still staged, or already committed if only the flush of its rename failed. The call can be retried. | Leaves the files to the cleaner: it resolves them through the resolution callback, or deletes the listed files without asking if the journal is committed. |
| throws <xref:System.OperationCanceledException> | Nothing: the commit did not start, and the transaction is as it was. The commit can be called again. | Leaves every file of the transaction indeterminate, as for any transaction that was neither committed nor rolled back: the cleaner resolves them through the resolution callback. |

Three things follow for an application:

- **An exception from the commit is not a failed operation.** After the database commit, an added file is in the repository and a deleted file is gone or left for the cleaner, whatever the repository commit throws. Log it; the next cleanup pass resolves what is indeterminate through the resolution callback, which keeps a file the database references and deletes one it does not.
- **Do not let the commit be cancelled.** A cancellation is the one case in which nothing was committed at all: the transaction is still open, and disposing it leaves every one of its files indeterminate. That loses nothing, but it leaves to the next cleanup pass with a resolution callback what the commit would have finished at once, including the deletions the database has committed. Pass <xref:System.Threading.CancellationToken.None?displayProperty=nameWithType> to <xref:FulcrumFS.FileRepoTransaction.CommitAsync*?displayProperty=nameWithType> and <xref:FulcrumFS.FileRepoStage.CommitAsync*?displayProperty=nameWithType>. A <xref:FulcrumFS.FileRepo> observes the token only while the call waits for another call on the same transaction, and never once the commit has started.
- **A handler that throws does not undo anything.** It is the way to see a failure to commit a file as an exception at the call site, and what the caller must then assume is exactly what the event says: the affected files are indeterminate.

Committing a transaction that has itself already been disposed is a mistake of the caller rather than a failed commit: it throws <xref:System.ObjectDisposedException> too, the call commits nothing, and a transaction that was disposed without having been committed or rolled back left its files indeterminate when it was disposed.

<xref:FulcrumFS.FileRepoStagedTransaction.StageAsync*> is different, because it comes before the database commit: when it throws, nothing has been staged and the database must not commit. The database commit is certainly not attempted then, so the application can call <xref:FulcrumFS.FileRepoStagedTransaction.RollbackAsync*> to discard the files the transaction added at once. Disposing the transaction without that leaves those files indeterminate for the cleaner and records nothing for its deletions, which were never staged.

> [!NOTE]
> A <xref:FulcrumFS.FileRepo> tolerates three things without reporting them, because none of them is a failure for the application. If the indeterminate marker of an added file cannot be removed when the add commits, because something else has the marker open, the file is accessible and stays indeterminate until the next cleanup pass resolves it through the resolution callback and removes the marker; the attempt is logged to the marker. In <xref:FulcrumFS.DeleteMode.Immediate> mode, a file that cannot be deleted at once has its deletion committed all the same, with a delete marker that a cleanup pass carries out. And once a [delete journal](crash-recovery.md#delete-journals) is committed, the deletions it records are committed, so nothing that goes wrong with them afterwards is reported, including a repository that cannot be accessed at that moment: a file that could not be deleted, and a marker or the journal itself that could not be removed, are left to the cleaner, which deletes the files a committed journal lists without asking. That covers every deletion of a staged transaction once its stage has committed the journal, and the deletions of an ordinary transaction that records them in a journal when it commits.

## Orphan Reclamation

Because the commit ordering rule can intentionally leave orphans, and a transaction that is disposed without a commit leaves its files for the cleaner rather than guess whether the database committed, the repository expects a cleanup pass with a resolution callback to run periodically. <xref:FulcrumFS.FileRepo.CleanAsync*> removes deferred deletions whose grace period has elapsed, sweeps stray markers, and resolves indeterminate files via the supplied callback. See [Repository Cleanup](../guides/cleanup.md) for how to schedule and configure it.

## Further Reading

- [Adding and Committing Files](../guides/adding-files.md) - The practical add and rollback API.
- [Crash Recovery and Invariants](crash-recovery.md) - What states a crash can leave and how they converge.
- [Repository Cleanup](../guides/cleanup.md) - Running the cleaner and resolving indeterminate files.

</div>
