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

    /// <summary>
    /// Main file deletions are deferred (same as <see cref="DeferredUntilClean"/>), but variant deletions take physical effect immediately. The deferred
    /// grace period for variants exists primarily so that backups capture variants in a consistent state alongside their main files; this mode skips that
    /// protection and is only appropriate when either:
    /// <list type="bullet">
    /// <item><description>
    /// Backups exclude variants entirely, for example because variants are always (re)materialized on demand via
    /// <see cref="FileRepo.GetOrAddVariantAsync(FileId, string, IFileProcessingPipelineSelector, CancellationToken)"/>, or are regenerated from main files as
    /// part of a backup restoration procedure.</description></item>
    /// <item><description>
    /// The repository is offline and already backed up, and the operator wants to "force delete" variants immediately to remediate a problem with previously
    /// generated variants.</description></item>
    /// </list>
    /// </summary>
    DeferredFilesOnly,
}
