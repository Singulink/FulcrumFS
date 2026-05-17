namespace FulcrumFS;

/// <content> Contains the implementations of clean functionality for the file repository. These instance methods forward to a lazily-constructed <see
/// cref="FileRepoCleaner"/> using the repository's base directory and <see cref="FileRepoOptions.MarkerFileLogging"/> mode. </content>
partial class FileRepo : IFileRepoCleaner
{
    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    /// <exception cref="DirectoryNotFoundException">The repository has not been initialized. Call <see cref="EnsureCreated"/> first.</exception>
    public async Task CleanAsync(TimeSpan deleteDelay, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await GetCleaner().CleanAsync(deleteDelay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided synchronous callback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    /// <exception cref="DirectoryNotFoundException">The repository has not been initialized. Call <see cref="EnsureCreated"/> first.</exception>
    public async Task CleanAsync(TimeSpan deleteDelay, Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await GetCleaner().CleanAsync(deleteDelay, resolveIndeterminateCallback, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up the repository by removing files whose delete markers are older than <paramref name="deleteDelay"/> and resolving indeterminate files using
    /// the provided asynchronous callback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another clean operation is already in progress.</exception>
    /// <exception cref="DirectoryNotFoundException">The repository has not been initialized. Call <see cref="EnsureCreated"/> first.</exception>
    public async Task CleanAsync(TimeSpan deleteDelay, Func<FileId, Task<IndeterminateResolution>>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await GetCleaner().CleanAsync(deleteDelay, resolveIndeterminateCallback, cancellationToken).ConfigureAwait(false);
    }

    private FileRepoCleaner GetCleaner() =>
        _cleaner ??= new FileRepoCleaner(new FileRepoCleaningOptions(Options.BaseDirectory) { MarkerFileLogging = Options.MarkerFileLogging });
}
