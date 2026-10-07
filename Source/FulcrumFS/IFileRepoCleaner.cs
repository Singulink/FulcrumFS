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
    /// <param name="deleteDelay">The minimum age a delete marker (or a committed delete journal) must reach before its files are physically deleted. Use
    /// <see cref="TimeSpan.Zero"/> to delete all marked files immediately.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    /// <exception cref="IOException">A committed delete journal is damaged, or processing a file failed with an unexpected I/O error. The pass carries on
    /// with everything else and throws once it has finished: a damaged journal is left in place without deleting the files it lists and is named in the
    /// message, and a file that failed is left for the next pass.</exception>
    /// <exception cref="AggregateException">More than one such failure occurred in the pass. The inner exceptions are the individual failures.</exception>
    Task CleanAsync(TimeSpan deleteDelay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided synchronous callback.
    /// </summary>
    /// <param name="deleteDelay">The minimum age a delete marker (or a committed delete journal) must reach before its files are physically deleted. Use
    /// <see cref="TimeSpan.Zero"/> to delete all marked files immediately.</param>
    /// <param name="resolveIndeterminateCallback">An optional callback invoked for each indeterminate file encountered during the clean, including each file
    /// listed in the journal of a staged transaction that did not finish. Return <see cref="IndeterminateResolution.Keep"/> to clear the indeterminate state
    /// and keep the file, or <see cref="IndeterminateResolution.Delete"/> to mark it for deletion.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    /// <exception cref="IOException">A committed delete journal is damaged, or processing a file failed with an unexpected I/O error. The pass carries on
    /// with everything else and throws once it has finished: a damaged journal is left in place without deleting the files it lists and is named in the
    /// message, and a file that failed is left for the next pass.</exception>
    /// <exception cref="AggregateException">More than one such failure occurred in the pass. The inner exceptions are the individual failures.</exception>
    Task CleanAsync(TimeSpan deleteDelay, Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided asynchronous callback.
    /// </summary>
    /// <param name="deleteDelay">The minimum age a delete marker (or a committed delete journal) must reach before its files are physically deleted. Use
    /// <see cref="TimeSpan.Zero"/> to delete all marked files immediately.</param>
    /// <param name="resolveIndeterminateCallback">An optional callback invoked for each indeterminate file encountered during the clean, including each file
    /// listed in the journal of a staged transaction that did not finish. Return <see cref="IndeterminateResolution.Keep"/> to clear the indeterminate state
    /// and keep the file, or <see cref="IndeterminateResolution.Delete"/> to mark it for deletion.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    /// <exception cref="IOException">A committed delete journal is damaged, or processing a file failed with an unexpected I/O error. The pass carries on
    /// with everything else and throws once it has finished: a damaged journal is left in place without deleting the files it lists and is named in the
    /// message, and a file that failed is left for the next pass.</exception>
    /// <exception cref="AggregateException">More than one such failure occurred in the pass. The inner exceptions are the individual failures.</exception>
    Task CleanAsync(TimeSpan deleteDelay, Func<FileId, Task<IndeterminateResolution>>? resolveIndeterminateCallback, CancellationToken cancellationToken = default);
}
