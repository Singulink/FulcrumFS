namespace FulcrumFS;

/// <summary>
/// Specifies when file deletions issued through the repository take physical effect.
/// </summary>
public enum DeleteMode
{
    /// <summary>
    /// File deletions are performed immediately when the operation completes (or when a transaction containing the deletion commits). No delete marker is
    /// written and no grace period is observed.
    /// </summary>
    Immediate,

    /// <summary>
    /// File deletions are deferred: a delete marker is written when the operation completes (or when a transaction containing the deletion commits), and the
    /// physical deletion happens later when a <see cref="FileRepoCleaner"/> clean operation removes markers older than the configured delete delay. This is
    /// the default mode and is recommended to allow concurrent transactions to access deleted files, support recovery, and let scheduled backups run before
    /// files are physically removed.
    /// </summary>
    DeferredUntilClean,
}
