using System.ComponentModel;
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
    private const string MainFileName = "$main$";
    private const string DeleteMarkerExtension = ".del";
    private const string IndeterminateMarkerExtension = ".ind";

    /// <summary>
    /// Gets the singleton instance of the <see cref="RecyclableMemoryStreamManager"/> used for managing MemoryStream memory. Internal use only.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static RecyclableMemoryStreamManager MemoryStreamManager { get; } = new();

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
    /// Gets the configuration options for the file repository.
    /// </summary>
    public FileRepoOptions Options { get; }

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
        Options = options;

        _lockFilePath = options.BaseDirectory.CombineFile(".lock", PathOptions.None);
        _dataDirectory = options.BaseDirectory.CombineDirectory("Data", PathOptions.None);
        _tempDirectory = options.BaseDirectory.CombineDirectory(".temp", PathOptions.None);
        _cleanupDirectory = options.BaseDirectory.CombineDirectory(".cleanup", PathOptions.None);
    }

    private static string NormalizeVariantId(string variantId)
    {
        if (variantId.Length is 0)
            throw new ArgumentException("Variant ID cannot be empty.", nameof(variantId));

        if (variantId.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetter(c) && c is not ('-' or '_')))
            throw new ArgumentException("Variant ID must contain only ASCII letters, digits, hyphens and underscores.", nameof(variantId));

        return variantId.ToLowerInvariant();
    }

    /// <summary>
    /// Gets a unique entry name for a given file ID and variant ID which is used to name temp work directories or cleanup files associated with the file.
    /// </summary>
    private static string GetEntryName(FileId fileId, string? variantId) => variantId is null ? fileId.String : $"{fileId.String} {variantId}";

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

        fileIdString = null;
        variantId = null;

        return false;
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
        string fileNamePart = variantId ?? MainFileName;
        string fullFileName = fileNamePart + extension;
        return GetDataFileGroupDirectory(fileId).CombineFile(fullFileName, PathOptions.None);
    }

    private IAbsoluteFilePath? FindDataFile(FileId fileId, string? variantId = null)
    {
        string fileNamePart = variantId ?? MainFileName;

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
        // Length is 32 hex chars + 4 hyphens
        Debug.Assert(fileId.String.Length is 36, "Unexpected file ID length.");

        var fileIdSpan = fileId.String.AsSpan();

        var relativeDir = DirectoryPath
            .ParseRelative(fileIdSpan[9..11], PathFormat.Universal, PathOptions.None)
            .CombineDirectory(fileIdSpan[11..13], PathOptions.None)
            .CombineDirectory(fileId.String, PathOptions.None);

        return _dataDirectory.Combine(relativeDir);
    }

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
                        $"Timestamp (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}\r\n\r\n" +
                        $"{message}\r\n\r\n").ConfigureAwait(false);
                    }
                }
            }
        }
        catch (IOException) when (opened || !markerRequired || cleanupFile.State is EntryState.Exists) { }
    }
}
