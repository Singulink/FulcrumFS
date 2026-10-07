namespace FulcrumFS;

/// <content> Contains the implementations of clean functionality for the file repository. These instance methods forward to a lazily-constructed <see
/// cref="FileRepoCleaner"/> using the repository's base directory and <see cref="FileRepoOptions.MarkerFileLogging"/> mode. </content>
partial class FileRepo : IFileRepoCleaner
{
    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress, or the repository was opened read-only.</exception>
    /// <exception cref="TimeoutException">The repository could not be initialized within <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/>, for example
    /// because it has not been created (call <see cref="EnsureCreated"/> first); the inner exception says why.</exception>
    /// <exception cref="IOException">A committed delete journal is damaged, or processing a file failed with an unexpected I/O error. The pass carries on
    /// with everything else and throws once it has finished: a damaged journal is left in place without deleting the files it lists and is named in the
    /// message, and a file that failed is left for the next pass.</exception>
    /// <exception cref="AggregateException">More than one such failure occurred in the pass. The inner exceptions are the individual failures.</exception>
    public async Task CleanAsync(TimeSpan deleteDelay, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await GetCleaner().CleanAsync(deleteDelay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided synchronous callback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress, or the repository was opened read-only.</exception>
    /// <exception cref="TimeoutException">The repository could not be initialized within <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/>, for example
    /// because it has not been created (call <see cref="EnsureCreated"/> first); the inner exception says why.</exception>
    /// <exception cref="IOException">A committed delete journal is damaged, or processing a file failed with an unexpected I/O error. The pass carries on
    /// with everything else and throws once it has finished: a damaged journal is left in place without deleting the files it lists and is named in the
    /// message, and a file that failed is left for the next pass.</exception>
    /// <exception cref="AggregateException">More than one such failure occurred in the pass. The inner exceptions are the individual failures.</exception>
    public async Task CleanAsync(TimeSpan deleteDelay, Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await GetCleaner().CleanAsync(deleteDelay, resolveIndeterminateCallback, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided asynchronous callback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress, or the repository was opened read-only.</exception>
    /// <exception cref="TimeoutException">The repository could not be initialized within <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/>, for example
    /// because it has not been created (call <see cref="EnsureCreated"/> first); the inner exception says why.</exception>
    /// <exception cref="IOException">A committed delete journal is damaged, or processing a file failed with an unexpected I/O error. The pass carries on
    /// with everything else and throws once it has finished: a damaged journal is left in place without deleting the files it lists and is named in the
    /// message, and a file that failed is left for the next pass.</exception>
    /// <exception cref="AggregateException">More than one such failure occurred in the pass. The inner exceptions are the individual failures.</exception>
    public async Task CleanAsync(TimeSpan deleteDelay, Func<FileId, Task<IndeterminateResolution>>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await GetCleaner().CleanAsync(deleteDelay, resolveIndeterminateCallback, cancellationToken).ConfigureAwait(false);
    }

    private FileRepoCleaner GetCleaner() =>
        _cleaner ??= new FileRepoCleaner(new FileRepoCleaningOptions(Options.BaseDirectory) { MarkerFileLogging = Options.MarkerFileLogging });
}
