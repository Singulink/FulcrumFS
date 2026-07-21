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
    /// disconnected storage volumes, network issues, or other I/O problems. Health checks run at the start of the next repository operation if the interval has
    /// passed since the last successful check, and require only one extra I/O sys-call. The repository automatically attempts to re-initialize itself if the
    /// health check fails, with each repository operation waiting up to <see cref="MaxAccessWaitOrRetryTime"/> for re-initialization before timing out.
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
    public TimeSpan MaxAccessWaitOrRetryTime {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMinutes(1));

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
