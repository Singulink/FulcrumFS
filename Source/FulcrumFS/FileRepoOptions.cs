using Singulink.Enums;

namespace FulcrumFS;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents configuration options for a file repository.
/// </summary>
public class FileRepoOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoOptions"/> class.
    /// </summary>
    public FileRepoOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoOptions"/> class with the specified base directory for the file repository.
    /// </summary>
    /// <param name="baseDirectory">The base directory for the file repository. Must be an existing directory in the file system.</param>
    [SetsRequiredMembers]
    public FileRepoOptions(IAbsoluteDirectoryPath baseDirectory)
    {
        BaseDirectory = baseDirectory;
    }

    /// <summary>
    /// Gets the base directory for the file repository.
    /// </summary>
    public required IAbsoluteDirectoryPath BaseDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the time delay between when files are marked for deletion and when they are actually deleted from the repository. A value of <see
    /// cref="TimeSpan.Zero"/> indicates immediate deletion upon transaction commit. Default is 48 hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A delete delay is important for concurrent open transactions to have access to deleted files for a period of time. It can also be used to enable
    /// recovery of files that were mistakenly deleted, or to allow scheduled file backups to run before files are physically deleted.</para>
    /// <para>
    /// This setting does not affect immediate deletion of files when a file add operation gets rolled back, e.g. when a transaction that added a file is rolled
    /// back or the file is deleted within the same transaction that added it.</para>
    /// </remarks>
    public TimeSpan DeleteDelay
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromHours(48);

    /// <summary>
    /// Gets or initializes the time delay after which files are considered indeterminate if they are not successfully committed or rolled back. Default is 48
    /// hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This delay must be long enough to ensure that files added during long-running transactions — both in the file repository and in any external databases
    /// used as a source of truth for indeterminate resolution — are not considered indeterminate before those transactions have committed. See the <see
    /// cref="FileRepo.CleanAsync(Func{FileId, IndeterminateResolution}?, CancellationToken)"/> method for details on indeterminate file state resolution.
    /// </para>
    /// <para>
    /// If the delay is too short, newly added files may be prematurely deleted during <see cref="FileRepo.CleanAsync(Func{FileId, IndeterminateResolution}?,
    /// CancellationToken)"/> operations before they are committed, resulting in potential data loss. In practice, even a very large delay should have no
    /// impact on the repository unless there is an problem with the application that is causing frequent transaction commit/rollback failures, so a very
    /// generous safety margin is recommended when configuring this value. The delay should be at least several times longer than the longest possible
    /// transaction that may add files to the repository.
    /// </para>
    /// </remarks>
    public TimeSpan IndeterminateDelay
    {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromHours(48);

    /// <summary>
    /// Gets or initializes the interval at which health checks on the repo volume/directory are performed. Minimum value is 1 second. Default is 15 seconds.
    /// </summary>
    /// <remarks>
    /// A relatively short health check interval is recommended to ensure that the repository can recover quickly from any issues that may arise, such as
    /// disconnected storage volumes, network issues, or other I/O problems. Health checks run at the start of the next repository operation if the interval has
    /// passed since the last successful check, and require only one extra I/O sys-call. The repository automatically attempts to re-initialize itself if the
    /// health check fails, with each repository operation waiting up to <see cref="MaxAccessWaitOrRetryTime"/> for re-initialization before timing out.
    /// </remarks>
    public TimeSpan HealthCheckInterval {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or initializes the logging mode used to log information and errors to indeterminate and delete marker files. Default is <see
    /// cref="LoggingMode.HumanReadable"/>.
    /// </summary>
    public LoggingMode MarkerFileLogging {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = LoggingMode.HumanReadable;

    /// <summary>
    /// Gets or initializes the maximum time that repository operations will wait for successful I/O access to the repository before throwing a <see
    /// cref="TimeoutException"/>. Must be between 1 second and <see cref="int.MaxValue"/> milliseconds (inclusive). Default is 8 seconds.
    /// </summary>
    public TimeSpan MaxAccessWaitOrRetryTime {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMilliseconds(int.MaxValue), nameof(value));

            field = value;
        }
    } = TimeSpan.FromSeconds(8);
}
