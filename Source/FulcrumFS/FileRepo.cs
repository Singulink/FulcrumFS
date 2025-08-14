using Microsoft.IO;

namespace FulcrumFS;

/// <summary>
/// Represents a transactional file repository.
/// </summary>
public sealed partial class FileRepo : IDisposable
{
    /// <summary>
    /// Gets the singleton instance of the <see cref="RecyclableMemoryStreamManager"/> used for managing MemoryStream memory. Internal use only.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static RecyclableMemoryStreamManager MemoryStreamManager { get; } = new();

    private IAbsoluteFilePath _lockFile;
    private FileStream? _lockStream;

    private readonly IAbsoluteDirectoryPath _filesDirectory;
    private readonly IAbsoluteDirectoryPath _tempDirectory;
    private readonly IAbsoluteDirectoryPath _cleanupDirectory;

    private readonly HashSet<Guid> _processingFileIds = [];

    private readonly KeyLocker<(Guid FileId, string? VariantId)> _fileSync = new();
    private readonly AsyncLock _stateSync = new();
    private readonly AsyncLock _cleanSync = new();

    private long _lastSuccessfulHealthCheck = long.MinValue;
    private bool _isDisposed;

    /// <summary>
    /// Gets the configuration options for the file repository.
    /// </summary>
    public FileRepoOptions Options { get; }

    /// <summary>
    /// Gets the directory where files are stored.
    /// </summary>
    public IAbsoluteDirectoryPath FilesDirectory => _filesDirectory;

    /// <summary>
    /// Occurs when a commit operation fails. The handler can be used to log errors or perform custom error handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler receives an <see cref="Exception"/> object that describes the error that occurred. If multiple errors occurred, the exception will be of
    /// type <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not thrown automatically for transaction commit failures since added files are still accessible after the commit fails (they are just
    /// marked as being in an indeterminate state). The handler can throw an exception if throwing behavior is desired.</para>
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
    /// just marked as being in an indeterminate state). The handler can throw an exception if throwing behavior is desired.</para>
    /// </remarks>
    public event Func<Exception, Task>? RollbackFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepo"/> class with the specified options.
    /// </summary>
    public FileRepo(FileRepoOptions options)
    {
        Options = options;

        _lockFile = options.BaseDirectory.CombineFile(FileRepoPaths.LockFileName, PathOptions.None);
        _filesDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None);
        _tempDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.TempDirectoryName, PathOptions.None);
        _cleanupDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);
    }

    /// <summary>
    /// Gets a unique entry name for a given file ID and variant ID which is used to name temp work directories or cleanup files associated with the file.
    /// </summary>
    private static string GetEntryName(FileId fileId, string? variantId) => variantId is null ? fileId.ToString() : $"{fileId} {variantId}";

    private static bool TryGetFileIdStringAndVariant(string entryName, [MaybeNullWhen(false)] out FileId fileId, out string? variantId)
    {
        int dashIndex = entryName.IndexOf(' ');
        string fileIdString;

        if (dashIndex < 0)
        {
            fileIdString = entryName;
            variantId = null;
        }
        else
        {
            fileIdString = entryName[..dashIndex];
            variantId = entryName[(dashIndex + 1)..];
        }

        if (FileId.TryParse(fileIdString, out fileId))
            return true;

        variantId = null;
        return false;
    }

    private IAbsoluteFilePath GetDeleteMarker(FileId fileId, string? variant)
    {
        string name = variant is null ? fileId.ToString() : GetEntryName(fileId, variant);
        return _cleanupDirectory.CombineFile(name + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
    }

    private IAbsoluteFilePath GetIndeterminateMarker(FileId fileId)
    {
        return _cleanupDirectory.CombineFile(fileId.ToString() + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
    }

    private IAbsoluteFilePath GetDataFile(FileId fileId, string extension, string? variantId = null)
    {
        string fileNamePart = variantId ?? FileRepoPaths.MainFileName;
        string fullFileName = fileNamePart + extension;
        return GetFileDirectory(fileId).CombineFile(fullFileName, PathOptions.None);
    }

    private IAbsoluteFilePath? FindDataFile(FileId fileId, string? variantId = null)
    {
        string fileNamePart = variantId ?? FileRepoPaths.MainFileName;

        try
        {
            return GetFileDirectory(fileId).GetChildFiles(fileNamePart + ".*").SingleOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private IAbsoluteDirectoryPath GetFileDirectory(FileId fileId) => _filesDirectory.Combine(fileId.GetRelativeDirectory());

    private async Task LogToMarkerAsync<T>(IAbsoluteFilePath cleanupFile, string header, T message, bool markerRequired)
    {
        bool opened = false;

        try
        {
            var stream = cleanupFile.OpenAsyncStream(FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            opened = true;

            await using (stream.ConfigureAwait(false))
            {
                if (Options.MarkerFileLogging is LoggingMode.HumanReadable)
                {
                    var sw = new StreamWriter(stream, leaveOpen: true);

                    await using (sw.ConfigureAwait(false))
                    {
                        await sw.WriteLineAsync(
                        $"=============== {header} ===============\r\n\r\n" +
                        $"Timestamp: {DateTimeOffset.Now}\r\n\r\n" +
                        $"{message}\r\n\r\n").ConfigureAwait(false);
                    }
                }
            }
        }
        catch (IOException) when (opened || !markerRequired || cleanupFile.State is EntryState.Exists) { }
    }
}
