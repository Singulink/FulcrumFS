using System.Diagnostics;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoHealthCheckTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(1);

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task HealthCheck_RunsInBackgroundWhileActive_StopsWhenIdle_ProbesInlineOnResume()
    {
        var repoDir = NewRepoDir();

        var repo = new FileRepo(repoDir, o =>
        {
            o.HealthCheckInterval = _interval;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });

        repo.EnsureCreated();

        try
        {
            int probes = 0;
            int initializations = 0;
            int failuresToInject = 0;

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.RepoInitialized)
                {
                    Interlocked.Increment(ref initializations);
                }
                else if (step is DebugStep.HealthCheckProbe)
                {
                    Interlocked.Increment(ref probes);

                    if (Interlocked.Exchange(ref failuresToInject, 0) > 0)
                        throw new IOException("Simulated volume failure.");
                }
            };

            var lockFile = new FileInfo(repoDir.CombineFile(FileRepoPaths.RepoLockFileName, PathOptions.None).PathExport);

            // The first access initializes the repository and starts background checks.
            await AccessAsync(repo);
            Volatile.Read(ref initializations).ShouldBe(1);
            repo.HealthChecksActive.ShouldBeTrue();

            // While the repository is accessed regularly, checks run in the background (no access ever probes inline) and never grow the lock file.
            await AccessRepeatedlyAsync(repo, until: () => Volatile.Read(ref probes) >= 3, timeout: _interval * 15);
            repo.HealthChecksActive.ShouldBeTrue();
            Volatile.Read(ref initializations).ShouldBe(1);
            lockFile.Refresh();
            lockFile.Length.ShouldBe(1);

            // Once idle for the interval, checks stop and no further I/O happens.
            await WaitUntilAsync(() => !repo.HealthChecksActive, _interval * 10);
            int probesWhenIdle = Volatile.Read(ref probes);
            await Task.Delay(_interval * 3);
            Volatile.Read(ref probes).ShouldBe(probesWhenIdle);

            // The first access after an idle period probes inline, then resumes background checks.
            await AccessAsync(repo);
            Volatile.Read(ref probes).ShouldBe(probesWhenIdle + 1);
            repo.HealthChecksActive.ShouldBeTrue();
            Volatile.Read(ref initializations).ShouldBe(1);

            // A failed inline probe (volume failed while idle) re-initializes within the same access, which still succeeds.
            await WaitUntilAsync(() => !repo.HealthChecksActive, _interval * 10);
            Volatile.Write(ref failuresToInject, 1);
            await AccessAsync(repo);
            Volatile.Read(ref failuresToInject).ShouldBe(0);
            Volatile.Read(ref initializations).ShouldBe(2);
            repo.HealthChecksActive.ShouldBeTrue();

            // A failed background probe (volume failed while active) makes the next access re-initialize.
            Volatile.Write(ref failuresToInject, 1);
            await AccessRepeatedlyAsync(repo, until: () => Volatile.Read(ref initializations) == 3, timeout: _interval * 15);
            Volatile.Read(ref failuresToInject).ShouldBe(0);
            repo.HealthChecksActive.ShouldBeTrue();

            // Re-initialization recreates the lock file (DeleteOnClose); once probed again it is back to its single byte.
            int probesAfterReinit = Volatile.Read(ref probes);
            await AccessRepeatedlyAsync(repo, until: () => Volatile.Read(ref probes) > probesAfterReinit, timeout: _interval * 15);
            lockFile.Refresh();
            lockFile.Length.ShouldBe(1);
        }
        finally
        {
            repo.Dispose();
        }
    }

    private static async Task AccessAsync(FileRepo repo)
    {
        await using (await repo.BeginTransactionAsync())
        {
        }
    }

    private static async Task AccessRepeatedlyAsync(FileRepo repo, Func<bool> until, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            await AccessAsync(repo);

            if (until())
                return;

            if (Stopwatch.GetElapsedTime(start) > timeout)
                throw new TimeoutException("The condition was not met within the allotted time.");

            await Task.Delay(100);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();

        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > timeout)
                throw new TimeoutException("The condition was not met within the allotted time.");

            await Task.Delay(50);
        }
    }

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_FileRepoHealthCheck_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
