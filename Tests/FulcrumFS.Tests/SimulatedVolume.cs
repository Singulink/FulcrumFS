using System.Diagnostics;

namespace FulcrumFS;

/// <summary>
/// The volume of a repository, which can stop answering as the repository sees it: the repository's health probe fails, and its info file is moved aside,
/// so that initializing the repository again fails until the volume answers again. It takes the repository's debug step hook and passes every step that it
/// does not fail on to <see cref="OnStep"/>.
/// </summary>
/// <remarks>
/// The repository learns that its volume does not answer from a probe of its own, so the volume is made to stop while the repository is idle (see
/// <see cref="StopAnsweringOnceIdleAsync(TimeSpan?)"/>): its health checks have stopped by then, its next access probes the volume inline, which fails, and
/// initializing it again fails for as long as the info file is aside. Nothing else is simulated: any other file of the repository can be read, written and
/// deleted while the volume does not answer, which is all that an operation that is already past the repository's access check needs.
/// </remarks>
internal sealed class SimulatedVolume
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
