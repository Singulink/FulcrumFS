using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Singulink.Collections;

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
    private int _lockStreamSize;

    private readonly IAbsoluteDirectoryPath _filesDirectory;
    private readonly IAbsoluteDirectoryPath _tempDirectory;
    private readonly IAbsoluteDirectoryPath _cleanupDirectory;

    private readonly HashSet<FileId> _processingFiles = [];
    private readonly WeakCollection<FileRepoTransaction> _activeTransactions = [];

    private readonly KeyLocker<(FileId FileId, string? VariantId)> _fileSync = new();
    private readonly AsyncLock _stateSync = new();
    private readonly AsyncLock _cleanSync = new();

    private long _lastSuccessfulHealthCheck = long.MinValue;
    private bool _isDisposed;

    /// <summary>
    /// Gets the configuration options for the file repository. The returned instance is frozen and cannot be modified.
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
        if (options.BaseDirectory is null)
            throw new ArgumentException("Base directory must be set in options.", nameof(options));

        options.Freeze();
        Options = options;

        _lockFile = options.BaseDirectory.CombineFile(FileRepoPaths.LockFileName, PathOptions.None);
        _filesDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None);
        _tempDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.TempDirectoryName, PathOptions.None);
        _cleanupDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepo"/> class with the specified options.
    /// </summary>
    public FileRepo(IOptions<FileRepoOptions> options) : this(options.Value) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepo"/> class with the specified base directory and optional configuration action.
    /// </summary>
    /// <param name="baseDirectory">The base directory for the file repository. Must be an existing directory in the file system.</param>
    /// <param name="configure">An optional action that configures additional options.</param>
    public FileRepo(IAbsoluteDirectoryPath baseDirectory, Action<FileRepoOptions>? configure = null)
        : this(BuildOptions(baseDirectory, configure)) { }

    private static FileRepoOptions BuildOptions(IAbsoluteDirectoryPath baseDirectory, Action<FileRepoOptions>? configure)
    {
        var options = new FileRepoOptions(baseDirectory);
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Ensures that the file repository is initialized, creating necessary directories and acquiring locks.
    /// </summary>
    public void EnsureInitialized()
    {
        using (_stateSync.Lock())
        {
            InitializeCore();
        }
    }

    /// <summary>
    /// Disposes of the file repository, releasing any resources and locks held by it.
    /// </summary>
    public void Dispose()
    {
        using (_stateSync.Lock())
        {
            if (_lockStream is not null)
            {
                _lockStream.Dispose();
                _lockStream = null;
            }

            _isDisposed = true;
        }
    }

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        return EnsureInitializedAsync(forceHealthCheck: false, cancellationToken);
    }

    private async ValueTask EnsureInitializedAsync(bool forceHealthCheck, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long initStartTimestamp = Stopwatch.GetTimestamp();

        using (await _stateSync.LockAsync(Options.MaxAccessWaitOrRetryTime, cancellationToken))
        {
            if (_lockStream is null || forceHealthCheck || Stopwatch.GetElapsedTime(_lastSuccessfulHealthCheck) >= Options.HealthCheckInterval)
            {
                var elc = new ExceptionListCapture(ex => ex is IOException);

                if (_lockStream is not null && !elc.TryRun(CheckHealth))
                {
                    _lockStream.Dispose();
                    _lockStream = null;
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (elc.TryRun(InitializeCore))
                        return;

                    if (Stopwatch.GetElapsedTime(initStartTimestamp) > Options.MaxAccessWaitOrRetryTime)
                        throw new TimeoutException("The operation timed out attempting to get I/O access the repository.", elc.ResultException);

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        void CheckHealth()
        {
            // Use a variable to store our size rather than querying it, as querying it is an extra syscall - we use SetLength rather than just reading Length
            // as a "write" API could be more likely to fail if the file system is in a bad state than a "read" API.
            int newSize;
            lock (_lockStream) newSize = _lockStreamSize = (_lockStreamSize + 1) % 10;
            _lockStream.SetLength(newSize);

            _lastSuccessfulHealthCheck = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// MUST BE CALLED WITHIN A STATE SYNC LOCK.
    /// </summary>
    private void InitializeCore()
    {
        if (_lockStream is not null)
            return;

        InitializeCoreSlow();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void InitializeCoreSlow()
        {
            if (_isDisposed)
            {
                void Throw() => throw new ObjectDisposedException(nameof(FileRepo));
                Throw();
            }

            var lockStream = _lockFile.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

            try
            {
                _tempDirectory.Delete(recursive: true);
                _tempDirectory.Create();
                _tempDirectory.Attributes |= FileAttributes.Hidden;
                _cleanupDirectory.Create();
                _cleanupDirectory.Attributes |= FileAttributes.Hidden;
            }
            catch (IOException)
            {
                lockStream.Dispose();
                throw;
            }

            _lockStream = lockStream;
            _lastSuccessfulHealthCheck = Stopwatch.GetTimestamp();
        }
    }

    private IAbsoluteFilePath GetDeleteMarker(FileId fileId, string? variant)
    {
        string name = variant is null ? fileId.ToString() : GetFileIdAndVariantString(fileId, variant);
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

    /// <summary>
    /// Gets the directory where the main file and its variants are stored for the specified file ID.
    /// </summary>
    private IAbsoluteDirectoryPath GetFileDirectory(FileId fileId) => _filesDirectory.Combine(fileId.RelativeDirectory);

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

                    string entry =
                        $"=============== {header} ===============\r\n\r\n" +
                        $"Timestamp: {DateTimeOffset.Now}\r\n\r\n" +
                        $"{message}\r\n\r\n";

                    await using (sw.ConfigureAwait(false))
                        await sw.WriteLineAsync(entry).ConfigureAwait(false);
                }
            }
        }
        catch (IOException) when (opened || !markerRequired || cleanupFile.State is EntryState.Exists) { }
    }

    /// <summary>
    /// Gets a unique entry name for a given file ID and variant ID which is used to name temp work directories or cleanup files associated with the file.
    /// </summary>
    private static string GetFileIdAndVariantString(FileId fileId, string? variantId) => variantId is null ? fileId.ToString() : $"{fileId} {variantId}";

    private static bool TryParseFileIdAndVariant(string entryName, [MaybeNullWhen(false)] out FileId fileId, out string? variantId)
    {
        int separatorIndex = entryName.IndexOf(' ');

        if (separatorIndex < 0)
        {
            variantId = null;
            return FileId.TryParseUnsafe(entryName, out fileId);
        }

        var fileIdChars = entryName.AsSpan()[..separatorIndex];
        var variantIdChars = entryName.AsSpan()[(separatorIndex + 1)..];

        if (FileId.TryParse(fileIdChars, out fileId) && VariantId.IsValidAndNormalized(variantIdChars))
        {
            variantId = variantIdChars.ToString();
            return true;
        }

        fileId = null;
        variantId = null;
        return false;
    }
}
