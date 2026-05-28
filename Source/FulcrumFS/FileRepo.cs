using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
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

    private readonly IAbsoluteFilePath _infoFile;
    private readonly IAbsoluteFilePath _lockFile;
    private FileStream? _lockStream;
    private int _lockStreamSize;

    private readonly IAbsoluteDirectoryPath _filesDirectory;
    private readonly IAbsoluteDirectoryPath _tempDirectory;
    private readonly IAbsoluteDirectoryPath _cleanupDirectory;

    private readonly KeyLocker<(FileId FileId, string? VariantId)> _fileSync = new();
    private readonly AsyncLock _stateSync = new();

    private FileRepoCleaner? _cleaner;

    private long _lastSuccessfulHealthCheck = long.MinValue;
    private volatile bool _isDisposed;

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

        _infoFile = options.BaseDirectory.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None);
        _lockFile = options.BaseDirectory.CombineFile(FileRepoPaths.RepoLockFileName, PathOptions.None);
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
    /// Ensures that the file repository exists at the configured base directory, creating or repairing the required directory structure as necessary. May
    /// also acquire the repository lock for an instance session via a subsequent operation.
    /// </summary>
    /// <remarks>
    /// <para>If the base directory does not exist or is empty, it is created and initialized as a FulcrumFS repository.</para>
    /// <para>If the base directory contains an existing FulcrumFS repository (identified by the presence of the <see cref="FileRepoPaths.InfoFileName"/>
    /// marker file), any missing required subdirectories are restored. Foreign files and folders alongside the repository are left untouched.</para>
    /// <para>If the base directory exists, contains foreign content, and is not already a FulcrumFS repository, an exception is thrown rather than initializing
    /// the directory as a repository.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The base directory contains content that does not belong to a FulcrumFS repository.</exception>
    /// <exception cref="NotSupportedException">The repository was created by a newer incompatible version of FulcrumFS.</exception>
    /// <exception cref="InvalidDataException">The repository info file is malformed.</exception>
    public void EnsureCreated()
    {
        using (_stateSync.Lock())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            bool infoExists = _infoFile.State is EntryState.Exists;

            if (!infoExists)
            {
                // No marker file. Refuse to initialize a directory that already contains foreign content - this guards against accidentally pointing at the wrong
                // directory (e.g. a typo). An empty or non-existent directory is treated as a fresh repository location.

                var baseDirState = Options.BaseDirectory.State;

                if (baseDirState is EntryState.Exists)
                {
                    if (Options.BaseDirectory.GetChildEntries().Any())
                    {
                        throw new InvalidOperationException(
                            $"Cannot initialize a FulcrumFS repository at '{Options.BaseDirectory.PathDisplay}': the directory contains content that does not " +
                            $"belong to a repository. Repository directories must be empty or contain only repository-managed content on initial creation.");
                    }
                }
                else
                {
                    Options.BaseDirectory.Create();
                }

                // Write the info marker FIRST so a crash mid-creation still leaves the directory identifiable as a (partial) FulcrumFS repository, allowing the
                // next EnsureCreated call to enter the repair path and complete the structure.

                RepoInfoFile.Write(_infoFile);
            }
            else
            {
                // Existing repository - verify we're allowed full access before attempting to repair structure or perform any operations.
                RepoInfoFile.VerifyFullAccessSupported(_infoFile);
            }

            _filesDirectory.Create();
            _cleanupDirectory.Create();
        }
    }

    /// <summary>
    /// Ensures the file repository has been verified and the repository lock is acquired. The repository must already exist (call <see cref="EnsureCreated"/>
    /// first if needed).
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The configured base directory is not a FulcrumFS repository or is missing required structure.</exception>
    public void EnsureInitialized()
    {
        using (_stateSync.Lock())
            InitializeCore();
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

                void CheckHealth()
                {
                    // Use a field to store our size rather than querying it, as querying it is an extra syscall - we use SetLength rather than just reading
                    // Length as a "write" API could be more likely to fail if the file system is in a bad state than a "read" API.

                    _lockStreamSize = (_lockStreamSize + 1) % 10;
                    _lockStream.SetLength(_lockStreamSize);
                    _lastSuccessfulHealthCheck = Stopwatch.GetTimestamp();
                }

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
                        throw new TimeoutException("The operation timed out attempting to get I/O access to the repository.", elc.ResultException);

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// MUST BE CALLED WITHIN A STATE SYNC LOCK. Acquires the repository lock, verifies the repository structure exists, then prepares per-session state.
    /// Does not create the repository - call <see cref="EnsureCreated"/> first if needed.
    /// </summary>
    private void InitializeCore()
    {
        if (_lockStream is not null)
            return;

        InitializeCoreSlow();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void InitializeCoreSlow()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var lockStream = _lockFile.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

            try
            {
                VerifyRepoStructure();

                _tempDirectory.Delete(recursive: true);
                _tempDirectory.Create();
            }
            catch
            {
                lockStream.Dispose();
                throw;
            }

            _lockStream = lockStream;
            _lastSuccessfulHealthCheck = Stopwatch.GetTimestamp();
        }

        void VerifyRepoStructure()
        {
            if (_infoFile.State is not EntryState.Exists)
            {
                if (Options.BaseDirectory.State is not EntryState.Exists)
                    throw new DirectoryNotFoundException($"The file repository base directory '{Options.BaseDirectory.PathDisplay}' was not found.");

                throw new DirectoryNotFoundException(
                    $"The directory '{Options.BaseDirectory.PathDisplay}' is not an initialized FulcrumFS repository. " +
                    $"Call '{nameof(EnsureCreated)}' to initialize it.");
            }

            RepoInfoFile.VerifyFullAccessSupported(_infoFile);

            if (_filesDirectory.State is not EntryState.Exists || _cleanupDirectory.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The FulcrumFS repository at '{Options.BaseDirectory.PathDisplay}' is in an incomplete state. " +
                    $"Call '{nameof(EnsureCreated)}' to repair it.");
            }
        }
    }

    // TODO: Remove legacy migration in next preview release.

    /// <summary>
    /// Migrates a repository created by early pre-release legacy versions of FulcrumFS that used dot-prefixed <c>.temp</c> and <c>.cleanup</c> directory names
    /// to the current layout. Must be called before <see cref="EnsureCreated"/> or any other operation on hosts that may still have legacy repositories on
    /// disk. Safe to call on already-migrated or non-existent repositories - it is a no-op when no legacy artifacts are present.
    /// </summary>
    /// <remarks>
    /// This method exists only as a one-time migration aid and will be removed in a future release.
    /// </remarks>
    public void MigrateLegacyLayout()
    {
        using (_stateSync.Lock())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            const string legacyTempName = ".temp";
            const string legacyCleanupName = ".cleanup";

            var legacyTemp = Options.BaseDirectory.CombineDirectory(legacyTempName, PathOptions.None);
            var legacyCleanup = Options.BaseDirectory.CombineDirectory(legacyCleanupName, PathOptions.None);

            bool legacyTempExists = legacyTemp.State is EntryState.Exists;
            bool legacyCleanupExists = legacyCleanup.State is EntryState.Exists;

            if (!legacyTempExists && !legacyCleanupExists)
                return;

            // Legacy temp dir is transient - just delete if present.
            if (legacyTempExists)
                legacyTemp.Delete(recursive: true);

            // Legacy cleanup dir contains delete/indeterminate markers - rename to the new name, preserving contents.
            if (legacyCleanupExists && _cleanupDirectory.State is not EntryState.Exists)
            {
                // Strip the hidden attribute from the legacy directory so the new (non-hidden) layout is consistent after migration.
                try { legacyCleanup.Attributes &= ~FileAttributes.Hidden; }
                catch (IOException) { /* best-effort */ }

                Directory.Move(legacyCleanup.PathExport, _cleanupDirectory.PathExport);
            }

            // Legacy repositories pre-date the info marker file. Stamp one now so the migrated repo is identifiable as a v1 FulcrumFS repository and subsequent
            // EnsureCreated / open paths don't mistake the existing files/cleanup folders for foreign content.
            if (_infoFile.State is not EntryState.Exists)
                RepoInfoFile.Write(_infoFile);
        }
    }

    /// <summary>
    /// Gets a unique entry name for a given file ID and variant ID which is used to name temp work directories or cleanup files associated with the file.
    /// </summary>
    private static string GetFileIdAndVariantString(FileId fileId, string? variantId) => variantId is null ? fileId.ToString() : $"{fileId} {variantId}";

    internal static bool TryParseFileIdAndVariant(string entryName, [MaybeNullWhen(false)] out FileId fileId, out string? variantId)
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
