using Singulink.Enums;

namespace FulcrumFS;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents configuration options for a file repository.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="FileRepo"/> checks and freezes the options when it is constructed, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.</para>
/// <para>
/// The members that are <see langword="required"/> have no default. The compiler requires them when the options are created in code, but not when they are
/// bound from a configuration, so they read as not set until they are set, in any order, and the <see cref="FileRepo"/> constructor throws, with a message
/// that names them, if one is still not set then.</para>
/// </remarks>
public sealed class FileRepoOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoOptions"/> class.
    /// </summary>
    public FileRepoOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoOptions"/> class with the specified base directory and minimum format version for the file
    /// repository.
    /// </summary>
    /// <param name="baseDirectory">The base directory for the file repository. Must be an existing directory in the file system.</param>
    /// <param name="minimumFormatVersion">The lowest format version the repository has once it is opened: a new repository is created at it and an older one
    /// is upgraded to it; see <see cref="MinimumFormatVersion"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumFormatVersion"/> is not <see cref="RepoFormatVersion.Version1"/>,
    /// <see cref="RepoFormatVersion.Version2"/> or <see cref="RepoFormatVersion.Latest"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> has a name that is not friendly (see <see cref="BaseDirectory"/>).</exception>
    [SetsRequiredMembers]
    public FileRepoOptions(IAbsoluteDirectoryPath baseDirectory, RepoFormatVersion minimumFormatVersion)
    {
        CheckBaseDirectory(baseDirectory, nameof(baseDirectory));
        CheckFormatVersion(minimumFormatVersion, nameof(minimumFormatVersion));

        BaseDirectory = baseDirectory;
        MinimumFormatVersion = minimumFormatVersion;
    }

    /// <summary>
    /// Gets or sets the base directory for the file repository, or <see langword="null"/> while it is not set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The directory must be on a local file system: a local disk, or block storage that the operating system mounts as a local file system, such as an
    /// iSCSI or SAN volume. Network file systems, such as NFS mounts and SMB shares, are not supported. Transactions and the cleaner coordinate through
    /// file locks, which .NET on Linux and macOS emulates with advisory locks: it does not take the shared lock that a transaction holds on a file it is
    /// deleting when the file is on an NFS or SMB mount or on a file system it cannot identify, and takes none when its file locking is turned off
    /// (<c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>), which an application that uses a repository on those platforms must not do.</para>
    /// <para>
    /// The path must be valid for <see cref="PathOptions.NoUnfriendlyNames"/> on every platform: no name in it starts or ends with a space, ends with a dot or
    /// contains a control character, and on Windows none is a reserved device name such as <c>CON</c>. Configuration binding reads paths with
    /// <see cref="PathOptions.None"/>, which keeps such names, so without this check a typo such as a trailing space or dot would create a directory with that
    /// literal name, which many tools handle badly.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value has a name that is not friendly (see above).</exception>
    [MaybeNull]
    public required IAbsoluteDirectoryPath BaseDirectory {
        get => field ?? (IsFrozen ? throw NotSet(nameof(BaseDirectory)) : null);
        set {
            EnsureNotFrozen();
            CheckBaseDirectory(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the mode used when files are deleted through the repository. Default is <see cref="DeleteMode.DeferredUntilClean"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In <see cref="DeleteMode.DeferredUntilClean"/> mode, a delete marker is written when a delete operation completes (or when a transaction containing the
    /// deletion commits), and the physical deletion happens later when a <see cref="FileRepoCleaner"/> clean operation processes markers older than the delete
    /// delay specified to <see cref="FileRepoCleaner.CleanAsync(TimeSpan, CancellationToken)"/>. This allows concurrent open transactions to access deleted
    /// files for a period of time, supports recovery of files that were mistakenly deleted, and allows scheduled file backups to run before files are
    /// physically deleted.</para>
    /// <para>
    /// In <see cref="DeleteMode.Immediate"/> mode, files are physically deleted as soon as the delete operation completes (or when a transaction containing
    /// the deletion commits). No delete marker is written and no grace period is observed.</para>
    /// <para>
    /// This setting does not affect immediate deletion of files when a file add operation gets rolled back, e.g. when a transaction that added a file is rolled
    /// back or the file is deleted within the same transaction that added it.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="FulcrumFS.DeleteMode"/> value.</exception>
    public DeleteMode DeleteMode {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
            field = value;
        }
    } = DeleteMode.DeferredUntilClean;

    /// <summary>
    /// Gets or sets the mode used to generate file IDs in the repository. Default is <see cref="FileIdMode.Secure"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FileIdMode.Sequential"/> IDs are ordered by creation time, which improves locality and indexing performance, and they can be
    /// predicted: someone who knows one ID can guess others, so they give no protection to files that are accessible to anyone who knows their ID.
    /// <see cref="FileIdMode.Secure"/> IDs are random and can't be predicted.</para>
    /// <para>
    /// If this setting is changed, existing file IDs are not affected and the repository continues to function as expected, but new file IDs are generated
    /// according to the new mode.
    /// </para>
    /// <para>
    /// The mode applies to the whole repository, so files that need different modes are in separate repositories.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="FulcrumFS.FileIdMode"/> value.</exception>
    public FileIdMode FileIdMode {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
            field = value;
        }
    } = FileIdMode.Secure;

    /// <summary>
    /// Gets or sets the interval at which health checks on the repo volume/directory are performed. Must be at least 1 second. Default is 15 seconds.
    /// </summary>
    /// <remarks>
    /// The interval bounds how long it takes the repository to notice an I/O problem, such as a disconnected storage volume, and recover from it. While the
    /// repository is being accessed, health checks run in the background and never block repository operations; once the repository has been idle for the
    /// interval they stop, so an idle repository performs no I/O, and the first operation after an idle period performs the check before proceeding. Health
    /// checks are lightweight (a single I/O sys-call). If a check fails, the repository automatically attempts to re-initialize itself, with each repository
    /// operation waiting up to <see cref="MaxAccessWaitOrRetryTime"/> for re-initialization before timing out (a commit or rollback of a transaction waits that
    /// long once for all of its files; see <see cref="MaxAccessWaitOrRetryTime"/>).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 second.</exception>
    public TimeSpan HealthCheckInterval {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1));
            field = value;
        }
    } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the logging mode used to log information and errors to indeterminate and delete marker files. Default is
    /// <see cref="RepoLoggingMode.HumanReadable"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="RepoLoggingMode"/> value.</exception>
    public RepoLoggingMode MarkerFileLogging {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
            field = value;
        }
    } = RepoLoggingMode.HumanReadable;

    /// <summary>
    /// Gets or sets the maximum time that repository operations will wait for successful I/O access to the repository before throwing a
    /// <see cref="TimeoutException"/>. Must be between 1 second and 1 minute. Default is 8 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operation waits when it finds the repository inaccessible, for example after a health check has found that its volume stopped answering (see
    /// <see cref="HealthCheckInterval"/>): it retries initializing the repository once a second until it succeeds or this time is up.</para>
    /// <para>
    /// A commit or a rollback of a transaction needs access for each of its files, and its files share this wait instead of having one each (disposing a
    /// transaction rolls nothing back and does not wait for access). Only time spent getting access counts (waiting for it, and the attempts to reach a volume that does not answer), not the
    /// time the files take to commit, so a large commit on slow but healthy storage is never cut short, and an outage is ridden out wherever in the commit it
    /// happens, as long as the commit's waits for its outages together stay within this time. Once the time is used up, each further file is attempted once
    /// without waiting and, if the repository is still inaccessible, fails with the same <see cref="TimeoutException"/>, which the commit or rollback reports
    /// through <see cref="FileRepo.TransactionCompletionFailed"/> like any file it could not complete, leaving the file for the cleaner (a deletion that a
    /// committed delete journal records is committed, so the commit does not report it). So on a volume that has stopped answering, a commit of any number
    /// of files returns after waiting this long once. On a version 1 repository, staging a staged transaction, which writes a marker for each file it
    /// deletes, shares one wait among its files in the same way.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 second or greater than 1 minute.</exception>
    public TimeSpan MaxAccessWaitOrRetryTime {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMinutes(1));

            field = value;
        }
    } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Gets or sets the lowest format version the repository has once it is opened: a repository below it is upgraded when the <see cref="FileRepo"/>
    /// initializes, and one above it, within what this library supports, is used unchanged. Must be set: there is no default, because the right version
    /// depends on which processes and library versions use the repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a precondition: an older repository is upgraded, not refused. <see cref="FileRepo.FormatVersion"/> reports the version the repository
    /// actually has, which can be newer than this option. The library never changes the format of a repository on its own; this option is the only thing that
    /// does:</para>
    /// <list type="bullet">
    /// <item><description><see cref="FileRepo.EnsureCreated"/> creates a new repository at this version.</description></item>
    /// <item><description>An existing repository whose version is older is upgraded to this version when the <see cref="FileRepo"/> is initialized, which
    /// <see cref="FileRepo.EnsureCreated"/> and <see cref="FileRepo.EnsureInitialized"/> both do, and the first operation does if neither has been called.
    /// The upgrade is made while the instance holds the repository lock and before any operation runs.</description></item>
    /// <item><description>A repository whose version is newer than this option, but supported by this library, is used with full access and is not
    /// changed.</description></item>
    /// </list>
    /// <para>
    /// An upgraded repository can no longer be used or cleaned by an older library, which refuses it with a <see cref="NotSupportedException"/>; FulcrumFS
    /// 1.0.x cannot open a version 2 repository at all. The values:</para>
    /// <list type="bullet">
    /// <item><description><see cref="RepoFormatVersion.Version1"/> keeps a repository usable and cleanable by every FulcrumFS version, 1.0.x included, and
    /// upgrades nothing.</description></item>
    /// <item><description><see cref="RepoFormatVersion.Version2"/> adds delete journals, which record the deletions of a staged transaction, or of an
    /// ordinary one that commits several, in one file. FulcrumFS 2.0 and later can use and clean a version 2 repository.</description></item>
    /// <item><description><see cref="RepoFormatVersion.Latest"/> is the newest format of the library in use, so updating the library to one with a newer
    /// format upgrades the repository the next time it is opened, after which older libraries refuse it.</description></item>
    /// </list>
    /// <para>
    /// The API behaves the same on a version 1 repository. The only difference is internal: transactions record their deletions with a marker per file
    /// instead of a delete journal, with the same guarantees at a cost that grows with the number of files. A repository opened in <see cref="ReadOnly"/> mode
    /// is never upgraded, whatever this option says.</para>
    /// <para>
    /// The requirement to set this option is enforced by the compiler only. Options bound from configuration and passed through
    /// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> must include it as well: there is no fallback. It reads as
    /// <see cref="RepoFormatVersion.Unspecified"/> while it is not set, and the <see cref="FileRepo"/> constructor throws an <see cref="ArgumentException"/>
    /// that names the choices when it has not been set.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not <see cref="RepoFormatVersion.Version1"/>, <see cref="RepoFormatVersion.Version2"/> or
    /// <see cref="RepoFormatVersion.Latest"/>, other than <see cref="RepoFormatVersion.Unspecified"/> while it is not set.</exception>
    public required RepoFormatVersion MinimumFormatVersion {
        get => field is RepoFormatVersion.Unspecified && IsFrozen ? throw NotSet(nameof(MinimumFormatVersion)) : field;
        set {
            EnsureNotFrozen();

            // The binder of a configuration that lacks the value writes back what it read, which leaves the value as it was.
            if (value == field)
                return;

            CheckFormatVersion(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the repository is opened read-only. Default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A read-only <see cref="FileRepo"/> reads a repository that another process may be using as its writer at the same time, for example a static file
    /// host serving files from an application's repository, or opens a repository whose format is newer than this library supports for full access but whose
    /// <c>read-compat-version</c> is within what it supports. It takes no repository lock, never writes to the repository and runs no health checks.</para>
    /// <para>
    /// Fetching files and variants, enumerating the repository and <see cref="FileRepo.FormatVersion"/> work as usual. Every member that would write throws
    /// an <see cref="InvalidOperationException"/>: beginning a transaction, adding or retiring variants
    /// (<see cref="FileRepo.GetOrAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/> only when it would have to
    /// add one) and cleaning. <see cref="FileRepo.EnsureCreated"/> creates nothing: like <see cref="FileRepo.EnsureInitialized"/>, it opens an existing
    /// repository and throws a <see cref="DirectoryNotFoundException"/> when there is none. The repository is never upgraded, whatever
    /// <see cref="MinimumFormatVersion"/> says.</para>
    /// <para>
    /// The writer can remove files from disk while a read-only instance reads them: a stream that is already open keeps reading the removed file to the end,
    /// but a removed file can no longer be fetched or opened.</para>
    /// </remarks>
    public bool ReadOnly {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the options are in use: a <see cref="FileRepo"/> checked them (see <see cref="Validate"/>) when it was constructed with
    /// them, and they can no longer be changed.
    /// </summary>
    [MemberNotNullWhen(true, nameof(BaseDirectory))]
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the <see cref="FileRepo"/> constructor does. The required <see cref="BaseDirectory"/> and <see cref="MinimumFormatVersion"/> are
    /// set.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(BaseDirectory))]
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0 || BaseDirectory is null)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        List<string> problems = [];

        if (BaseDirectory is null)
            problems.Add(NotSetMessage(nameof(BaseDirectory)));

        if (MinimumFormatVersion is RepoFormatVersion.Unspecified)
        {
            problems.Add(
                $"{nameof(FileRepoOptions)}.{nameof(MinimumFormatVersion)} must be set to {nameof(RepoFormatVersion.Version1)}, " +
                $"{nameof(RepoFormatVersion.Version2)} or {nameof(RepoFormatVersion.Latest)}; {nameof(RepoFormatVersion.Version1)} keeps repositories " +
                "compatible with FulcrumFS 1.0.x.");
        }

        return problems;
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when a <see cref="FileRepo"/> is constructed with them.
    /// Options that are frozen already stay as they are.
    /// </summary>
    /// <param name="paramName">The name of the parameter that the options were passed in, which the exception names.</param>
    /// <exception cref="ArgumentException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(BaseDirectory))]
    internal void Freeze(string paramName)
    {
        if (IsFrozen)
            return;

        var problems = GetProblems();

        if (problems.Count > 0 || BaseDirectory is null)
            throw new ArgumentException(string.Join(" ", problems), paramName);

        IsFrozen = true;
    }

    private static void CheckBaseDirectory(IAbsoluteDirectoryPath value, string paramName)
    {
        if (!value.IsValidFor(PathOptions.NoUnfriendlyNames, out string? errorMessage))
        {
            throw new ArgumentException(
                $"{nameof(FileRepoOptions)}.{nameof(BaseDirectory)} '{value.PathDisplay}' must be a path with friendly names: {errorMessage}", paramName);
        }
    }

    private static void CheckFormatVersion(RepoFormatVersion value, string paramName)
    {
        if (value is RepoFormatVersion.Unspecified || !value.IsDefined())
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"The value is not {nameof(RepoFormatVersion.Version1)}, {nameof(RepoFormatVersion.Version2)} or {nameof(RepoFormatVersion.Latest)}.");
        }
    }

    private static string NotSetMessage(string property) => $"{nameof(FileRepoOptions)}.{property} is not set. It is required, and has no default.";

    private static InvalidOperationException NotSet(string property) => new(NotSetMessage(property));

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(FileRepoOptions)} are in use by a {nameof(FileRepo)}, so they can no longer be changed.");
    }
}
