namespace FulcrumFS;

/// <summary>
/// Identifies a checkpoint in the delete/rebase flow (or in repository initialization, health checking, the deletions and commits of transactions, and the
/// fetches of variants) at which a test may observe progress or inject a simulated crash or failure by throwing from <see cref="FileRepo.DebugStepHook"/>,
/// or change the repository as a concurrent operation would. The hook is <see langword="internal"/> and is never set in production use; it is exercised
/// only by the test assembly via <c>InternalsVisibleTo</c>. Because the
/// delete/rebase flow is strictly forward-only (every step persists a marker and never compensates on failure), throwing from the hook at a checkpoint
/// leaves exactly the on-disk state a real crash would, which lets recovery (cleaner rollforward, or the add/delete optimistic rollforward) be tested
/// faithfully.
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
    /// holds, so it is indeterminate, and the transaction reports the failure and goes on to commit its other files. The hook is called with the file's
    /// lock held, because releasing and removing the marker are one step: a deletion of the file that the hook starts on another transaction does not get
    /// through until the hook has returned and the marker is gone, so the hook must not wait for it.
    /// </summary>
    AddCommitStarted,

    /// <summary>
    /// A committing transaction has released the indeterminate marker of a file it deleted and is about to record or carry out the deletion (not raised for
    /// a deletion that a delete journal records). Throwing from the hook fails the commit of that one deletion before anything of it has happened: the file
    /// stays where it is with a marker that nobody holds, so it is indeterminate, and the transaction reports the failure and goes on to commit its other
    /// files. The hook is called with the file's lock held, as for <see cref="AddCommitStarted"/>: the rollback of another transaction's deletion of the
    /// file does not get through until the hook has returned and the deletion is recorded.
    /// </summary>
    DeleteCommitStarted,

    /// <summary>
    /// A transaction that is deleting a file found the file's indeterminate marker in use, which normally means that the transaction that added the file
    /// has not finished, and is about to wait before it tries to open the marker again. The hook is called without the file's lock, so it can finish the
    /// transaction that holds the marker, which makes the next attempt the first one that follows the add's commit or rollback.
    /// </summary>
    DeleteMarkerInUse,

    /// <summary>
    /// A transaction that is deleting a file has opened the file's indeterminate marker and is about to count the deletion as pending through it. The hook
    /// is called with the file's lock held. Removing the marker from the hook does what something outside the repository's transactions can do at this
    /// point, such as a cleanup pass in another process on a platform where opening a file and locking it are two steps: it leaves the deletion holding a
    /// marker that is no longer on disk.
    /// </summary>
    DeleteMarkerOpened,

    /// <summary>
    /// A transaction that is rolling back the add of a file, or discarding it, has released the file's indeterminate marker and is about to delete the file
    /// with its marker. The hook is called with the file's lock held. Throwing from it fails the rollback of that one file before anything of it has
    /// happened: the file stays where it is with a marker that nobody holds, so it is indeterminate.
    /// </summary>
    AddRollbackStarted,

    /// <summary>
    /// A committing transaction has recorded its deletions in a committed delete journal, flushed to disk, and is about to finish each of them by releasing
    /// and removing the file's indeterminate marker (raised by the commit of an ordinary transaction, which writes its journal as it commits). The hook is
    /// called with no file's lock held. The deletions are committed from here on whatever happens, so a test can make the repository inaccessible in the
    /// hook to fail every step that follows. Throwing from the hook is reported as a failure of the commit and changes nothing else: the deletions are
    /// finished as the journal's.
    /// </summary>
    DeleteJournalWritten,

    /// <summary>
    /// A fetch of a variant, or an add, has read an alias marker from a variant's slot and is about to look at the data file of its source: every read of
    /// a slot until it is steady raises it for an alias marker, and so does an add for the marker of its source variant before it looks at the root. A
    /// fetch calls the hook with no lock held, so it can run a whole retirement of the source; an add calls it with its variant locks held, which a cleanup
    /// pass does not take, so it can run a pass that finishes an interrupted retirement of the source. Either way the caller then goes on with a marker
    /// that is no longer on disk, which is what the same thing running at this moment on another thread leaves it with.
    /// </summary>
    VariantAliasMarkerRead,

    /// <summary>
    /// A fetch of a variant, or an add, has found the source data file of an alias marker it read missing, and is about to read the variant's slot
    /// again. A test can change the slot in the hook, as a retirement that finished just before does, or leave it alone. Not raised when the same marker
    /// is found with its source still missing, which ends the reading: the slot is read once more than this step is raised, counting a read that an add
    /// made before it started reading the slot until steady.
    /// </summary>
    VariantAliasSourceMissing,

    /// <summary>
    /// A fetch of a file group has listed alias markers whose source data file is not in the same listing, at least one of them not in the listing before
    /// under the same name, and is about to list the group again. A test can change the group in the hook or leave it alone. Not raised when the fetch
    /// reports the dangling aliases instead of listing again, so the fetch lists the group once more than this step is raised.
    /// </summary>
    GroupAliasSourceMissing,
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
