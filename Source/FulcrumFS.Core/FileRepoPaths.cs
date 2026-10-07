namespace FulcrumFS;

/// <summary>
/// Contains path constants used by file repositories.
/// </summary>
public static class FileRepoPaths
{
    /// <summary>
    /// The name of the repository information / marker file. Its presence at the base directory identifies a directory as a FulcrumFS repository and stores
    /// repository metadata such as the format version.
    /// </summary>
    public const string InfoFileName = "fulcrumfs.info";

    /// <summary>
    /// The name of the lock file held by an active <c>FileRepo</c> instance at the repository base directory. Ensures only a single instance is operating on
    /// the repository at a time and is also used as the target of periodic health checks.
    /// </summary>
    public const string RepoLockFileName = "fulcrumfs.lock";

    /// <summary>
    /// The directory name where files are stored in the repository.
    /// </summary>
    public const string FilesDirectoryName = "files";

    /// <summary>
    /// The file name used for main files in the repository.
    /// </summary>
    public const string MainFileName = "$main";

    /// <summary>
    /// The name of the directory where temporary files are stored during processing.
    /// </summary>
    public const string TempDirectoryName = "temp";

    /// <summary>
    /// The name of the directory used for cleanup operations in the repository.
    /// </summary>
    public const string CleanupDirectoryName = "cleanup";

    /// <summary>
    /// The name of the lock file held during a clean operation, placed inside the <see cref="CleanupDirectoryName"/> subdirectory to serialize concurrent
    /// <c>FileRepoCleaner</c> operations.
    /// </summary>
    public const string CleanupLockFileName = "cleanup.lock";

    /// <summary>
    /// The file extension used for delete markers in the repository, which go in the cleanup directory.
    /// </summary>
    public const string DeleteMarkerExtension = ".del";

    /// <summary>
    /// The file extension used for indeterminate markers in the repository, which go in the cleanup directory.
    /// </summary>
    public const string IndeterminateMarkerExtension = ".ind";

    /// <summary>
    /// The file extension used for staged delete journals, which go in the cleanup directory. A staged journal, named <c>{journalId}.staged</c>, lists the
    /// files a staged transaction is about to delete; the transaction holds it open until it commits or rolls back, and a staged journal nobody holds is
    /// indeterminate for its files. An ordinary transaction that commits several deletions writes its journal under this name too, holding it open until it
    /// has been renamed to committed.
    /// </summary>
    public const string StagedJournalExtension = ".staged";

    /// <summary>
    /// The file extension used for committed delete journals, which go in the cleanup directory. A staged journal is renamed to <c>{journalId}.journal</c>
    /// when its transaction commits and is then the delete record of every file it lists.
    /// </summary>
    public const string CommittedJournalExtension = ".journal";

    /// <summary>
    /// The file extension used for alias markers in the repository, which go in a file group's directory alongside data files. An alias marker stands in for a
    /// variant whose pipeline produced no changes (see <c>FileProcessingPipeline.SkipWhenSourceUnchanged</c>); the marker filename encodes a pointer to the
    /// resolved source data file in the form <c>{variantId}.{sourceVariantId}.{sourceExt}.alias</c>, where <c>sourceVariantId</c> is either
    /// a normalized variant ID or the literal <see cref="MainFileName"/> sentinel (<c>$main</c>) and <c>sourceExt</c> is the data extension without a leading
    /// dot. Aliases always point to real data files (never to other aliases) so resolution is a single direct lookup.
    /// </summary>
    public const string AliasMarkerExtension = ".alias";

    /// <summary>
    /// The file extension used for rebase markers in the repository, which go in a file group's directory alongside data files. A rebase marker records the
    /// in-progress promotion of an alias dependent to a standalone data file while its source variant is being retired with surviving dependents; the marker
    /// filename encodes the source and chosen variant IDs in the form <c>{sourceVariantId}.{chosenVariantId}.rebase</c>. Used by the cleaner to
    /// deterministically resume a rebase operation after a crash.
    /// </summary>
    public const string RebaseMarkerExtension = ".rebase";
}
