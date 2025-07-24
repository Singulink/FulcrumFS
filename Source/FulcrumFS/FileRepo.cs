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
    private const string DeleteMarkerExtension = ".del";
    private const string IndeterminateMarkerExtension = ".ind";

    internal static RecyclableMemoryStreamManager MemoryStreamManager { get; } = new();

    private IAbsoluteFilePath _lockFilePath;
    private FileStream? _lockStream;

    private readonly IAbsoluteDirectoryPath _dataDirectory;
    private readonly IAbsoluteDirectoryPath _tempDirectory;
    private readonly IAbsoluteDirectoryPath _cleanupDirectory;

    private readonly HashSet<Guid> _processingFileIds = [];

    private readonly KeyLocker<(Guid FileId, string? VariantId)> _fileSync = new();
    private readonly AsyncLock _stateSync = new();
    private readonly AsyncLock _cleanSync = new();

    private long _lastSuccessfulHealthCheck = long.MinValue;

    private bool _isDisposed;

    /// <summary>
    /// Gets the base directory for the file repository.
    /// </summary>
    public IAbsoluteDirectoryPath BaseDirectory { get; }

    /// <summary>
    /// Gets the time delay between when files are marked for deletion and when they are actually deleted from the repository.
    /// </summary>
    /// <remarks>
    /// See <see cref="FileRepoOptions.DeleteDelay"/> for more information on this setting.
    /// </remarks>
    public TimeSpan DeleteDelay { get; }

    /// <summary>
    /// Gets the time delay after which files are considered indeterminate if they are not successfully committed or rolled back.
    /// </summary>
    /// <remarks>
    /// See <see cref="FileRepoOptions.IndeterminateDelay"/> for more information on this setting.
    /// </remarks>
    public TimeSpan IndeterminateDelay { get; }

    /// <summary>
    /// Gets the interval at which a health check on the repo volume/directory is performed.
    /// </summary>
    /// <remarks>
    /// See <see cref="FileRepoOptions.HealthCheckInterval"/> for more information on this setting.
    /// </remarks>
    public TimeSpan HealthCheckInterval { get; }

    /// <summary>
    /// Gets the maximum time that operations will wait for successful I/O access to the repository before timing out or throwing the I/O exception.
    /// </summary>
    /// <remarks>
    /// See <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> for more information on this setting.
    /// </remarks>
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
        IndeterminateDelay = options.IndeterminateDelay;
        HealthCheckInterval = options.HealthCheckInterval;
        MaxAccessWaitOrRetryTime = options.MaxAccessWaitOrRetryTime;
    }

    [return: NotNullIfNotNull(nameof(extension))]
    internal static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return string.Empty;

        var file = FilePath.ParseRelative("f", PathFormat.Universal, PathOptions.None).WithExtension(extension);
        return file.Extension.ToLowerInvariant();
    }

    private static string? NormalizeVariantId(string? variantId)
    {
        if (string.IsNullOrEmpty(variantId))
            return null;

        if (variantId.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetter(c) && c is not ('-' or '_')))
            throw new ArgumentException("Variant ID must contain only ASCII letters, digits, hyphens and underscores.", nameof(variantId));

        return variantId.ToLowerInvariant();
    }

    /// <summary>
    /// Gets a unique entry name for a given file ID and variant ID which is used to name temp work directories or cleanup files associated with the file.
    /// </summary>
    private static string GetEntryName(FileId fileId, string? variantId) => variantId is null ? fileId.String : $"{fileId.String}-{variantId}";

    private static bool TryParseEntryName(string entryName, out (FileId FileId, string? VariantId) result)
    {
        int dashIndex = entryName.IndexOf('-');

        string fileIdString;
        string variantId;

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

        if (!FileId.TryParse(fileIdString, out var fileId))
        {
            result = default;
            return false;
        }

        result = (fileId, NormalizeVariantId(variantId));
        return true;
    }

    private IAbsoluteFilePath GetDeleteMarker(FileId fileId, string? variant)
    {
        string name = variant is null ? fileId.String : GetEntryName(fileId, variant);
        return _cleanupDirectory.CombineFile(name + DeleteMarkerExtension, PathOptions.None);
    }

    private IAbsoluteFilePath GetIndeterminateMarker(FileId fileId)
    {
        return _cleanupDirectory.CombineFile(fileId.String + IndeterminateMarkerExtension, PathOptions.None);
    }

    private IAbsoluteFilePath GetDataFile(FileId fileId, string extension, string? variantId = null)
    {
        string fileNamePart = variantId ?? "$main$";
        string fullFileName = fileNamePart + extension;
        return GetDataFileGroupDirectory(fileId).CombineFile(fullFileName, PathOptions.None);
    }

    private IAbsoluteFilePath? FindDataFile(FileId fileId, string? variantId = null)
    {
        string fileNamePart = variantId ?? "$main$";

        try
        {
            return GetDataFileGroupDirectory(fileId).GetChildFiles(fileNamePart + ".*").SingleOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private IAbsoluteDirectoryPath GetDataFileGroupDirectory(FileId fileId)
    {
        if (fileId.String.Length is not 32)
            throw new ArgumentException("File ID string must be 32 characters long.", nameof(fileId.String));

        var relativeDir = DirectoryPath
            .ParseRelative(fileId.String.AsSpan()[0..2], PathFormat.Universal, PathOptions.None)
            .CombineDirectory(fileId.String.AsSpan()[2..4], PathOptions.None)
            .CombineDirectory(fileId.String.AsSpan()[4..], PathOptions.None);

        return _dataDirectory.Combine(relativeDir);
    }

    private static async Task LogToMarkerAsync(IAbsoluteFilePath cleanupFile, string header, string message, bool ignoreErrors)
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
