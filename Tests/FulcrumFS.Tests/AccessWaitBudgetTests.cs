using System.Diagnostics;
using System.Globalization;

namespace FulcrumFS;

/// <summary>
/// Pins that a commit, a rollback and the staging of a version 1 repository wait for access to the repository for
/// <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> once, shared by all of their files, instead of once per file: on a volume that has stopped
/// answering they fail after one wait and leave their files for the cleaner, they still ride out an outage shorter than the wait, and the time their work
/// takes does not count.
/// </summary>
/// <remarks>
/// The volume stops answering as the repository sees it when that happens while it is idle: its health checks have stopped, the next access probes the
/// volume inline and the probe fails, and initializing the repository again fails, because its info file is gone, until the volume answers again.
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
    public async Task StageCommit_OfDeletions_OnAVolumeThatStoppedAnswering_FailsAfterOneWait_AndTheCleanerFinishesThem(RepoFormatVersion formatVersion)
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

            ShouldHaveReportedTimeouts(failures, RepoTransactionCompletionOperation.Commit, FileCount);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            if (formatVersion is RepoFormatVersion.Version1)
            {
                foreach (var fileId in fileIds)
                    ShouldBeIndeterminate(repoDir, fileId);
            }
            else
            {
                CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().Extension.ShouldBe(FileRepoPaths.CommittedJournalExtension);
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

    /// <summary>
    /// The volume of a repository, which can stop answering as the repository sees it: the repository's health probe fails, and its info file is moved
    /// aside, so that initializing the repository again fails until the volume answers again. It takes the repository's debug step hook and passes every
    /// step that it does not fail on to <see cref="OnStep"/>.
    /// </summary>
    private sealed class SimulatedVolume
    {
        private static readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(10);

        private readonly FileRepo _repo;
        private readonly string _infoFile;
        private readonly string _infoFileAside;
        private readonly Lock _sync = new();

        private bool _isAnswering = true;
        private TimeSpan? _answerAgainAfter;
        private int _failedProbes;
        private int _initializations;

        public SimulatedVolume(FileRepo repo, IAbsoluteDirectoryPath repoDir)
        {
            _repo = repo;
            _infoFile = repoDir.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None).PathExport;
            _infoFileAside = _infoFile + ".aside";

            repo.DebugStepHook = OnDebugStep;
        }

        /// <summary>
        /// Gets or sets an action invoked for each debug step of the repository that the volume does not fail on.
        /// </summary>
        public Action<DebugStep>? OnStep { get; set; }

        /// <summary>
        /// Gets or sets how long a health probe hangs before it fails while the volume is not answering, for a volume that hangs rather than fails; or
        /// <see langword="null"/> for one that fails at once.
        /// </summary>
        public TimeSpan? ProbeHang { get; set; }

        /// <summary>
        /// Gets the number of health probes that failed because the volume was not answering.
        /// </summary>
        public int FailedProbes => Volatile.Read(ref _failedProbes);

        /// <summary>
        /// Gets the number of times the repository has been initialized since the volume was created.
        /// </summary>
        public int Initializations => Volatile.Read(ref _initializations);

        /// <summary>
        /// Waits until the repository has been idle for long enough that its health checks have stopped, so that its next access probes the volume inline, and
        /// then stops answering. If <paramref name="answerAgainAfter"/> is specified, the volume answers again that long after that probe failed.
        /// </summary>
        public async Task StopAnsweringOnceIdleAsync(TimeSpan? answerAgainAfter = null)
        {
            long start = Stopwatch.GetTimestamp();

            while (_repo.HealthChecksActive)
            {
                ThrowIfIdleTimedOut(start);
                await Task.Delay(50);
            }

            StopAnswering(answerAgainAfter);
        }

        /// <summary>
        /// Does what <see cref="StopAnsweringOnceIdleAsync(TimeSpan?)"/> does, blocking the calling thread, for a debug step.
        /// </summary>
        public void StopAnsweringOnceIdle(TimeSpan? answerAgainAfter = null)
        {
            long start = Stopwatch.GetTimestamp();

            while (_repo.HealthChecksActive)
            {
                ThrowIfIdleTimedOut(start);
                Thread.Sleep(50);
            }

            StopAnswering(answerAgainAfter);
        }

        /// <summary>
        /// Makes the volume answer again, if it is not answering.
        /// </summary>
        public void AnswerAgain()
        {
            lock (_sync)
            {
                if (_isAnswering)
                    return;

                File.Move(_infoFileAside, _infoFile);
                _isAnswering = true;
            }
        }

        private void StopAnswering(TimeSpan? answerAgainAfter)
        {
            lock (_sync)
            {
                File.Move(_infoFile, _infoFileAside);
                _isAnswering = false;
                _answerAgainAfter = answerAgainAfter;
            }
        }

        private void OnDebugStep(DebugStep step)
        {
            if (step is DebugStep.HealthCheckProbe)
            {
                TimeSpan? answerAgainAfter;

                lock (_sync)
                {
                    if (_isAnswering)
                        return;

                    answerAgainAfter = _answerAgainAfter;
                    _answerAgainAfter = null;
                }

                Interlocked.Increment(ref _failedProbes);

                if (ProbeHang is { } hang)
                    Thread.Sleep(hang);

                if (answerAgainAfter is { } delay)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(delay);
                        AnswerAgain();
                    });
                }

                throw new IOException("Simulated volume that does not answer.");
            }

            if (step is DebugStep.RepoInitialized)
                Interlocked.Increment(ref _initializations);

            OnStep?.Invoke(step);
        }

        private static void ThrowIfIdleTimedOut(long start)
        {
            if (Stopwatch.GetElapsedTime(start) > _idleTimeout)
                throw new InvalidOperationException("The repository's health checks did not stop while it was idle.");
        }
    }
}
