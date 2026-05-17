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
}
