using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Singulink.Enums;

namespace FulcrumFS;

/// <summary>
/// Represents a transactional file repository stored in a local directory.
/// </summary>
/// <remarks>
/// Code that only uses a repository, rather than creating and configuring it, can depend on <see cref="IFileRepo"/> instead, so that another implementation
/// can replace this one. The members that return local types, such as <see cref="RepoFileInfo"/> with its path, implement the interface members explicitly.
/// </remarks>
public sealed partial class FileRepo : IFileRepo, IDisposable
{
    private readonly IAbsoluteFilePath _infoFile;
    private readonly IAbsoluteFilePath _lockFile;
    private volatile FileStream? _lockStream;

    // Health checks run on a timer while the repository is being accessed (so a slow volume never blocks operations) and stop once it has been idle
    // for the interval (so an idle repository uses no IOPS); the first access after that probes inline and re-arms the timer. _probeSync serializes probe
    // writes on the lock stream.
    private readonly Lock _probeSync = new();
    private Timer? _healthCheckTimer;
    private volatile bool _healthChecksActive;
    private long _lastAccessTimestamp; // Accessed via Volatile.Read/Write, which are atomic for 64-bit values on all platforms (Interlocked-based on 32-bit).
    private byte _probeValue;

    private readonly RepoFileSystem _fs;
    private readonly IAbsoluteDirectoryPath _tempDirectory;

    // Whether this instance has emptied the temp directory, which it does once, at its first successful initialization (see InitializeCore). Read and
    // written within the state sync lock.
    private bool _isTempDirectoryCleaned;

    private readonly KeyLocker<(FileId FileId, string? VariantId)> _fileSync = new();
    private readonly AsyncLock _stateSync = new();

    // Sequential IDs only need to be unique and ordered within a repository, so each repository has its own generator instead of contending for the shared one.
    private readonly FileIdGenerator _idGenerator = new();

    private FileRepoCleaner? _cleaner;

    // The repository's format version as last read from its info file at initialization, or as upgraded by this instance (0 until then). Re-read at every
    // initialization; while the repository lock is held nobody else can upgrade the repository, so the value is current for as long as it is held.
    private volatile int _formatVersion;

    // A read-only instance holds no lock stream, so this marks it initialized instead. It is verified once and never re-initialized.
    private volatile bool _isReadOnlyInitialized;

    private volatile bool _isDisposed;

    /// <summary>
    /// Gets the configuration options for the file repository. The returned instance is frozen and cannot be modified.
    /// </summary>
    public FileRepoOptions Options { get; }

    /// <summary>
    /// Gets a value indicating whether the repository was opened read-only, as configured by <see cref="FileRepoOptions.ReadOnly"/>.
    /// </summary>
    public bool IsReadOnly => Options.ReadOnly;

    /// <summary>
    /// Gets the format version that the repository actually has, as read from its info file when this instance opened the repository or written to it by an
    /// upgrade at that point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is a specific version, never <see cref="RepoFormatVersion.Latest"/>. Once the repository is open it is at least
    /// <see cref="FileRepoOptions.MinimumFormatVersion"/>, and it is higher when the repository already had a newer version, which is not changed. In
    /// <see cref="FileRepoOptions.ReadOnly"/> mode, which never upgrades, it can also be lower than the option, or a version this library only supports for
    /// reading, which is then not a defined <see cref="RepoFormatVersion"/> value.</para>
    /// <para>
    /// On a version 1 repository, transactions record their deletions with per-file markers instead of a delete journal, with the same behavior.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The repository has not been opened yet: neither <see cref="EnsureCreated"/> nor
    /// <see cref="EnsureInitialized"/> has returned and no operation has run.</exception>
    public RepoFormatVersion FormatVersion
    {
        get {
            int formatVersion = _formatVersion;

            if (formatVersion is 0)
            {
                throw new InvalidOperationException(
                    $"The repository format version is not known until the repository has been opened. Call '{nameof(EnsureCreated)}' or " +
                    $"'{nameof(EnsureInitialized)}' first.");
            }

            return (RepoFormatVersion)formatVersion;
        }
    }

    /// <summary>
    /// Gets a value indicating whether background health checks are currently running (test-only). They run while the repository is being accessed and
    /// stop once it has been idle for <see cref="FileRepoOptions.HealthCheckInterval"/>.
    /// </summary>
    internal bool HealthChecksActive => _healthChecksActive;

    /// <summary>
    /// Gets the directory where files are stored.
    /// </summary>
    public IAbsoluteDirectoryPath FilesDirectory => _fs.FilesDirectory;

    /// <summary>
    /// Gets the mode in which deletions take physical effect, as configured by <see cref="FileRepoOptions.DeleteMode"/>.
    /// </summary>
    public DeleteMode DeleteMode => Options.DeleteMode;

    /// <summary>
    /// Gets the algorithm the repository uses to generate file IDs, as configured by <see cref="FileRepoOptions.FileIdMode"/>.
    /// </summary>
    public FileIdMode FileIdMode => Options.FileIdMode;

    /// <summary>
    /// Occurs when a transaction-completion step (commit or rollback) fails. The handler receives a <see cref="RepoTransactionCompletionFailureInfo"/> carrying
    /// the operation that failed and the underlying exception, and can be used to log errors or perform custom error handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This event fires only for failures of the final commit or rollback step itself, not for in-flight exceptions raised while operating on the
    /// transaction (validation errors, processor failures, etc., which surface directly to the calling code).</para>
    /// <para>
    /// If multiple errors occurred during the completion step, <see cref="RepoTransactionCompletionFailureInfo.Exception"/> will be an
    /// <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not thrown automatically for completion-step failures since the affected files (added during commit or scheduled for deletion during
    /// rollback) remain accessible afterwards; they are merely left in an indeterminate state pending repair by the next cleaner pass. The handler can
    /// throw an exception if throwing behavior is desired.</para>
    /// <para>
    /// The event is raised once the commit or rollback has done all it can, and the handlers are awaited in turn. An exception that a handler throws
    /// therefore propagates from the commit or rollback call after the fact and undoes nothing, and it keeps the handlers subscribed after it from being
    /// called. A caller that sees one from <see cref="FileRepoTransaction.CommitAsync(CancellationToken)"/> or
    /// <see cref="FileRepoStage.CommitAsync(CancellationToken)"/> must not conclude that the transaction was not committed or was rolled back: every file
    /// of it is committed or left indeterminate, stays accessible, and is not rolled back when the transaction is disposed.</para>
    /// </remarks>
    public event RepoTransactionCompletionFailedHandler? TransactionCompletionFailed;

    /// <summary>
    /// Occurs when repository corruption is detected during a fetch or add operation. Corruption conditions (for example a variant alias whose source data
    /// file is missing) are surfaced through this event so monitoring and repair tooling can observe them, in addition to any direct exception or
    /// per-operation handling that already applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler receives a <see cref="RepoCorruptionInfo"/> describing the corruption. The event fires every time corruption is encountered (no
    /// per-process debouncing); handlers can debounce or log as appropriate.</para>
    /// <para>
    /// Handler exceptions are not caught and will propagate out of the operation that detected the corruption.</para>
    /// </remarks>
    public event RepoCorruptionHandler? CorruptionDetected;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepo"/> class with the specified options.
    /// </summary>
    /// <exception cref="ArgumentException">The base directory or the minimum format version is not set in the options.</exception>
    public FileRepo(FileRepoOptions options)
    {
        if (options.BaseDirectory is null)
            throw new ArgumentException("Base directory must be set in options.", nameof(options));

        // The property is required, but options that were not created in code (for example bound from configuration) can still leave it unset. There is no
        // fallback: the right version depends on which processes use the repository, so the application has to choose it.
        if (!options.MinimumFormatVersion.IsDefined())
        {
            throw new ArgumentException(
                $"{nameof(FileRepoOptions)}.{nameof(FileRepoOptions.MinimumFormatVersion)} must be set to {nameof(RepoFormatVersion.Version1)}, " +
                $"{nameof(RepoFormatVersion.Version2)} or {nameof(RepoFormatVersion.Latest)}; {nameof(RepoFormatVersion.Version1)} keeps repositories " +
                "compatible with FulcrumFS 1.0.x.",
                nameof(options));
        }

        options.Freeze();
        Options = options;

        _infoFile = options.BaseDirectory.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None);
        _lockFile = options.BaseDirectory.CombineFile(FileRepoPaths.RepoLockFileName, PathOptions.None);
        _tempDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.TempDirectoryName, PathOptions.None);
        _fs = new RepoFileSystem(options.BaseDirectory, options.MarkerFileLogging)
        {
            GetCorruptionDetectedHandler = () => CorruptionDetected,
            GetDebugStepHook = () => DebugStepHook,
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepo"/> class with the specified options.
    /// </summary>
    /// <exception cref="ArgumentException">The base directory or the minimum format version is not set in the options.</exception>
    public FileRepo(IOptions<FileRepoOptions> options) : this(options.Value) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepo"/> class with the specified base directory, minimum format version and optional configuration
    /// action.
    /// </summary>
    /// <param name="baseDirectory">The base directory for the file repository. Must be an existing directory in the file system.</param>
    /// <param name="minimumFormatVersion">The lowest format version the repository has once it is opened: a new repository is created at it and an older one
    /// is upgraded to it; see <see cref="FileRepoOptions.MinimumFormatVersion"/>.</param>
    /// <param name="configure">An optional action that configures additional options.</param>
    public FileRepo(IAbsoluteDirectoryPath baseDirectory, RepoFormatVersion minimumFormatVersion, Action<FileRepoOptions>? configure = null)
        : this(BuildOptions(baseDirectory, minimumFormatVersion, configure)) { }

    private static FileRepoOptions BuildOptions(
        IAbsoluteDirectoryPath baseDirectory,
        RepoFormatVersion minimumFormatVersion,
        Action<FileRepoOptions>? configure)
    {
        var options = new FileRepoOptions(baseDirectory, minimumFormatVersion);
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Ensures that the file repository exists at the configured base directory, creating it or repairing its directory structure as necessary, and then
    /// initializes this instance exactly as <see cref="EnsureInitialized"/> does. When this method returns, the repository exists and this instance has it
    /// open.
    /// </summary>
    /// <remarks>
    /// <para>If the base directory does not exist or is empty, it is created as a FulcrumFS repository with the format version configured by
    /// <see cref="FileRepoOptions.MinimumFormatVersion"/>.</para>
    /// <para>If the base directory contains an existing FulcrumFS repository (identified by the presence of the <see cref="FileRepoPaths.InfoFileName"/>
    /// marker file), any missing required subdirectories are restored. Foreign files and folders alongside the repository are left untouched.</para>
    /// <para>If the base directory exists, contains foreign content, and is not already a FulcrumFS repository, an exception is thrown rather than initializing
    /// the directory as a repository.</para>
    /// <para>The instance is then initialized: it acquires the repository lock, which it holds until it is disposed, and upgrades a repository with an older
    /// format version than <see cref="FileRepoOptions.MinimumFormatVersion"/> to that version, so the repository is at least at that version and
    /// <see cref="FormatVersion"/> reports the version it has. Like <see cref="EnsureInitialized"/>, this method does not wait for the lock: if another
    /// instance holds the repository, it throws an <see cref="IOException"/>. This is what an overlapped restart looks like, since the previous process holds
    /// the lock until it exits, and waiting would not help because that process may hold it for minutes: do not restart a process that owns a repository with
    /// overlap, or open the repository lazily on first use rather than at startup. <see cref="EnsureInitialized"/> is the variant that throws a
    /// <see cref="DirectoryNotFoundException"/> instead of creating a repository that does not exist. Both methods can be called any number of times.</para>
    /// <para>In <see cref="FileRepoOptions.ReadOnly"/> mode this method behaves exactly as <see cref="EnsureInitialized"/>: an existing repository is verified
    /// and opened without a lock, nothing is created, repaired or upgraded, and a <see cref="DirectoryNotFoundException"/> is thrown if the repository does
    /// not exist.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The base directory contains content that does not belong to a FulcrumFS repository.</exception>
    /// <exception cref="DirectoryNotFoundException">This instance was opened read-only and the repository does not exist or is in an incomplete state, which
    /// only an instance with full access can create or repair.</exception>
    /// <exception cref="IOException">Another instance holds the repository lock, or another I/O error occurred.</exception>
    /// <exception cref="NotSupportedException">The repository was created by a newer incompatible version of FulcrumFS.</exception>
    /// <exception cref="InvalidDataException">The repository info file is malformed.</exception>
    public void EnsureCreated()
    {
        using (_stateSync.Lock())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            // A read-only instance creates and repairs nothing, so for it this is EnsureInitialized, including the exception for a missing repository.
            if (!Options.ReadOnly)
                CreateOrRepairStructure();

            InitializeCore();
        }
    }

    /// <summary>
    /// Initializes this instance on an existing repository: verifies it, acquires the repository lock and upgrades it to
    /// <see cref="FileRepoOptions.MinimumFormatVersion"/> if it is older. Unlike <see cref="EnsureCreated"/>, this method does not create a repository that
    /// does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Repository operations initialize the repository too, so calling this method (or <see cref="EnsureCreated"/>) is only needed to verify the repository,
    /// acquire its lock and make any format upgrade up front. It does not wait for the lock: if another instance holds the repository, it throws an
    /// <see cref="IOException"/>, while an operation retries for up to <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> and then throws a
    /// <see cref="TimeoutException"/>. The <see cref="IOException"/> is what an overlapped restart looks like, since the previous process holds the lock until
    /// it exits, and waiting would not help because that process may hold it for minutes: do not restart a process that owns a repository with overlap, or
    /// open the repository lazily on first use rather than at startup. Calling it again on an initialized instance does nothing.</para>
    /// <para>
    /// If the repository has an older format version than <see cref="FileRepoOptions.MinimumFormatVersion"/>, initializing it upgrades it to that version
    /// while holding the repository lock: the info file is replaced in one atomic step and flushed to disk, keeping the time the repository was created.
    /// After that, older versions of FulcrumFS refuse to open or clean the repository. A repository with the configured version or a newer one is not
    /// changed.</para>
    /// <para>
    /// In <see cref="FileRepoOptions.ReadOnly"/> mode no lock is acquired: the repository's <c>read-compat-version</c> and structure are verified once, and
    /// nothing is written or upgraded.</para>
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">The configured base directory is not a FulcrumFS repository or is missing required structure.</exception>
    /// <exception cref="IOException">Another instance holds the repository lock, or another I/O error occurred.</exception>
    /// <exception cref="NotSupportedException">The repository was created by a newer incompatible version of FulcrumFS.</exception>
    /// <exception cref="InvalidDataException">The repository info file is malformed.</exception>
    public void EnsureInitialized()
    {
        using (_stateSync.Lock())
            InitializeCore();
    }

    /// <summary>
    /// MUST BE CALLED WITHIN A STATE SYNC LOCK. Creates the repository at <see cref="TargetFormatVersion"/> if the base directory holds none, or restores the
    /// missing directories of an existing one. Takes no repository lock, so it never upgrades an existing repository: <see cref="InitializeCore"/> does that
    /// under the lock.
    /// </summary>
    private void CreateOrRepairStructure()
    {
        if (_infoFile.State is not EntryState.Exists)
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

            RepoInfoFile.Write(_infoFile, TargetFormatVersion);
        }
        else
        {
            // Existing repository - verify we're allowed full access before attempting to repair structure or perform any operations.
            RepoInfoFile.VerifyFullAccessSupported(_infoFile);
        }

        _fs.FilesDirectory.Create();
        _fs.CleanupDirectory.Create();
    }

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        return EnsureInitializedAsync(forceHealthCheck: false, waitBudget: null, cancellationToken);
    }

    /// <summary>
    /// Ensures that the repository is initialized and accessible for one of the accesses of a commit, a rollback or a staging, which share the specified wait
    /// budget, or with a wait of its own if it is <see langword="null"/>.
    /// </summary>
    private ValueTask EnsureInitializedAsync(AccessWaitBudget? waitBudget, CancellationToken cancellationToken = default)
    {
        return EnsureInitializedAsync(forceHealthCheck: false, waitBudget, cancellationToken);
    }

    /// <summary>
    /// Ensures that the repository is initialized and accessible, waiting for access for up to <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> from
    /// the start of the call, or for no longer than what is left of <paramref name="waitBudget"/> if one is specified, and then throws a
    /// <see cref="TimeoutException"/>.
    /// </summary>
    private async ValueTask EnsureInitializedAsync(bool forceHealthCheck, AccessWaitBudget? waitBudget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long initStartTimestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastAccessTimestamp, initStartTimestamp);

        // Fast path: initialized and being health-checked in the background, or initialized read-only (which has no lock to check).
        if (!forceHealthCheck && ((_lockStream is not null && _healthChecksActive) || _isReadOnlyInitialized))
            return;

        // How long the call can wait for access. Without a wait budget it is MaxAccessWaitOrRetryTime from the start of the call, whatever the time goes to.
        // With one it is what is left of the budget, and the call charges the budget with all the time it spends getting access: waiting for the state
        // lock (which another call holds while it probes the volume or retries initialization), probing the volume, the initialization attempts and the
        // delays between them. An attempt counts although it is I/O: on a volume that hangs rather than fails, the attempts are where the time goes, and
        // not charging them would let one access retry for many times the budget. What the budget does not count is the work of the commit or rollback it
        // belongs to, which happens outside this method. Once nothing is left, the call takes the lock only if it is free, probes and attempts once, and
        // throws the same TimeoutException as a call whose wait has run out.
        long chargedUntil = initStartTimestamp;
        var lockTimeout = waitBudget?.Remaining ?? Options.MaxAccessWaitOrRetryTime;
        IDisposable stateLock;

        try
        {
            stateLock = await _stateSync.LockAsync(lockTimeout, cancellationToken);
        }
        catch (TimeoutException) when (waitBudget is not null)
        {
            // The wait used up all that was left, although its timer can end it a little before the stopwatch says so.
            waitBudget.Charge(lockTimeout);
            throw;
        }

        // The wait starts with the call: the fast path before it takes no time worth separating.
        ChargeElapsed();

        using (stateLock)
        {
            // First access after an idle period (or a forced check): probe inline so a failure that happened while idle is caught before the operation
            // proceeds, then resume background checks. Concurrent first accesses wait here for the single probe rather than each probing.
            if (_lockStream is { } lockStream && (forceHealthCheck || !_healthChecksActive))
            {
                bool isVolumeAnswering = TryProbeVolume(lockStream);
                ChargeElapsed();

                if (isVolumeAnswering)
                    ArmHealthCheckTimer();
                else
                    DropLockStream(lockStream);
            }

            if (_lockStream is not null || _isReadOnlyInitialized)
                return;

            var elc = new ExceptionListCapture(ex => ex is IOException);
            var retryInterval = TimeSpan.FromSeconds(1);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isInitialized = elc.TryRun(InitializeCore);
                ChargeElapsed();

                if (isInitialized)
                    return;

                var retryDelay = waitBudget is null
                    ? (Stopwatch.GetElapsedTime(initStartTimestamp) > Options.MaxAccessWaitOrRetryTime ? TimeSpan.Zero : retryInterval)
                    : (waitBudget.Remaining < retryInterval ? waitBudget.Remaining : retryInterval);

                if (retryDelay == TimeSpan.Zero)
                    throw new TimeoutException("The operation timed out attempting to get I/O access to the repository.", elc.ResultException);

                // A delay is charged with the length it is given, so that one for all that is left uses the budget up even if its timer ends it early, and
                // the time it actually took is not charged again.
                waitBudget?.Charge(retryDelay);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                chargedUntil = Stopwatch.GetTimestamp();
            }
        }

        // Charges the budget, if there is one, with the time since it was last charged.
        void ChargeElapsed()
        {
            if (waitBudget is null)
                return;

            long now = Stopwatch.GetTimestamp();
            waitBudget.Charge(Stopwatch.GetElapsedTime(chargedUntil, now));
            chargedUntil = now;
        }
    }

    /// <summary>
    /// Timer callback: probes the repository volume and, on failure, releases the lock stream so that the next repository operation re-initializes the
    /// repository (waiting up to <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/>). The probe runs outside the state lock so a slow volume never
    /// blocks operations, and the timer is re-armed only once the probe has finished so probes never overlap. Once the repository has been idle for the
    /// interval the timer stops instead; the next access probes inline and re-arms it.
    /// </summary>
    private void RunHealthCheck(object? state)
    {
        var lockStream = _lockStream;

        if (lockStream is null)
            return; // Not initialized (or disposed): re-initialization arms the timer again.

        if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastAccessTimestamp)) >= Options.HealthCheckInterval)
        {
            _healthChecksActive = false;
            return;
        }

        if (!TryProbeVolume(lockStream))
        {
            using (_stateSync.Lock())
                DropLockStream(lockStream);

            return;
        }

        try
        {
            _healthCheckTimer?.Change(Options.HealthCheckInterval, Timeout.InfiniteTimeSpan);
            _healthChecksActive = true;
        }
        catch (ObjectDisposedException)
        {
            // The repository was disposed while the probe ran.
        }
    }

    /// <summary>
    /// MUST BE CALLED WITHIN A STATE SYNC LOCK. Starts (or resumes) background health checks one interval from now.
    /// </summary>
    private void ArmHealthCheckTimer()
    {
        if (_healthCheckTimer is null)
            _healthCheckTimer = new Timer(RunHealthCheck, null, Options.HealthCheckInterval, Timeout.InfiniteTimeSpan);
        else
            _healthCheckTimer.Change(Options.HealthCheckInterval, Timeout.InfiniteTimeSpan);

        _healthChecksActive = true;
    }

    /// <summary>
    /// Probes the repository volume by overwriting the first byte of the exclusively held lock file in place. An in-place data write exercises the write
    /// path (which fails immediately on a dismounted or disconnected volume) without changing any file metadata, so unlike a length change it never forces
    /// a journal commit or a device cache flush - on storage without a write-back cache those stall for seconds behind buffered application data.
    /// </summary>
    private bool TryProbeVolume(FileStream lockStream)
    {
        lock (_probeSync)
        {
            try
            {
                DebugStepHook?.Invoke(DebugStep.HealthCheckProbe);

                _probeValue ^= 1;
                lockStream.Position = 0;
                lockStream.WriteByte(_probeValue);

                DebugStepHook?.Invoke(DebugStep.HealthCheckProbeWritten);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// MUST BE CALLED WITHIN A STATE SYNC LOCK. Releases the given lock stream if it is still the current one, so the next operation re-initializes the
    /// repository.
    /// </summary>
    private void DropLockStream(FileStream lockStream)
    {
        if (!ReferenceEquals(_lockStream, lockStream))
            return;

        lock (_probeSync)
        {
            _lockStream = null;
            _healthChecksActive = false;
            lockStream.Dispose();
        }
    }

    /// <summary>
    /// MUST BE CALLED WITHIN A STATE SYNC LOCK. Acquires the repository lock, verifies the repository structure exists, upgrades the format of a repository
    /// that is older than <see cref="FileRepoOptions.MinimumFormatVersion"/>, then prepares per-session state. Does not create the repository:
    /// <see cref="EnsureCreated"/> creates it first if needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An instance initializes again whenever a health check has failed and dropped the repository lock (see <see cref="DropLockStream"/>), and its
    /// operations go on in the meantime. The first initialization empties the temp directory of what a previous process left in it; a later one leaves the
    /// directory's content, which is the working files of those operations, as it is.</para>
    /// <para>
    /// In read-only mode only the repository's <c>read-compat-version</c> and structure are verified, once: no lock is taken (another instance may hold it as
    /// the writer), nothing is upgraded, the temp directory is not touched and no health checks run, since the probe writes to the lock file.</para>
    /// </remarks>
    private void InitializeCore()
    {
        if (_lockStream is not null || _isReadOnlyInitialized)
            return;

        InitializeCoreSlow();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void InitializeCoreSlow()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (Options.ReadOnly)
            {
                VerifyRepoStructure();
                _isReadOnlyInitialized = true;
                DebugStepHook?.Invoke(DebugStep.RepoInitialized);
                return;
            }

            var lockStream = OpenLockFile();

            try
            {
                VerifyRepoStructure();

                // The only way the library changes a repository's format: the application configured a newer version than the repository has. The upgrade
                // is made under the repository lock and before any operation, so no instance of this library uses the repository while it changes.
                int targetVersion = TargetFormatVersion;

                if (_formatVersion < targetVersion && RepoInfoFile.VerifyFullAccessSupportedAndUpgrade(_infoFile, targetVersion))
                    _formatVersion = targetVersion;

                // The temp directory is emptied once, at the first initialization of this instance, which is when it takes the repository lock: what is
                // in the directory then was left behind by a process that ended. From then on its content is the working files of this instance's own
                // operations, which go on while the repository is initialized again after a failed health check; an add can be in its pipeline for
                // minutes. Emptying the directory then would delete those files from under the add, which fails when it goes on, and where one of them is
                // open and cannot be deleted (on Windows, a file that is open without sharing its deletion) it would fail every initialization until the
                // add had finished. So later initializations only make sure that the directory exists.
                if (!_isTempDirectoryCleaned)
                    _tempDirectory.Delete(recursive: true);

                _tempDirectory.Create();
            }
            catch
            {
                lockStream.Dispose();
                throw;
            }

            _lockStream = lockStream;
            _isTempDirectoryCleaned = true;
            Volatile.Write(ref _lastAccessTimestamp, Stopwatch.GetTimestamp());
            DebugStepHook?.Invoke(DebugStep.RepoInitialized);
            ArmHealthCheckTimer();
        }

        void VerifyRepoStructure()
        {
            if (_infoFile.State is not EntryState.Exists)
            {
                if (Options.BaseDirectory.State is not EntryState.Exists)
                    throw new DirectoryNotFoundException($"The file repository base directory '{Options.BaseDirectory.PathDisplay}' was not found.");

                throw new DirectoryNotFoundException(
                    $"The directory '{Options.BaseDirectory.PathDisplay}' is not a FulcrumFS repository. " +
                    (Options.ReadOnly ?
                        $"It cannot be created by an instance opened read-only; call '{nameof(EnsureCreated)}' on an instance with full access to create it." :
                        $"Call '{nameof(EnsureCreated)}' to create it."));
            }

            _formatVersion = Options.ReadOnly ?
                RepoInfoFile.VerifyReadCompatSupported(_infoFile).FullAccess :
                RepoInfoFile.VerifyFullAccessSupported(_infoFile).FullAccess;

            if (_fs.FilesDirectory.State is not EntryState.Exists || _fs.CleanupDirectory.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The FulcrumFS repository at '{Options.BaseDirectory.PathDisplay}' is in an incomplete state. " +
                    (Options.ReadOnly ?
                        $"It cannot be repaired by an instance opened read-only; call '{nameof(EnsureCreated)}' on an instance with full access to repair it." :
                        $"Call '{nameof(EnsureCreated)}' to repair it."));
            }
        }
    }

    /// <summary>
    /// Gets the format version that a new repository is created at and an older one is upgraded to: <see cref="FileRepoOptions.MinimumFormatVersion"/>, with
    /// <see cref="RepoFormatVersion.Latest"/> resolved to the newest version this library supports.
    /// </summary>
    private int TargetFormatVersion =>
        Options.MinimumFormatVersion is RepoFormatVersion.Latest ? RepoInfoFile.SupportedVersion : (int)Options.MinimumFormatVersion;

    private FileStream OpenLockFile() => _lockFile.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> if the repository was opened read-only.
    /// </summary>
    private void ThrowIfReadOnly()
    {
        if (Options.ReadOnly)
            throw CreateReadOnlyException();
    }

    private InvalidOperationException CreateReadOnlyException() =>
        new($"The operation is not allowed because the FulcrumFS repository at '{Options.BaseDirectory.PathDisplay}' was opened read-only.");

    /// <summary>
    /// Disposes of the file repository, releasing any resources and locks held by it.
    /// </summary>
    public void Dispose()
    {
        using (_stateSync.Lock())
        {
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = null;
            _healthChecksActive = false;

            if (_lockStream is { } lockStream)
                DropLockStream(lockStream);

            // Operations on a disposed instance go on to initialization, which throws ObjectDisposedException.
            _isReadOnlyInitialized = false;
            _isDisposed = true;
        }
    }
}
