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
    /// Gets or sets the interval at which a health check on the repo volume/directory is performed. Minimum value is 1 second. Default is 15 seconds.
    /// </summary>
    /// <remarks>
    /// Health check is performed at the beginning of the next operation after the interval time has passed since the last check. The repository automatically
    /// attempts to re-initialize itself if the health check fails, with each operation waiting up to <see cref="MaxAccessWaitOrRetryTime"/> for re-initialization
    /// before timing out.
    /// </remarks>
    public TimeSpan HealthCheckInterval {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the maximum time that operations will wait for successful I/O access to the repository before timing out or throwing the I/O exception.
    /// </summary>
    public TimeSpan MaxAccessWaitOrRetryTime {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1), nameof(value));
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
    /// marked as being in an indeterminate state). The handler can throw an exception if that behavior is desired.</para>
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
    /// just marked as being in an indeterminate state). The handler can throw an exception if that behavior is desired.</para>
    /// </remarks>
    public Func<Exception, Task>? RollbackFailed { get; set; }
}
