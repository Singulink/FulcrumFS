<div class="article">

# Repository Cleanup

Cleanup is the background maintenance pass that reclaims deferred deletions, sweeps stray markers, completes interrupted operations, and resolves files left in an indeterminate state. This guide covers running and scheduling the cleaner, and auditing a repository against the application's database.

### Why cleanup is required

FulcrumFS intentionally defers physical deletions and tolerates orphaned files from the commit ordering rule, so a repository relies on a periodic cleaner to reclaim space and finish recovery work. Without it, deleted files would accumulate on disk indefinitely, and any file orphaned by a crash between the database and repository commits would never be reclaimed.

> [!NOTE]
> The cleaner is safe to run as often as you like because every step is idempotent and forward-only. Overlapping runs, extra runs, and runs against a quiescent repository are all harmless. See [Crash Recovery and Invariants](../concepts/crash-recovery.md).

## Running a Clean Pass

Call <xref:FulcrumFS.FileRepo.CleanAsync*> with a delete delay. Deferred deletions whose markers are older than the delay are physically removed; newer ones are left alone so concurrent readers and backups still have their grace window.

```csharp
await repo.CleanAsync(deleteDelay: TimeSpan.FromHours(24));
```

A larger delay keeps deleted content recoverable and backup-consistent for longer at the cost of disk usage; a smaller delay reclaims space sooner. A 24 hour delay is a reasonable default for systems with daily backups.

A pass does not stop at the first problem. A file that cannot be deleted, for example because another process has it open, is logged to its delete marker and retried by the next pass. An unexpected I/O failure is collected instead: the pass processes everything else, then throws it as an <xref:System.IO.IOException> (or an <xref:System.AggregateException> if there were several), and the affected files are left for the next pass.

#### Delete journals

A pass also applies delete journals, which [staged transactions](../concepts/commit-model.md#staged-transactions), and ordinary transactions that commit two or more deletions, write on a repository of format version 2 or later. A committed journal is the delete marker of every file it lists, and the delete delay applies to it the same way: once it is older than the delay, its files are deleted and the journal is removed. A journal left staged by a transaction that did not finish is resolved file by file through the resolution callback described below, like an indeterminate file. [Delete Journals](../concepts/crash-recovery.md#delete-journals) describes every state a journal can be in.

> [!WARNING]
> A committed journal that fails validation means the storage it is on was damaged or something outside the library changed it. The cleaner then deletes none of the files it lists and leaves the journal in place, finishes the rest of the pass, and throws an <xref:System.IO.IOException> that names the journal. The exception repeats on every pass until the journal is dealt with: check the storage, then restore the journal from a backup, or remove it and reclaim the files it listed with an audit (see below).

## Resolving Indeterminate Files

When a commit or rollback fails (typically because of a transient file system error), affected files are marked indeterminate and kept accessible so a reader holding the <xref:FulcrumFS.FileId> can still serve them. Resolution decides whether each one should ultimately survive or be reclaimed; that decision belongs to the application, because only the application knows whether the owning database row still exists.

Pass a callback to <xref:FulcrumFS.FileRepo.CleanAsync*> that decides, per <xref:FulcrumFS.FileId>, what to do. Both synchronous and asynchronous callback overloads are available.

```csharp
await repo.CleanAsync(
    TimeSpan.FromHours(24),
    async fileId =>
    {
        // The database is the source of truth: if a row references this
        // file, keep it; otherwise reclaim the bytes.
        bool referenced = await dbContext.Documents.AnyAsync(d => d.FileId == fileId);
        return referenced ? IndeterminateResolution.Keep : IndeterminateResolution.Delete;
    });
```

> [!IMPORTANT]
> The resolution callback must consult the same database (or other source of truth) that owns the file references. Returning <xref:FulcrumFS.IndeterminateResolution.Delete> for a file that a live database row references would orphan the row's reference. When in doubt, return <xref:FulcrumFS.IndeterminateResolution.Keep>; an extra orphan file costs disk but never breaks correctness.

## Cleaning Without a Repository Instance

Cleanup does not require a live <xref:FulcrumFS.FileRepo>. The <xref:FulcrumFS.FileRepoCleaner> type runs a clean pass against a base directory on its own, which suits a separate maintenance process, a scheduled job, or a recovery tool that operates on an offline copy of the repository.

```csharp
var cleaner = new FileRepoCleaner(baseDirectory);
await cleaner.CleanAsync(TimeSpan.FromHours(24));
```

<xref:FulcrumFS.FileRepoCleaner> implements <xref:FulcrumFS.IFileRepoCleaner> and exposes the same <xref:FulcrumFS.FileRepoCleaner.CleanAsync*> overloads, including the indeterminate-resolution callbacks. Configure it with <xref:FulcrumFS.FileRepoCleaningOptions> when defaults are not enough.

The cleaner never changes a repository's [format version](../concepts/repository-layout.md#format-version). It cleans every repository whose format it can clean, and refuses one that needs a newer version of FulcrumFS with a <xref:System.NotSupportedException> without touching it. A cleaner deployed separately from the application therefore has to be updated before the application upgrades the repositories it cleans.

## Scheduling

There is no built-in scheduler; you decide cadence. A common arrangement is a hosted background service that calls <xref:FulcrumFS.FileRepo.CleanAsync*> on an interval (for example hourly), passing a resolution callback that consults the database:

```csharp
public class CleanupHostedService(
    FileRepo repo, IDbContextFactory<AppDbContext> dbFactory, ILogger<CleanupHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

                await repo.CleanAsync(
                    TimeSpan.FromHours(24),
                    async fileId => await db.Documents.AnyAsync(d => d.FileId == fileId, stoppingToken)
                        ? IndeterminateResolution.Keep
                        : IndeterminateResolution.Delete,
                    stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Whatever the pass could not finish is left for the next one.
                logger.LogError(ex, "Repository cleanup pass failed.");
            }
        }
    }
}
```

The call is wrapped in a try/catch because a pass that hits an I/O failure throws once it has finished (see [Running a Clean Pass](#running-a-clean-pass)), and an exception that escapes the loop ends the service, so one failed pass would stop all further cleanup; logging it and carrying on lets the next pass retry what was left.

> [!TIP]
> Run cleanup after scheduled backups complete so backups capture deleted content within its grace window before the cleaner reclaims it. A nightly backup at 02:00 followed by a cleanup pass at 03:00 is a typical pattern.

## Auditing a Repository and Recovering After a Database Restore

<xref:FulcrumFS.FileRepo.EnumerateFilesAsync*> lists every file group in the repository as a <xref:FulcrumFS.RepoFileEntry>. It exists for two rare situations that the cleaner cannot handle on its own:

- **Recovering after a database restore.** When the application database is restored to an earlier point in time, files added after that point are still in the repository but no row references them. Their transactions committed normally, so they are not indeterminate and the cleaner has no reason to look at them.
- **Auditing.** An operator can check that the repository and the application agree: that every file in the repository is referenced by the application, and (by querying in the other direction) that every file the application references exists.

Each entry carries the <xref:FulcrumFS.RepoFileEntry.FileId>, the <xref:FulcrumFS.RepoFileEntry.CreationTimeUtc> of its file group, and two flags. <xref:FulcrumFS.RepoFileEntry.IsPendingDeletion> means the file has already been deleted and is waiting for a cleanup pass. <xref:FulcrumFS.RepoFileEntry.IsIndeterminate> means a transaction that adds or deletes it is still open or did not complete, which the resolution callback described above takes care of. A recovery sweep leaves both kinds to the cleaner and deletes the remaining files that no row references:

```csharp
// After restoring the database to an earlier point, delete the files that no row references.
var cutoff = DateTime.UtcNow.AddHours(-1);

await foreach (var entry in repo.EnumerateFilesAsync(cancellationToken))
{
    // Leave deleted and indeterminate files to the cleaner, and skip recent files
    // whose database rows may not be committed yet.
    if (entry.IsPendingDeletion || entry.IsIndeterminate || entry.CreationTimeUtc > cutoff)
        continue;

    if (!await dbContext.Documents.AnyAsync(d => d.FileId == entry.FileId, cancellationToken))
    {
        await using var txn = await repo.BeginTransactionAsync();
        await txn.TryDeleteAsync(entry.FileId, cancellationToken);
        await txn.CommitAsync(cancellationToken);
    }
}
```

An audit runs the same loop and reports the unreferenced files instead of deleting them.

The enumeration takes no locks and does not block other repository operations. The cleanup state behind the two flags is read once when it starts, files added or deleted while it runs may or may not appear, and the order of the results is unspecified. Only well-formed file group directories are reported; anything else under the files directory is skipped.

> [!CAUTION]
> Enumerating a repository reads every one of its shard directories (up to 65,536), and a sweep like the one above also queries the database once per file. On a large repository that is a long, I/O-heavy operation. Run it deliberately when you need it, not on a schedule: routine reclamation is the cleaner's job.

## Next Steps

- [Deleting Files and Variants](deleting-files.md) - What cleanup reclaims.
- [Transactional Commit Model](../concepts/commit-model.md) - The indeterminate state.
- [Crash Recovery and Invariants](../concepts/crash-recovery.md) - What the cleaner completes after a crash.

</div>
