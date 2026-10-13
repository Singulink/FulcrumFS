using System.Diagnostics;
using System.Globalization;

namespace FulcrumFS;

/// <summary>
/// Pins that a commit, a rollback and the staging of a version 1 repository wait for access to the repository for
/// <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> once, shared by all of their files, instead of once per file: on a volume that has stopped
/// answering they fail after one wait and leave their files for the cleaner, they still ride out an outage shorter than the wait, and the time their work
/// takes does not count. Also pins what a commit on such a volume reports: the files it could not commit, but no deletion that a committed delete journal
/// records, which the cleaner makes without asking.
/// </summary>
/// <remarks>
/// The volume stops answering as the repository sees it when that happens while it is idle (see <see cref="SimulatedVolume"/>): its health checks have
/// stopped, the next access probes the volume inline and the probe fails, and initializing the repository again fails, because its info file is gone, until
/// the volume answers again.
/// </remarks>
[PrefixTestClass]
public sealed class AccessWaitBudgetTests
{
    // The files of each transaction. A call that waits once returns well before twice the wait; one that waits once per file takes at least this many
    // times the wait.
    private const int FileCount = 4;

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    // The repository's MaxAccessWaitOrRetryTime, which a commit or a rollback waits for once for all of its files, and its HealthCheckInterval: both the
    // shortest the options allow.
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(1);

    // How long the commit of each file takes in the tests whose work takes longer than the wait.
    private static readonly TimeSpan _slowFileWork = TimeSpan.FromMilliseconds(300);

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // ---------------------------------------------------------------------------------------------------------------
    // A volume that has stopped answering
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task Commit_OfDeletions_OnAVolumeThatStoppedAnswering_FailsAfterOneWait_AndTheCleanerFinishesThem(DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, FileCount);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                await volume.StopAnsweringOnceIdleAsync();

                // The commit first tries to record the deletions in a journal, which needs access (immediate mode needs it too, before it finds that it
                // has no journal), and then commits each deletion on its own.
                ShouldHaveWaitedOnce(await TimeAsync(() => txn.CommitAsync(TestContext.CancellationToken)));
            }

            ShouldHaveReportedTimeouts(failures, RepoTransactionCompletionOperation.Commit, FileCount + 1);

            // No deletion has happened or is recorded: each file is indeterminate, with a marker that nobody holds.
            foreach (var fileId in fileIds)
                ShouldBeIndeterminate(repoDir, fileId);

            volume.AnswerAgain();

            // The application's database has committed the deletions, so its answer to the cleaner is to delete the files.
            (await CleanAnsweringAsync(repo, _ => IndeterminateResolution.Delete)).ShouldBe(fileIds, ignoreOrder: true);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Commit_OfAdds_OnAVolumeThatStoppedAnswering_FailsAfterOneWait_AndLeavesTheFilesForTheCleaner()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var fileIds = new FileId[FileCount];

            await using (var txn = await repo.BeginTransactionAsync())
            {
                for (int i = 0; i < FileCount; i++)
                    fileIds[i] = await AddSampleFileAsync(txn);

                await volume.StopAnsweringOnceIdleAsync();
                ShouldHaveWaitedOnce(await TimeAsync(() => txn.CommitAsync(TestContext.CancellationToken)));
            }

            ShouldHaveReportedTimeouts(failures, RepoTransactionCompletionOperation.Commit, FileCount);

            foreach (var fileId in fileIds)
                ShouldBeIndeterminate(repoDir, fileId);

            volume.AnswerAgain();

            // The application's database has committed the files, so its answer to the cleaner is to keep them.
            (await CleanAnsweringAsync(repo, _ => IndeterminateResolution.Keep)).ShouldBe(fileIds, ignoreOrder: true);

            foreach (var fileId in fileIds)
                ShouldBeCommitted(repoDir, fileId);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Rollback_OnAVolumeThatStoppedAnswering_FailsAfterOneWait_AndLeavesTheFilesForTheCleaner(bool staged, bool byDisposal)
    {
        // A staged transaction is staged on a version 1 repository, where it holds a marker for each deletion and rolls each of them back on its own, as an
        // ordinary transaction does.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, staged ? RepoFormatVersion.Version1 : RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var deleted = await AddSampleFilesAsync(repo, FileCount / 2);
            var added = new List<FileId>();
            IAsyncDisposable txn;
            Func<Task> rollback;

            if (staged)
            {
                var stagedTxn = await repo.BeginStagedTransactionAsync();
                txn = stagedTxn;
                rollback = () => stagedTxn.RollbackAsync(TestContext.CancellationToken);

                for (int i = 0; i < FileCount / 2; i++)
                    added.Add(await AddSampleFileAsync(stagedTxn));

                foreach (var fileId in deleted)
                    await stagedTxn.DeleteAsync(fileId, TestContext.CancellationToken);

                await stagedTxn.StageAsync(TestContext.CancellationToken);
            }
            else
            {
                var ordinaryTxn = await repo.BeginTransactionAsync();
                txn = ordinaryTxn;
                rollback = () => ordinaryTxn.RollbackAsync(TestContext.CancellationToken);

                for (int i = 0; i < FileCount / 2; i++)
                    added.Add(await AddSampleFileAsync(ordinaryTxn));

                foreach (var fileId in deleted)
                    await ordinaryTxn.DeleteAsync(fileId, TestContext.CancellationToken);
            }

            await volume.StopAnsweringOnceIdleAsync();
            ShouldHaveWaitedOnce(await TimeAsync(byDisposal ? () => txn.DisposeAsync().AsTask() : rollback));
            await txn.DisposeAsync();

            ShouldHaveReportedTimeouts(failures, RepoTransactionCompletionOperation.Rollback, FileCount);

            foreach (var fileId in added.Concat(deleted))
                ShouldBeIndeterminate(repoDir, fileId);

            volume.AnswerAgain();

            // The application's database has rolled back too, so its answer to the cleaner is to delete the files it added and to keep the files it deleted.
            var asked = await CleanAnsweringAsync(repo, id => added.Contains(id) ? IndeterminateResolution.Delete : IndeterminateResolution.Keep);
            asked.ShouldBe(added.Concat(deleted), ignoreOrder: true);

            added.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);

            foreach (var fileId in deleted)
                ShouldBeCommitted(repoDir, fileId);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StageCommit_OfDeletions_OnAVolumeThatStoppedAnswering_WaitsOnce_AndTheCleanerFinishesThem(RepoFormatVersion formatVersion)
    {
        // In immediate mode the commit deletes each file the journal lists once it has committed the journal; on version 1 it commits each deletion as an
        // ordinary transaction does.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, FileCount);

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                var stage = await txn.StageAsync(TestContext.CancellationToken);

                await volume.StopAnsweringOnceIdleAsync();
                ShouldHaveWaitedOnce(await TimeAsync(() => stage.CommitAsync(TestContext.CancellationToken)));
            }

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            if (formatVersion is RepoFormatVersion.Version1)
            {
                // No deletion is recorded: each of them failed to commit, which is reported, and its file is indeterminate.
                ShouldHaveReportedTimeouts(failures, RepoTransactionCompletionOperation.Commit, FileCount);

                foreach (var fileId in fileIds)
                    ShouldBeIndeterminate(repoDir, fileId);
            }
            else
            {
                // The journal was committed before any file was reached, so every deletion is committed although none could be carried out: nothing is
                // reported, and the journal is kept, with nobody holding it, for the cleaner.
                failures.ShouldBeEmpty();
                ShouldBeTheUnheldCommittedJournal(CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem());
            }

            volume.AnswerAgain();

            // The application's database has committed the deletions. On version 2 the committed journal records them, and the cleaner deletes the files
            // without asking.
            var asked = await CleanAnsweringAsync(repo, _ => IndeterminateResolution.Delete);
            asked.ShouldBe(formatVersion is RepoFormatVersion.Version1 ? fileIds : [], ignoreOrder: true);

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task StageCommit_OfAddsAndDeletions_OnAVolumeThatStoppedAnswering_ReportsTheAdds_AndNotTheDeletionsItsJournalRecords()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var deleted = await AddSampleFilesAsync(repo, FileCount);
            var added = new List<FileId>();

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                for (int i = 0; i < FileCount / 2; i++)
                    added.Add(await AddSampleFileAsync(txn));

                foreach (var fileId in deleted)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                var stage = await txn.StageAsync(TestContext.CancellationToken);

                // The commit renames the journal, which needs no access check, then commits each add, and then deletes each file the journal lists.
                await volume.StopAnsweringOnceIdleAsync();
                ShouldHaveWaitedOnce(await TimeAsync(() => stage.CommitAsync(TestContext.CancellationToken)));
            }

            // Nothing records that the adds are committed, so their failures are reported and the files are indeterminate. The deletions are committed
            // by the journal, so the files that could not be deleted are not failures, and the journal is kept for the cleaner.
            ShouldHaveReportedTimeouts(failures, RepoTransactionCompletionOperation.Commit, added.Count);

            foreach (var fileId in added)
                ShouldBeIndeterminate(repoDir, fileId);

            deleted.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            ShouldBeTheUnheldCommittedJournal(CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.CommittedJournalExtension).ShouldHaveSingleItem());
            CleanupDir(repoDir).GetChildFiles().Count().ShouldBe(added.Count + 1);

            volume.AnswerAgain();

            // The application's database has committed, so its answer to the cleaner is to keep the files it added. The cleaner does not ask about the
            // files the journal lists.
            (await CleanAnsweringAsync(repo, _ => IndeterminateResolution.Keep)).ShouldBe(added, ignoreOrder: true);

            foreach (var fileId in added)
                ShouldBeCommitted(repoDir, fileId);

            deleted.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldHaveSingleItem();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.DeferredUntilClean, false)]
    [DataRow(DeleteMode.DeferredFilesOnly, false)]
    [DataRow(DeleteMode.DeferredUntilClean, true)]
    public async Task Commit_OfDeletions_WhoseVolumeStopsAnsweringOnceTheirJournalIsWritten_WaitsOnce_ReportsNoDeletion_AndTheCleanerFinishesThem(
        DeleteMode deleteMode, bool withAnAddWhoseCommitFails)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, FileCount);
            FileId? added = null;
            long stoppedAnswering = 0;
            TimeSpan waited;

            // The commit commits its adds, records its deletions in one journal and then removes the marker of each deleted file, which needs access to
            // the repository for each of them. The volume stops answering once the journal is written. The wait for the health checks to stop is made in
            // the debug step, which holds nothing that the checks need. With an add, its commit fails before that, while the volume still answers.
            volume.OnStep = step =>
            {
                if (step is DebugStep.AddCommitStarted)
                    throw new IOException("Simulated failure to commit the add.");

                if (step is DebugStep.DeleteJournalWritten)
                {
                    volume.StopAnsweringOnceIdle();
                    Volatile.Write(ref stoppedAnswering, Stopwatch.GetTimestamp());
                }
            };

            await using (var txn = await repo.BeginTransactionAsync())
            {
                if (withAnAddWhoseCommitFails)
                    added = await AddSampleFileAsync(txn);

                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                await txn.CommitAsync(TestContext.CancellationToken);
                waited = Stopwatch.GetElapsedTime(Volatile.Read(ref stoppedAnswering));
            }

            volume.OnStep = null;
            Volatile.Read(ref stoppedAnswering).ShouldNotBe(0);
            ShouldHaveWaitedOnce(waited);

            // The journal records the deletions, so the markers that could not be removed are not failures of the commit: none of them is reported. The
            // add that could not be committed is, as ever, and its file is indeterminate.
            if (added is null)
                failures.ShouldBeEmpty();
            else
                failures.ShouldHaveSingleItem().Exception.ShouldBeOfType<IOException>().Message.ShouldBe("Simulated failure to commit the add.");

            // The journal is on disk, and so is each deleted file with its marker, which nobody holds.
            ShouldBeTheUnheldCommittedJournal(CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.CommittedJournalExtension).ShouldHaveSingleItem());
            CleanupDir(repoDir).GetChildFiles().Count().ShouldBe(FileCount + 1 + (added is null ? 0 : 1));

            foreach (var fileId in fileIds)
                ShouldBeIndeterminate(repoDir, fileId);

            if (added is not null)
                ShouldBeIndeterminate(repoDir, added);

            volume.AnswerAgain();

            // The cleaner deletes the files the journal lists, and removes their markers with them, without asking. It asks about the added file, which
            // the application's database references.
            FileId[] expectedToBeAsked = added is null ? [] : [added];
            (await CleanAnsweringAsync(repo, _ => IndeterminateResolution.Keep)).ShouldBe(expectedToBeAsked);

            if (added is not null)
                ShouldBeCommitted(repoDir, added);

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.OnStep = null;
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Stage_Version1_OnAVolumeThatStopsAnsweringPartway_FailsAfterOneWait_AndLeavesTheMarkersItWroteForTheCleaner()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version1);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, FileCount);
            var written = fileIds[..^1];

            await using var txn = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            // Staging opens the markers in the order of the deletions. The volume stops answering once all but the last are open, so the last cannot be
            // opened, and staging then removes the ones it opened, each of which needs access as well. The wait for the health checks to stop is made in the
            // debug step, which holds the file's lock but nothing that the checks need.
            int opened = 0;
            long stoppedAnswering = 0;

            volume.OnStep = step =>
            {
                if (step is DebugStep.DeleteMarkerOpened && Interlocked.Increment(ref opened) == written.Length)
                {
                    volume.StopAnsweringOnceIdle();
                    Volatile.Write(ref stoppedAnswering, Stopwatch.GetTimestamp());
                }
            };

            await Should.ThrowAsync<TimeoutException>(() => txn.StageAsync(TestContext.CancellationToken));
            ShouldHaveWaitedOnce(Stopwatch.GetElapsedTime(Volatile.Read(ref stoppedAnswering)));

            // Nothing is staged. The markers it wrote could not be removed and are left with nobody holding them.
            foreach (var fileId in written)
                ShouldBeIndeterminate(repoDir, fileId);

            IndeterminateMarker(repoDir, fileIds[^1]).State.ShouldNotBe(EntryState.Exists);

            volume.OnStep = null;
            volume.AnswerAgain();
            await txn.DisposeAsync();

            // The application did not commit its database, so its answer to the cleaner is to keep the files.
            (await CleanAnsweringAsync(repo, _ => IndeterminateResolution.Keep)).ShouldBe(written, ignoreOrder: true);

            foreach (var fileId in fileIds)
                ShouldBeCommitted(repoDir, fileId);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // What the wait is for
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// On a volume that hangs rather than fails, the time goes into probing it and into the attempts to reach it, so that time is charged to the commit's
    /// wait as well: a probe that hangs for longer than the wait uses the wait up, and the commit fails once the probe has given up, instead of then retrying
    /// for a whole wait more.
    /// </summary>
    [TestMethod]
    public async Task Commit_OnAVolumeThatHangsRatherThanFails_ChargesTheHangToTheWait()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir) { ProbeHang = _wait * 1.5 };

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, FileCount);
            TimeSpan elapsed;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                await volume.StopAnsweringOnceIdleAsync();
                elapsed = await TimeAsync(() => txn.CommitAsync(TestContext.CancellationToken));
            }

            // The probe's hang, and the attempts after it, which fail at once; not the hang and a whole wait of retries after it.
            elapsed.ShouldBeGreaterThan(volume.ProbeHang.Value * 0.9);
            elapsed.ShouldBeLessThan(volume.ProbeHang.Value + (_wait * 0.5));

            ShouldHaveReportedTimeouts(failures, RepoTransactionCompletionOperation.Commit, FileCount + 1);

            foreach (var fileId in fileIds)
                ShouldBeIndeterminate(repoDir, fileId);
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Commit_OnAVolumeThatStopsAnsweringForLessThanTheWait_RidesItOut_AndCommitsEveryFile()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, FileCount);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                // The volume answers again halfway through the commit's first wait for it.
                await volume.StopAnsweringOnceIdleAsync(answerAgainAfter: _wait / 2);
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            volume.FailedProbes.ShouldBe(1);
            volume.Initializations.ShouldBe(1);
            failures.ShouldBeEmpty();

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Commit_WhoseWorkTakesLongerThanTheWait_CommitsEveryFile(bool outageAfterTheWork)
    {
        // Each deletion takes a while to commit, which on a healthy volume uses none of the wait. With an outage, the volume stops answering once the work
        // has taken longer than the wait, and the last file's access must still be able to wait for it.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, FileCount);
            int committing = 0;

            volume.OnStep = step =>
            {
                if (step is not DebugStep.DeleteCommitStarted)
                    return;

                Thread.Sleep(_slowFileWork);

                if (Interlocked.Increment(ref committing) == FileCount - 1 && outageAfterTheWork)
                    volume.StopAnsweringOnceIdle(answerAgainAfter: _wait / 2);
            };

            await using (var txn = await repo.BeginTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                (await TimeAsync(() => txn.CommitAsync(TestContext.CancellationToken))).ShouldBeGreaterThan(_wait);
            }

            volume.FailedProbes.ShouldBe(outageAfterTheWork ? 1 : 0);
            volume.Initializations.ShouldBe(outageAfterTheWork ? 1 : 0);
            failures.ShouldBeEmpty();

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts that a call waited for access once: for about the wait, and for well under twice it, where one wait per file takes at least
    /// <see cref="FileCount"/> times it.
    /// </summary>
    private static void ShouldHaveWaitedOnce(TimeSpan elapsed)
    {
        elapsed.ShouldBeGreaterThan(_wait * 0.9);
        elapsed.ShouldBeLessThan(_wait * 2);
    }

    /// <summary>
    /// Asserts that the transaction reported one failure of the specified operation, made of the specified number of <see cref="TimeoutException"/>s: one
    /// for each access to the repository that the operation made.
    /// </summary>
    private static void ShouldHaveReportedTimeouts(
        List<RepoTransactionCompletionFailureInfo> failures, RepoTransactionCompletionOperation operation, int count)
    {
        var reported = failures.ShouldHaveSingleItem();
        reported.Operation.ShouldBe(operation);

        var exceptions = reported.Exception.ShouldBeOfType<AggregateException>().InnerExceptions;
        exceptions.Count.ShouldBe(count);
        exceptions.ShouldAllBe(ex => ex is TimeoutException);
    }

    /// <summary>
    /// Asserts that a file is indeterminate: its group is on disk, with an indeterminate marker that nobody holds.
    /// </summary>
    private static void ShouldBeIndeterminate(IAbsoluteDirectoryPath repoDir, FileId fileId)
    {
        FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeFalse();
        IndeterminateMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);
    }

    /// <summary>
    /// Asserts that a file is in the repository with nothing marking it.
    /// </summary>
    private static void ShouldBeCommitted(IAbsoluteDirectoryPath repoDir, FileId fileId)
    {
        FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        DeleteMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
    }

    /// <summary>
    /// Asserts that an entry of the cleanup directory is a committed delete journal that nobody holds, which the cleaner can therefore apply.
    /// </summary>
    private static void ShouldBeTheUnheldCommittedJournal(IAbsoluteFilePath entry)
    {
        entry.Extension.ShouldBe(FileRepoPaths.CommittedJournalExtension);
        IsHeld(entry).ShouldBeFalse();
    }

    /// <summary>
    /// Returns <see langword="true"/> if a marker exists and somebody has it open, which is how the cleaner tells that its transaction is active.
    /// </summary>
    private static bool IsHeld(IAbsoluteFilePath file)
    {
        if (file.State is not EntryState.Exists)
            return false;

        try
        {
            using var probe = file.OpenStream(FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static async Task<TimeSpan> TimeAsync(Func<Task> action)
    {
        long start = Stopwatch.GetTimestamp();
        await action();
        return Stopwatch.GetElapsedTime(start);
    }

    /// <summary>
    /// Runs a cleanup pass with no delete delay, whose resolution callback answers as specified, and returns the files it was asked about.
    /// </summary>
    private async Task<List<FileId>> CleanAnsweringAsync(FileRepo repo, Func<FileId, IndeterminateResolution> answer)
    {
        var asked = new List<FileId>();

        await repo.CleanAsync(
            TimeSpan.Zero,
            id =>
            {
                asked.Add(id);
                return answer(id);
            },
            TestContext.CancellationToken);

        return asked;
    }

    private static List<RepoTransactionCompletionFailureInfo> TrackCompletionFailures(FileRepo repo)
    {
        var failures = new List<RepoTransactionCompletionFailureInfo>();

        repo.TransactionCompletionFailed += failure =>
        {
            lock (failures)
                failures.Add(failure);

            return Task.CompletedTask;
        };

        return failures;
    }

    private async Task<FileId[]> AddSampleFilesAsync(FileRepo repo, int count)
    {
        var fileIds = new FileId[count];

        await using var txn = await repo.BeginTransactionAsync();

        for (int i = 0; i < count; i++)
            fileIds[i] = await AddSampleFileAsync(txn);

        await txn.CommitAsync(TestContext.CancellationToken);
        return fileIds;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepoTransaction txn)
    {
        await using var source = OpenSample();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        return added.FileId;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepoStagedTransaction txn)
    {
        await using var source = OpenSample();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        return added.FileId;
    }

    private static FileStream OpenSample() => File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);

    private static FileRepo CreateRepo(IAbsoluteDirectoryPath repoDir, DeleteMode deleteMode, RepoFormatVersion formatVersion)
    {
        var repo = new FileRepo(repoDir, formatVersion, o => {
            o.DeleteMode = deleteMode;
            o.MaxAccessWaitOrRetryTime = _wait;
            o.HealthCheckInterval = _healthCheckInterval;
        });

        repo.EnsureCreated();
        repo.FormatVersion.ShouldBe(formatVersion);
        return repo;
    }

    private static IAbsoluteDirectoryPath CleanupDir(IAbsoluteDirectoryPath repoDir) =>
        repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);

    private static IAbsoluteDirectoryPath FileDir(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);

    private static IAbsoluteFilePath IndeterminateMarker(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        CleanupDir(repoDir).CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);

    private static IAbsoluteFilePath DeleteMarker(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        CleanupDir(repoDir).CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_AccessWaitBudget_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
