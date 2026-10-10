namespace FulcrumFS;

/// <summary>
/// Identifies a checkpoint in the delete/rebase flow (or in repository initialization, health checking and transaction commits) at which a test may observe
/// progress or inject a simulated crash or failure by throwing from <see cref="FileRepo.DebugStepHook"/>. The hook is <see langword="internal"/> and is never
/// set in production use; it is exercised only by the test assembly via <c>InternalsVisibleTo</c>. Because the delete/rebase flow is strictly forward-only
/// (every step persists a marker and never compensates on failure), throwing from the hook at a checkpoint leaves exactly the on-disk state a real crash
/// would, which lets recovery (cleaner rollforward, or the add/delete optimistic rollforward) be tested faithfully.
/// </summary>
internal enum DebugStep
{
    /// <summary>The pre-commit cleanup-dir hints have been written, but no in-group delete marker exists yet (the operation has not committed).</summary>
    DeleteHintsWritten,

    /// <summary>The in-group delete markers have been written (the commit point); every listed variant now reads as retired.</summary>
    DeleteMarkersWritten,

    /// <summary>A subtree's <c>.rebase</c> marker has been written, but the chosen survivor has not been materialized yet.</summary>
    RebaseMarkerWritten,

    /// <summary>The chosen survivor has been materialized into a real data file, but the remaining survivors have not been re-pointed yet.</summary>
    RebaseMaterialized,

    /// <summary>The remaining survivors have been re-pointed onto the chosen variant, and the subtree's <c>.rebase</c> marker is about to be deleted.</summary>
    RebaseRepointed,

    /// <summary>Materialization of all rebases has completed and the retired physical residue (data files, markers, delete markers) is about to be deleted.</summary>
    DeleteResidueAboutToDelete,

    /// <summary>The repository lock has been acquired and per-session state prepared (initially, or again after a failed health check).</summary>
    RepoInitialized,

    /// <summary>A health check is about to probe the repository volume; throwing an <see cref="IOException"/> from the hook simulates a failed probe.</summary>
    HealthCheckProbe,

    /// <summary>A health check has written its probe to the lock file, so the probe has succeeded.</summary>
    HealthCheckProbeWritten,

    /// <summary>
    /// The commit of a staged transaction has started (the application has committed its database) but nothing has been committed yet; throwing from the
    /// hook and then disposing the transaction leaves the on-disk state of a process that ended between staging and committing.
    /// </summary>
    StagedCommitStarted,

    /// <summary>
    /// A committing transaction has released the indeterminate marker of a file it added and is about to remove it, which is what commits the add. Throwing
    /// from the hook fails the commit of that one file the way a real failure at this point does: the file stays where it is with a marker that nobody
    /// holds, so it is indeterminate, and the transaction reports the failure and goes on to commit its other files.
    /// </summary>
    AddCommitStarted,

    /// <summary>
    /// A committing transaction has released the indeterminate marker of a file it deleted and is about to record or carry out the deletion (not raised for
    /// a deletion that a delete journal records). Throwing from the hook fails the commit of that one deletion before anything of it has happened: the file
    /// stays where it is with a marker that nobody holds, so it is indeterminate, and the transaction reports the failure and goes on to commit its other
    /// files.
    /// </summary>
    DeleteCommitStarted,
}

/// <content>
/// Contains a test-only instrumentation seam used to deterministically reproduce crashes at well-defined points in the multi-step delete/rebase flow. See
/// <see cref="DebugStep"/> for the rationale.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Gets or sets a test-only hook invoked at each <see cref="DebugStep"/> checkpoint. <see langword="null"/> in all production
    /// use. A test may throw from the hook to simulate a crash at a precise point, or use it purely to observe step ordering. Read through
    /// <see cref="RepoFileSystem.DebugStepHook"/> at each callsite so changes are observed immediately.
    /// </summary>
    internal Action<DebugStep>? DebugStepHook { get; set; }
}
