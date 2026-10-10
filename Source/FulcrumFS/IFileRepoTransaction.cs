namespace FulcrumFS;

/// <summary>
/// Represents a transaction for an <see cref="IFileRepo"/>, which adds and deletes main files and is committed or rolled back as a whole.
/// </summary>
/// <remarks>
/// <para>
/// When the files belong to rows in a database, commit the database transaction first and this transaction second. Implementations must keep that ordering
/// safe: a file added by a transaction that did not finish committing stays readable until a cleanup pass resolves it through the callback passed to
/// <see cref="IFileRepoCleaner.CleanAsync(TimeSpan, Func{FileId, Task{IndeterminateResolution}}?, CancellationToken)"/>, and so does a file whose deletion
/// did not finish committing.</para>
/// <para>
/// Disposing the transaction rolls it back unless it has been committed.</para>
/// </remarks>
public interface IFileRepoTransaction : IAsyncDisposable
{
    /// <summary>
    /// Gets the repository the transaction belongs to.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    IFileRepo Repository { get; }

    /// <summary>
    /// Adds a new file to the repository from the specified file stream, taking the extension from the stream's file name, and processes it through the
    /// specified pipeline.
    /// </summary>
    /// <param name="stream">The source file stream.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline, or the selector that chooses one by extension.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The resulting file group, including the main file and any variants declared by the pipeline.</returns>
    /// <remarks>
    /// See <see cref="AddAsync(Stream, string, bool, IFileProcessingPipelineSelector, CancellationToken)"/>.
    /// </remarks>
    TaskWithProgress<IRepoFileGroup> AddAsync(
        FileStream stream,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new file to the repository from the specified stream and extension, and processes it through the specified pipeline.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="extension">The source file extension including the leading period, or an empty string if it has none.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline, or the selector that chooses one by extension.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The resulting file group, including the main file and any variants declared by the pipeline.</returns>
    /// <remarks>
    /// <para>
    /// The repository generates the new file's ID. The file is durable once the add returns: an implementation must not lose it after that, because the
    /// application may commit a database row that references it next. If the transaction is rolled back instead of committed, the file is deleted.</para>
    /// <para>
    /// The files of the returned group know their sizes without storage access or a network round trip (see
    /// <see cref="IRepoFile.GetLengthAsync(CancellationToken)"/>).</para>
    /// <para>
    /// The pipeline runs before the file is stored. Progress of each processor can be observed by enumerating the returned task before awaiting it (see
    /// <see cref="TaskWithProgress{T}"/>).</para>
    /// </remarks>
    /// <exception cref="FileProcessingException">A processor in the pipeline rejected or failed to process the source.</exception>
    /// <exception cref="FileSourceUnchangedException">The pipeline made no changes to the source and its
    /// <see cref="FileProcessingPipeline.ThrowWhenMainSourceUnchanged"/> option is set.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    TaskWithProgress<IRepoFileGroup> AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an existing file, or a file added by this transaction, by its file ID.
    /// </summary>
    /// <param name="fileId">The ID of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <remarks>
    /// <para>
    /// Use this method when the application believes the file exists, so that a mismatch surfaces as an exception. To delete a file that may already be gone,
    /// use <see cref="TryDeleteAsync(FileId, CancellationToken)"/> instead.</para>
    /// <para>
    /// Deleting a file that this transaction added discards the add. Deleting a file that this transaction has already deleted or discarded does nothing.
    /// The file stays readable until the transaction commits; afterwards, whether it can still be read until a cleanup pass depends on
    /// <see cref="IFileRepo.DeleteMode"/>.</para>
    /// <para>
    /// Deleting a file that another transaction has also deleted succeeds, including when that deletion has committed and the file is pending cleanup.
    /// Implementations must make the deletions converge in every delete mode, whichever kind of transaction made them. Only a file that is gone, or never
    /// existed, throws.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet exists. An implementation either accepts its deletion, which then wins (once this
    /// transaction has committed, the file is deleted whatever the adding transaction does afterwards, and that transaction's commit succeeds without
    /// bringing the file back), or refuses it with an <see cref="IOException"/> for as long as the adding transaction is open, having recorded nothing. A
    /// deletion that has committed is never undone by the add.</para>
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file was not found in the repository: it never existed, or its deletion has already been carried
    /// out.</exception>
    /// <exception cref="IOException">Another transaction is adding the file and has not finished, and the implementation does not accept the deletion of such
    /// a file. Nothing has been recorded, and the deletion can be made again once that transaction has finished.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    Task DeleteAsync(FileId fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an existing file, or a file added by this transaction, by its file ID if it exists, and reports whether it did.
    /// </summary>
    /// <param name="fileId">The ID of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the file existed and is now deleted by this transaction (or was added by this transaction and has been discarded);
    /// <see langword="false"/> if no such file exists.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the idempotent form of <see cref="DeleteAsync(FileId, CancellationToken)"/> for callers that are retrying or replaying work that may already
    /// have run. Nothing may be written to the repository when <see langword="false"/> is returned.</para>
    /// <para>
    /// A file that another transaction has deleted exists until its deletion has been carried out, so this method returns <see langword="true"/> for a file
    /// whose deletion has committed and is pending cleanup, and the deletions converge as described on
    /// <see cref="DeleteAsync(FileId, CancellationToken)"/>.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet exists as well: it is never reported as missing, and its deletion is accepted or
    /// refused as described on <see cref="DeleteAsync(FileId, CancellationToken)"/>.</para>
    /// </remarks>
    /// <exception cref="IOException">Another transaction is adding the file and has not finished, and the implementation does not accept the deletion of such
    /// a file. Nothing has been recorded, and the deletion can be made again once that transaction has finished.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    Task<bool> TryDeleteAsync(FileId fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the transaction, making the files it added and deleted permanent in the repository.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the call before the commit has started. A commit that has started is not
    /// interrupted.</param>
    /// <remarks>
    /// <para>
    /// A failure to commit an individual file is not thrown: the file stays accessible in an indeterminate state for the next cleanup pass to resolve, and
    /// the failure is reported through <see cref="IFileRepo.TransactionCompletionFailed"/>. Once this method has returned, every file of the transaction is
    /// therefore committed or indeterminate, and none has been rolled back.</para>
    /// <para>
    /// What has happened when this method throws depends on the exception. An <see cref="OperationCanceledException"/> means that nothing has been
    /// committed: the transaction is still open, and disposing it rolls it back. So once the application's database has committed, pass
    /// <see cref="CancellationToken.None"/>, or a cancellation could discard files that the database references. Any other exception comes from a
    /// <see cref="IFileRepo.TransactionCompletionFailed"/> handler that threw, once every file had been attempted, or from the repository having been
    /// disposed. Neither means that the transaction was rolled back: every file is committed or left indeterminate as above, and disposing the transaction
    /// rolls nothing back.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token was cancelled before the commit had started. Nothing has been committed.</exception>
    /// <exception cref="ObjectDisposedException">The repository has been disposed. The files that had not been committed yet are left
    /// indeterminate.</exception>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the transaction, discarding the files it added and abandoning the deletions it made.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <remarks>
    /// A failure to roll back an individual file is not thrown: the file stays accessible in an indeterminate state for the next cleanup pass to resolve, and
    /// the failure is reported through <see cref="IFileRepo.TransactionCompletionFailed"/>. Disposing the transaction without committing it rolls it back as
    /// well.
    /// </remarks>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
