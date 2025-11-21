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
    /// Initializes a new instance of the <see cref="FileRepoOptions"/> class with the specified base directory for the file repository.
    /// </summary>
    /// <param name="baseDirectory">The base directory for the file repository. Must be an existing directory in the file system.</param>
    [SetsRequiredMembers]
    public FileRepoOptions(IAbsoluteDirectoryPath baseDirectory)
    {
        BaseDirectory = baseDirectory;
    }

    /// <summary>
    /// Gets or sets the base directory for the file repository.
    /// </summary>
    public required IAbsoluteDirectoryPath BaseDirectory {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the time delay between when files are marked for deletion and when they are physically deleted from the repository. A value of <see
    /// cref="TimeSpan.Zero"/> indicates immediate deletion upon transaction commit. Default is 1 hour.
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
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the interval at which health checks on the repo volume/directory are performed. Minimum value is 1 second. Default is 15 seconds.
    /// </summary>
    /// <remarks>
    /// A relatively short health check interval is recommended to ensure that the repository can recover quickly from any issues that may arise, such as
    /// disconnected storage volumes, network issues, or other I/O problems. Health checks run at the start of the next repository operation if the interval has
    /// passed since the last successful check, and require only one extra I/O sys-call. The repository automatically attempts to re-initialize itself if the
    /// health check fails, with each repository operation waiting up to <see cref="MaxAccessWaitOrRetryTime"/> for re-initialization before timing out.
    /// </remarks>
    public TimeSpan HealthCheckInterval {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the logging mode used to log information and errors to indeterminate and delete marker files. Default is <see
    /// cref="LoggingMode.HumanReadable"/>.
    /// </summary>
    public LoggingMode MarkerFileLogging {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = LoggingMode.HumanReadable;

    /// <summary>
    /// Gets or sets the maximum time that repository operations will wait for successful I/O access to the repository before throwing a <see
    /// cref="TimeoutException"/>. Must be between 1 second and <see cref="int.MaxValue"/> milliseconds (inclusive). Default is 8 seconds.
    /// </summary>
    public TimeSpan MaxAccessWaitOrRetryTime {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMilliseconds(int.MaxValue), nameof(value));

            field = value;
        }
    } = TimeSpan.FromSeconds(8);

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
