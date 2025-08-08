namespace FulcrumFS;

/// <summary>
/// Contains path constants used by file repositories.
/// </summary>
public static class FileRepoPath
{
    /// <summary>
    /// The file name used for main files in the repository.
    /// </summary>
    public const string MainFileName = "$main$";

    /// <summary>
    /// The directory name where files are stored in the repository.
    /// </summary>
    public const string FilesDirectoryName = "files";

    /// <summary>
    /// The name of the directory where temporary files are stored during processing.
    /// </summary>
    public const string TempDirectoryName = ".temp";

    /// <summary>
    /// The name of the directory used for cleanup operations in the repository.
    /// </summary>
    public const string CleanupDirectoryName = ".cleanup";

    /// <summary>
    /// The file extension used for delete markers in the repository, which go in the cleanup directory.
    /// </summary>
    public const string DeleteMarkerExtension = ".del";

    /// <summary>
    /// The file extension used for indeterminate markers in the repository, which go in the cleanup directory.
    /// </summary>
    public const string IndeterminateMarkerExtension = ".ind";

    /// <summary>
    /// The name of the lock file used to prevent concurrent access to the repository.
    /// </summary>
    public const string LockFileName = ".lock";
}
