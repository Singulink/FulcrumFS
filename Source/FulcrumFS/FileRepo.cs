using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.IO;
using Nito.AsyncEx;
using Singulink.Threading;

namespace FulcrumFS;

/// <summary>
/// Represents a transactional file repository.
/// </summary>
public sealed partial class FileRepo : IDisposable
{
    internal static RecyclableMemoryStreamManager MemoryStreamManager { get; } = new();

    private IAbsoluteFilePath _lockFilePath;
    private FileStream? _lockStream;

    private readonly IAbsoluteDirectoryPath _dataDirectory;
    private readonly IAbsoluteDirectoryPath _tempDirectory;
    private readonly IAbsoluteDirectoryPath _cleanupDirectory;

    private readonly HashSet<Guid> _processingFileIds = [];

    private readonly KeyLocker<(Guid FileId, string? VariantId)> _fileSync = new();
    private readonly AsyncLock _stateSync = new();

    private long _lastSuccessfulHealthCheck = long.MinValue;

    private bool _isDisposed;

    /// <summary>
    /// Gets the base directory for the file repository.
    /// </summary>
    public IAbsoluteDirectoryPath BaseDirectory { get; }

    /// <summary>
    /// Gets the time delay between when files are marked for deletion and when they are actually deleted from the repository. Default is <see
    /// cref="TimeSpan.Zero"/>, indicating immediate deletion upon transaction commit.
    /// </summary>
    public TimeSpan DeleteDelay { get; }

    /// <summary>
    /// Gets the interval at which a health check on the repo volume/directory is performed. Default is 15 seconds.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; }

    /// <summary>
    /// Gets the maximum time that operations will wait for successful I/O access to the repository before timing out or throwing the I/O exception.
    /// </summary>
    public TimeSpan MaxAccessWaitOrRetryTime { get; }

    /// <summary>
    /// Occurs when a commit operation fails. The handler can be used to log errors or perform custom error handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler receives an <see cref="Exception"/> object that describes the error that occurred. If multiple errors occurred, the exception will be of
    /// type <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not thrown automatically for transaction commit failures since added files are still accessible after the commit fails (they are just
    /// marked as being in an indeterminate state). The handler can throw an exception if that behavior is desired.</para>
    /// </remarks>
    public event Func<Exception, Task>? CommitFailed;

    /// <summary>
    /// Occurs when a rollback operation fails. The handler can be used to log errors or perform custom error handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event handler receives an <see cref="Exception"/> object that describes the error that occurred. If multiple errors occurred, the exception will be
    /// of type <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not thrown automatically for transaction rollback failures since deleted files are still accessible after the rollback fails (they are
    /// just marked as being in an indeterminate state). The handler can throw an exception if that behavior is desired.</para>
    /// </remarks>
    public event Func<Exception, Task>? RollbackFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepo"/> class with the specified options.
    /// </summary>
    public FileRepo(FileRepoOptions options)
    {
        BaseDirectory = options.BaseDirectory;
        _lockFilePath = BaseDirectory.CombineFile(".lock", PathOptions.None);

        _dataDirectory = BaseDirectory.CombineDirectory("Data", PathOptions.None);
        _tempDirectory = BaseDirectory.CombineDirectory(".temp", PathOptions.None);
        _cleanupDirectory = BaseDirectory.CombineDirectory(".cleanup", PathOptions.None);

        DeleteDelay = options.DeleteDelay;
        HealthCheckInterval = options.HealthCheckInterval;
        MaxAccessWaitOrRetryTime = options.MaxAccessWaitOrRetryTime;
    }

    /// <summary>
    /// Cleans up the repository by checking for files that are marked for deletion (or which could not be previously deleted because they were in use) and
    /// removes them if <see cref="DeleteDelay"/> has passed since they were marked for deletion. Optionally also accepts a function to update the state of
    /// indeterminate files.
    /// </summary>
    public async Task CleanAsync(Func<Guid, Task<bool>>? getIndeterminateStateCallback, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(forceHealthCheck: true).ConfigureAwait(false);

        foreach (var deleteFileInfo in _cleanupDirectory.GetChildFilesInfo("*.delete"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deleteFileInfo.CreationTimeUtc + DeleteDelay < DateTime.UtcNow)
                continue;

            string fileIdString = deleteFileInfo.Path.NameWithoutExtension;
            var dataFileGroupDir = GetDataFileGroupDirectory(fileIdString);
            var indeterminateFile = GetCleanupIndeterminateFile(fileIdString);

            if (dataFileGroupDir.TryDelete(recursive: true, out var ex) && indeterminateFile.TryDelete(out ex))
                deleteFileInfo.Path.TryDelete(out ex);

            if (ex is not null)
                await WriteCleanupRecordAsync(deleteFileInfo.Path, "DELETE ATTEMPT FAILED", ex.ToString(), ignoreErrors: true).ConfigureAwait(false);
        }
    }

    [return: NotNullIfNotNull(nameof(extension))]
    internal static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return string.Empty;

        var file = FilePath.ParseRelative("f", PathFormat.Universal, PathOptions.None).WithExtension(extension);
        return file.Extension.ToLowerInvariant();
    }

    private static string GetFileIdString(Guid fileId) => fileId.ToString("N");

    private static string? NormalizeVariantId(string? variantId)
    {
        if (string.IsNullOrEmpty(variantId))
            return null;

        if (variantId.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetter(c) && c is not ('-' or '_')))
            throw new ArgumentException("Variant ID must contain only ASCII letters, digits, hyphens and underscores.", nameof(variantId));

        return variantId.ToLowerInvariant();
    }

    private static void ValidateFileId(Guid fileId)
    {
        if (fileId == Guid.Empty)
            throw new ArgumentException("Invalid file ID.", nameof(fileId));
    }

    private static string GetVariantEntryName(string fileIdString, string variantId) => $"{fileIdString}-{variantId}";

    private IAbsoluteFilePath GetCleanupDeleteFile(string fileIdString, string? variant)
    {
        string name = variant is null ? fileIdString : GetVariantEntryName(fileIdString, variant);
        return _cleanupDirectory.CombineFile(name + ".del", PathOptions.None);
    }

    private IAbsoluteFilePath GetCleanupIndeterminateFile(string fileIdString)
    {
        return _cleanupDirectory.CombineFile(fileIdString + ".ind", PathOptions.None);
    }

    private IAbsoluteFilePath GetDataFile(string fileIdString, string extension, string? variantId = null)
    {
        string fileNamePart = variantId ?? "$";
        string fullFileName = fileNamePart + extension;
        return GetDataFileGroupDirectory(fileIdString).CombineFile(fullFileName, PathOptions.None);
    }

    private IAbsoluteFilePath? FindDataFile(string fileIdString, string? variantId = null)
    {
        string fileNamePart = variantId ?? "$";

        try
        {
            return GetDataFileGroupDirectory(fileIdString).GetChildFiles(fileNamePart + ".*").SingleOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private IAbsoluteDirectoryPath GetDataFileGroupDirectory(string fileIdString)
    {
        var relativeDir = DirectoryPath
            .ParseRelative(fileIdString.AsSpan()[0..2], PathFormat.Universal, PathOptions.None)
            .CombineDirectory(fileIdString.AsSpan()[2..5], PathOptions.None)
            .CombineDirectory(fileIdString.AsSpan()[5..], PathOptions.None);

        return _dataDirectory.Combine(relativeDir);
    }

    private static async Task WriteCleanupRecordAsync(IAbsoluteFilePath cleanupFile, string header, string message, bool ignoreErrors)
    {
        try
        {
            var stream = cleanupFile.OpenAsyncStream(FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            var sw = new StreamWriter(stream, leaveOpen: true);

            await using (stream.ConfigureAwait(false))
            await using (sw.ConfigureAwait(false))
            {
                await sw.WriteLineAsync(
                $"=============== {header} ===============\r\n\r\n" +
                $"Timestamp (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}\r\n\r\n" +
                $"{message}\r\n\r\n").ConfigureAwait(false);
            }
        }
        catch (IOException) when (ignoreErrors) { }
    }
}
