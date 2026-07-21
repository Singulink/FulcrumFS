namespace FulcrumFS;

/// <summary>
/// Owns everything that touches the repository's on-disk layout: path arithmetic, group-structure operations, and marker file primitives. Both
/// <see cref="FileRepo"/> and <see cref="FileRepoCleaner"/> compose an instance and route all filesystem-aware operations through it so callsites never need
/// to choose between the files and cleanup directories. Dependency flows strictly one way: callers depend on this class; this class depends on neither
/// <see cref="FileRepo"/> nor <see cref="FileRepoCleaner"/>.
/// </summary>
internal sealed partial class RepoFileSystem
{
    /// <summary>
    /// Gets the repository base directory.
    /// </summary>
    public IAbsoluteDirectoryPath BaseDirectory { get; }

    /// <summary>
    /// Gets the directory where file groups (data files, in-group delete markers, alias markers, rebase markers) are stored.
    /// </summary>
    public IAbsoluteDirectoryPath FilesDirectory { get; }

    /// <summary>
    /// Gets the directory where cleanup-side markers (delete hints, indeterminate markers, the clean lock file) are stored.
    /// </summary>
    public IAbsoluteDirectoryPath CleanupDirectory { get; }

    /// <summary>
    /// Gets the marker file logging mode captured at construction. Consumed by all marker primitives and structure-aware operations so callsites do not need
    /// to thread it through every call.
    /// </summary>
    public RepoLoggingMode MarkerFileLogging { get; }

    /// <summary>
    /// Gets or sets a provider that returns the current <see cref="RepoCorruptionHandler"/> (or <see langword="null"/> when no subscribers are attached).
    /// Invoked via <see cref="CorruptionDetected"/> by structure-aware operations immediately before a <see cref="RepoCorruptedException"/> is thrown so
    /// monitoring and repair tooling observes the condition. The indirection lets <see cref="FileRepo"/> wire this once at construction
    /// (<c>() =&gt; CorruptionDetected</c>) while still tracking live subscriber changes; <see cref="FileRepoCleaner"/> leaves it <see langword="null"/>
    /// (the cleaner does not surface corruption events).
    /// </summary>
    public Func<RepoCorruptionHandler?>? GetCorruptionDetectedHandler { get; set; }

    /// <summary>
    /// Gets the current corruption handler by invoking <see cref="GetCorruptionDetectedHandler"/>, or <see langword="null"/> if no provider is wired or the provider
    /// returns <see langword="null"/>. Call this at the corruption-raise site so the result reflects the live subscriber chain at the moment of the event.
    /// </summary>
    public RepoCorruptionHandler? CorruptionDetected => GetCorruptionDetectedHandler?.Invoke();

    /// <summary>
    /// Gets or sets a provider that returns the current test-only <see cref="DebugStep"/> hook (or <see langword="null"/> when none is set). Invoked via
    /// <see cref="DebugStepHook"/> at each delete/rebase checkpoint. The indirection lets <see cref="FileRepo"/> own the hook field while <see cref="RepoFileSystem"/>
    /// reads through to it on each invocation; <see cref="FileRepoCleaner"/> leaves it <see langword="null"/>.
    /// </summary>
    internal Func<Action<DebugStep>?>? GetDebugStepHook { get; set; }

    /// <summary>
    /// Gets the current test-only debug step hook by invoking <see cref="GetDebugStepHook"/>, or <see langword="null"/> if no provider is wired or the
    /// provider returns <see langword="null"/>. <see langword="null"/> in all production use.
    /// </summary>
    internal Action<DebugStep>? DebugStepHook => GetDebugStepHook?.Invoke();

    public RepoFileSystem(IAbsoluteDirectoryPath baseDirectory, RepoLoggingMode markerFileLogging)
    {
        BaseDirectory = baseDirectory;
        FilesDirectory = baseDirectory.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None);
        CleanupDirectory = baseDirectory.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);
        MarkerFileLogging = markerFileLogging;
    }
}
