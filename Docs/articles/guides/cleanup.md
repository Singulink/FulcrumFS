<div class="article">

# Repository Cleanup

Cleanup is the background maintenance pass that reclaims deferred deletions, sweeps stray markers, completes interrupted operations, and resolves files left in an indeterminate state. This guide covers running and scheduling the cleaner.

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

## Scheduling

There is no built-in scheduler; you decide cadence. A common arrangement is a hosted background service that calls <xref:FulcrumFS.FileRepo.CleanAsync*> on an interval (for example hourly), passing a resolution callback that consults the database:

```csharp
public class CleanupHostedService(FileRepo repo, IDbContextFactory<AppDbContext> dbFactory)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

            await repo.CleanAsync(
                TimeSpan.FromHours(24),
                async fileId => await db.Documents.AnyAsync(d => d.FileId == fileId, stoppingToken)
                    ? IndeterminateResolution.Keep
                    : IndeterminateResolution.Delete);
        }
    }
}
```

> [!TIP]
> Run cleanup after scheduled backups complete so backups capture deleted content within its grace window before the cleaner reclaims it. A nightly backup at 02:00 followed by a cleanup pass at 03:00 is a typical pattern.

## Next Steps

- [Deleting Files and Variants](deleting-files.md) - What cleanup reclaims.
- [Transactional Commit Model](../concepts/commit-model.md) - The indeterminate state.
- [Crash Recovery and Invariants](../concepts/crash-recovery.md) - What the cleaner completes after a crash.

</div>
