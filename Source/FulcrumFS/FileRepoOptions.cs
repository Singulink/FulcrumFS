namespace FulcrumFS;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents configuration options for a file repository.
/// </summary>
public class FileRepoOptions
{
    /// <summary>
    /// Gets or sets the base directory for the file repository.
    /// </summary>
    public required IAbsoluteDirectoryPath BaseDirectory { get; set; }

    /// <summary>
    /// Gets or sets the time delay between when files are marked for deletion and when they are actually deleted from the repository. Default is <see
    /// cref="TimeSpan.Zero"/>, indicating immediate deletion upon transaction commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting a delete delay can be useful for allowing other open transactions that may still be referencing deleted files to complete before the files are
    /// actually deleted. It can also be used to allow recovery of files that were mistakenly deleted, or to allow file backup systems that run on a schedule to
    /// back up files before they are deleted.</para>
    /// <para>
    /// NOTE: This setting does not affect immediate deletion of files when a file add operation gets rolled back, either by the transaction being rolled back
    /// or when the file is deleted within the same transaction.</para>
    /// </remarks>
    public TimeSpan DeleteDelay {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets the time delay after which files are considered indeterminate if they are not successfully committed or rolled back. Default is 24 hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This delay must be long enough to ensure that files added during long-running transactions — both in the file repository and in any external databases
    /// used as a source of truth for indeterminate resolution — are not considered indeterminate before those transactions have committed. See the <see
    /// cref="FileRepo.CleanAsync(Func{FileId, IndeterminateResolution}?, CancellationToken)"/> method for details on indeterminate file state resolution.
    /// </para>
    /// <para>
    /// If the delay is too short, files may be prematurely deleted before they are committed, resulting in potential data loss. Even a large delay should have
    /// minimal impact on the repository unless transactions frequently fail to commit or roll back, so a very generous safety margin is recommended when
    /// configuring this value.
    /// </para>
    /// </remarks>
    public TimeSpan IndeterminateDelay
    {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromHours(24);

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
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets a value indicating whether the repository logs information and errors to indeterminate and delete marker files. Default is
    /// <see langword="true"/>.
    /// </summary>
    public bool LogToMarkerFiles { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum time that repository operations will wait for successful I/O access to the repository before throwing a <see
    /// cref="TimeoutException"/>. Must be between 1 second and <see cref="int.MaxValue"/> milliseconds (inclusive). Default is 10 seconds.
    /// </summary>
    public TimeSpan MaxAccessWaitOrRetryTime {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMilliseconds(int.MaxValue), nameof(value));

            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets a handler that is invoked when a commit operation fails, which can be used to log errors or perform custom error handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler receives an <see cref="Exception"/> object that describes the error that occurred. If multiple errors occurred, the exception will be of
    /// type <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not thrown automatically for transaction commit failures since added files are still accessible after the commit fails (they are just
    /// marked as being in an indeterminate state). The handler can throw the exception if throwing behavior is desired instead.</para>
    /// </remarks>
    public Func<Exception, Task>? CommitFailed { get; set; }

    /// <summary>
    /// Gets or sets a handler that is invoked when a rollback operation fails, which can be used to log errors or perform custom error handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event handler receives an <see cref="Exception"/> object that describes the error that occurred. If multiple errors occurred, the exception will be
    /// of type <see cref="AggregateException"/>.</para>
    /// <para>
    /// Exceptions are not thrown automatically for transaction rollback failures since deleted files are still accessible after the rollback fails (they are
    /// just marked as being in an indeterminate state). The handler can throw the exception if throwing behavior is desired instead.</para>
    /// </remarks>
    public Func<Exception, Task>? RollbackFailed { get; set; }
}
