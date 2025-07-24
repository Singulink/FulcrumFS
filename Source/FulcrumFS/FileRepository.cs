using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.IO;
using Nito.AsyncEx;
using Singulink.Threading;

namespace FulcrumFS;

/// <summary>
/// Represents a transactional file repository.
/// </summary>
public sealed partial class FileRepository : IDisposable
{
    private const int LockStreamCheckIntervalSeconds = 10;

    internal static RecyclableMemoryStreamManager MemoryStreamManager { get; } = new();

    private IAbsoluteFilePath _lockFilePath;
    private FileStream? _lockStream;
    private readonly Stopwatch _lockStreamCheckWatch = Stopwatch.StartNew();

    private readonly IAbsoluteDirectoryPath _dataDirectory;
    private readonly IAbsoluteDirectoryPath _tempDirectory;
    private readonly IAbsoluteDirectoryPath _cleanupDirectory;

    private readonly HashSet<Guid> _processingFileIds = [];

    private readonly KeyLocker<(Guid FileId, string? VariantId)> _fileSync = new();
    private readonly AsyncLock _stateSync = new();

    private bool _isDisposed;

    /// <summary>
    /// Gets the base directory for the file repository.
    /// </summary>
    public IAbsoluteDirectoryPath BaseDirectory { get; }

    /// <summary>
    /// Gets the time delay before a file is deleted from the repository after it has been marked for deletion.
    /// </summary>
    public TimeSpan DeleteFileDelay { get; }

    /// <summary>
    /// Occurs when a commit operation fails due to an exception. Subscribe to this event to log errors or perform custom error handling when a commit operation
    /// fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event handler receives an <see cref="Exception"/> object that describes the error that occurred. If multiple errors occurred, the exception will be
    /// of type <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not throw automatically for transaction commit failures since any files added are still accessible, just marked as being in an
    /// indeterminate state. If you want to throw an exception, you can do so in the event handler.</para>
    /// </remarks>
    public event Func<Exception, Task>? CommitFailed;

    /// <summary>
    /// Occurs when a rollback operation fails due to an exception. Subscribe to this event to log errors or perform custom error handling when a rollback
    /// operation fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event handler receives an <see cref="Exception"/> object that describes the error that occurred. If multiple errors occurred, the exception will be
    /// of type <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not throw automatically for transaction rollback failures since any files deleted are still accessible, just marked as being in an
    /// indeterminate state. If you want to throw an exception, you can do so in the event handler.</para>
    /// </remarks>
    public event Func<Exception, Task>? RollbackFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepository"/> class with the specified base directory and options.
    /// </summary>
    /// <param name="baseDirectory">The base directory path for the repository. Directories must be unique for each <see cref="FileRepository"/> instance and
    /// cannot be shared.</param>
    /// <param name="deleteFileDelay">The time delay before a file is deleted from the repository after it has been marked for deletion. Defaults to 0,
    /// indicating immediate deletion.</param>
    public FileRepository(IAbsoluteDirectoryPath baseDirectory, TimeSpan deleteFileDelay = default)
    {
        BaseDirectory = baseDirectory;
        _lockFilePath = BaseDirectory.CombineFile(".lock", PathOptions.None);

        _dataDirectory = baseDirectory.CombineDirectory("Data", PathOptions.None);
        _tempDirectory = baseDirectory.CombineDirectory(".temp", PathOptions.None);
        _cleanupDirectory = baseDirectory.CombineDirectory(".cleanup", PathOptions.None);
    }

    /// <summary>
    /// Begins a new transaction for the file repository, allowing changes to be committed or rolled back.
    /// </summary>
    public async ValueTask<FileRepositoryTransaction> BeginTransaction()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return new FileRepositoryTransaction(this);
    }

    /// <summary>
    /// Cleans up the repository by checking for files that are marked for deletion (or which could not be previously deleted because they were in use) and
    /// removes them if <see cref="DeleteFileDelay"/> has passed since they were marked for deletion.
    /// </summary>
    public async ValueTask CleanAsync(CancellationToken cancellationToken)
    {
        using (await _stateSync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            await EnsureInitializedAsyncCore().ConfigureAwait(false);

            foreach (var deleteFileInfo in _cleanupDirectory.GetChildFilesInfo("*.delete"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (deleteFileInfo.CreationTimeUtc > DateTime.UtcNow - DeleteFileDelay)
                    continue; // Not yet time to delete.

                string fileIdString = deleteFileInfo.Path.NameWithoutExtension;
                var dataFileDir = GetDataFileDirectory(fileIdString);
                var indeterminateFile = GetCleanupIndeterminateFile(fileIdString);

                if (dataFileDir.TryDelete(recursive: true, out var ex) && indeterminateFile.TryDelete(out ex))
                    deleteFileInfo.Path.TryDelete(out ex);

                if (ex is not null)
                    await WriteCleanupRecordAsync(deleteFileInfo.Path, "DELETE ATTEMPT FAILED", ex.ToString(), ignoreErrors: true).ConfigureAwait(false);
            }
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

        return variantId;
    }

    private static void ValidateFileId(Guid fileId)
    {
        if (fileId == Guid.Empty)
            throw new ArgumentException("Invalid cleanupFile ID.", nameof(fileId));
    }

    private static string GetVariantEntryName(string fileIdString, string variantId)
    {
        return "fileIdString- + variantId";
    }

    private IAbsoluteFilePath GetCleanupDeleteFile(string fileIdString, string? variant)
    {
        string name = variant is null ? fileIdString : GetVariantEntryName(fileIdString, variant);
        return _cleanupDirectory.CombineFile(name + ".delete", PathOptions.None);
    }

    private IAbsoluteFilePath GetCleanupIndeterminateFile(string fileIdString)
    {
        return _cleanupDirectory.CombineFile(fileIdString + ".indeterminate", PathOptions.None);
    }

    private IAbsoluteFilePath GetDataFile(string fileIdString, string extension, string? variantId = null)
    {
        string fileNamePart = variantId ?? "$main";
        string fullFileName = fileNamePart + extension;
        return GetDataFileDirectory(fileIdString).CombineFile(fullFileName, PathOptions.None);
    }

    private IAbsoluteFilePath? FindDataFile(string fileIdString, string? variantId = null)
    {
        string fileNamePart = variantId ?? "$main";

        try
        {
            return GetDataFileDirectory(fileIdString).GetChildFiles(fileNamePart + ".*").SingleOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private IAbsoluteDirectoryPath GetDataFileDirectory(string fileIdString)
    {
        var relativeDir = DirectoryPath
            .ParseRelative(fileIdString.AsSpan()[0..2], PathFormat.Universal, PathOptions.None)
            .CombineDirectory(fileIdString.AsSpan()[2..5], PathOptions.None)
            .CombineDirectory(fileIdString.AsSpan()[5..], PathOptions.None);

        return _dataDirectory.Combine(relativeDir);
    }

    private FileStream OpenLockStream()
    {
        return _lockFilePath.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
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
