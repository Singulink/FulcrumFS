using Singulink.Enums;

namespace FulcrumFS;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents configuration options for a file repository.
/// </summary>
public class FileRepoOptions
{
    private bool _frozen;

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
    [SetsRequiredMembers]
    public FileRepoOptions(IAbsoluteDirectoryPath baseDirectory, RepoFormatVersion minimumFormatVersion)
    {
        BaseDirectory = baseDirectory;
        MinimumFormatVersion = minimumFormatVersion;
    }

    /// <summary>
    /// Gets or sets the base directory for the file repository.
    /// </summary>
    /// <remarks>
    /// The directory must be on a local file system: a local disk, or block storage that the operating system mounts as a local file system, such as an
    /// iSCSI or SAN volume. Network file systems, such as NFS mounts and SMB shares, are not supported. Transactions and the cleaner coordinate through
    /// file locks, which .NET on Linux and macOS emulates with advisory locks: it does not take the shared lock that a transaction holds on a file it is
    /// deleting when the file is on an NFS or SMB mount or on a file system it cannot identify, and takes none when its file locking is turned off
    /// (<c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>), which an application that uses a repository on those platforms must not do.
    /// </remarks>
    public required IAbsoluteDirectoryPath BaseDirectory {
        get;
        set {
            EnsureNotFrozen();
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
    /// Use <see cref="FileIdMode.Sequential"/> for improved locality and indexing performance when file IDs are not exposed to untrusted parties or are only
    /// used for publicly accessible resources. Use <see cref="FileIdMode.Secure"/> when file IDs are used for private resources or otherwise exposed to
    /// untrusted parties and must be unpredictable.</para>
    /// <para>
    /// If this setting is changed, existing file IDs are not affected and the repository continues to function as expected, but new file IDs are generated
    /// according to the new mode.
    /// </para>
    /// <para>
    /// If you have a mix of public and private resources, it is good practice to use separate repositories with different <see cref="FileIdMode"/> settings to
    /// avoid the security risks of using sequential IDs for private resources or the performance costs of using secure IDs for public resources.</para>
    /// </remarks>
    public FileIdMode FileIdMode {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
            field = value;
        }
    } = FileIdMode.Secure;

    /// <summary>
    /// Gets or sets the interval at which health checks on the repo volume/directory are performed. Minimum value is 1 second. Default is 15 seconds.
    /// </summary>
    /// <remarks>
    /// A relatively short health check interval is recommended to ensure that the repository can recover quickly from any issues that may arise, such as
    /// disconnected storage volumes, network issues, or other I/O problems. While the repository is being accessed, health checks run in the background
    /// and never block repository operations; once the repository has been idle for the interval they stop, so an idle repository performs no I/O, and the
    /// first operation after an idle period performs the check before proceeding. Health checks are lightweight (a single I/O sys-call). If a check fails,
    /// the repository automatically attempts to re-initialize itself, with each repository operation waiting up to <see cref="MaxAccessWaitOrRetryTime"/>
    /// for re-initialization before timing out (a commit or rollback of a transaction waits that long once for all of its files; see
    /// <see cref="MaxAccessWaitOrRetryTime"/>).
    /// </remarks>
    public TimeSpan HealthCheckInterval {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1));
            field = value;
        }
    } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the logging mode used to log information and errors to indeterminate and delete marker files. Default is <see
    /// cref="RepoLoggingMode.HumanReadable"/>.
    /// </summary>
    public RepoLoggingMode MarkerFileLogging {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
            field = value;
        }
    } = RepoLoggingMode.HumanReadable;

    /// <summary>
    /// Gets or sets the maximum time that repository operations will wait for successful I/O access to the repository before throwing a <see
    /// cref="TimeoutException"/>. Must be between 1 second and 1 minute (inclusive). Default is 8 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operation waits when it finds the repository inaccessible, for example after a health check has found that its volume stopped answering (see
    /// <see cref="HealthCheckInterval"/>): it retries initializing the repository once a second until it succeeds or this time is up.</para>
    /// <para>
    /// A commit or a rollback of a transaction, including the rollback made by disposing one, needs access for each of its files, and its files share this wait
    /// instead of having one each. Only time spent getting access counts (waiting for it, and the attempts to reach a volume that does not answer), not the
    /// time the files take to commit, so a large commit on slow but healthy storage is never cut short, and an outage is ridden out wherever in the commit it
    /// happens, as long as the commit's waits for its outages together stay within this time. Once the time is used up, each further file is attempted once
    /// without waiting and, if the repository is still inaccessible, fails with the same <see cref="TimeoutException"/>, which the commit or rollback reports
    /// through <see cref="FileRepo.TransactionCompletionFailed"/> like any file it could not complete, leaving the file for the cleaner (a deletion that a
    /// committed delete journal records is committed, so the commit does not report it). So on a volume that has stopped answering, a commit of any number
    /// of files returns after waiting this long once. On a version 1 repository, staging a staged transaction, which writes a marker for each file it
    /// deletes, shares one wait among its files in the same way.</para>
    /// </remarks>
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
    /// 1.0.x cannot open a version 2 repository at all. Choose:</para>
    /// <list type="bullet">
    /// <item><description><see cref="RepoFormatVersion.Version1"/> while any process that uses or cleans the repositories, including a cleanup job that runs
    /// separately from the application, still runs FulcrumFS 1.0.x. Once every one of them runs FulcrumFS 2.0 or later, set
    /// <see cref="RepoFormatVersion.Version2"/>.</description></item>
    /// <item><description><see cref="RepoFormatVersion.Version2"/> for delete journals, which record the deletions of a staged transaction, or of an
    /// ordinary one that commits several, in one file, once every such process runs FulcrumFS 2.0 or later.</description></item>
    /// <item><description><see cref="RepoFormatVersion.Latest"/> only for repositories that the application owns exclusively, so that they always have the
    /// newest format of the library in use.</description></item>
    /// </list>
    /// <para>
    /// The API behaves the same on a version 1 repository. The only difference is internal: transactions record their deletions with a marker per file
    /// instead of a delete journal, with the same guarantees at a cost that grows with the number of files. A repository opened in <see cref="ReadOnly"/> mode
    /// is never upgraded, whatever this option says.</para>
    /// <para>
    /// The requirement to set this option is enforced by the compiler only. Options bound from configuration and passed through
    /// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> must include it as well: there is no fallback, and the <see cref="FileRepo"/>
    /// constructor throws an <see cref="ArgumentException"/> that names the choices when it has not been set.</para>
    /// </remarks>
    public required RepoFormatVersion MinimumFormatVersion {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
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

    internal void Freeze()
    {
        _frozen = true;
    }

    private void EnsureNotFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException("Cannot modify options after the repository has been initialized.");
    }
}
