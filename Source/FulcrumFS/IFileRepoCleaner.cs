namespace FulcrumFS;

/// <summary>
/// Provides cleanup operations for a file repository: removing expired delete markers, resolving indeterminate files, and physically deleting files whose
/// delete markers have aged past the supplied delete delay.
/// </summary>
public interface IFileRepoCleaner
{
    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/>.
    /// </summary>
    /// <param name="deleteDelay">The minimum age a delete marker must reach before its file is physically deleted. Use <see cref="TimeSpan.Zero"/> to delete
    /// all marked files immediately.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    Task CleanAsync(TimeSpan deleteDelay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided synchronous callback.
    /// </summary>
    /// <param name="deleteDelay">The minimum age a delete marker must reach before its file is physically deleted. Use <see cref="TimeSpan.Zero"/> to delete
    /// all marked files immediately.</param>
    /// <param name="resolveIndeterminateCallback">An optional callback invoked for each indeterminate file encountered during the clean. Return <see
    /// cref="IndeterminateResolution.Keep"/> to clear the indeterminate state and keep the file, or <see cref="IndeterminateResolution.Delete"/> to mark it for
    /// deletion.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    Task CleanAsync(TimeSpan deleteDelay, Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided asynchronous callback.
    /// </summary>
    /// <param name="deleteDelay">The minimum age a delete marker must reach before its file is physically deleted. Use <see cref="TimeSpan.Zero"/> to delete
    /// all marked files immediately.</param>
    /// <param name="resolveIndeterminateCallback">An optional callback invoked for each indeterminate file encountered during the clean. Return <see
    /// cref="IndeterminateResolution.Keep"/> to clear the indeterminate state and keep the file, or <see cref="IndeterminateResolution.Delete"/> to mark it for
    /// deletion.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    Task CleanAsync(TimeSpan deleteDelay, Func<FileId, Task<IndeterminateResolution>>? resolveIndeterminateCallback, CancellationToken cancellationToken = default);
}
