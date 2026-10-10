namespace FulcrumFS;

/// <summary>
/// Represents a staged transaction for an <see cref="IFileRepo"/>: a transaction for application transactions that delete many files, committed in two calls,
/// <see cref="StageAsync(CancellationToken)"/> before the application commits its database and <see cref="IFileRepoStage.CommitAsync(CancellationToken)"/>
/// after.
/// </summary>
/// <remarks>
/// <para>
/// The commit ordering rule is the same as for an <see cref="IFileRepoTransaction"/>: stage, commit the database, then commit the stage. Staging must make
/// the recorded deletions durable, so that once the application has committed its database the deletions are not lost, however many files they cover. A
/// staged transaction has no commit method of its own, so it cannot be committed without being staged first. When nothing needs to happen between the two
/// steps, <see cref="StageAndCommitAsync(CancellationToken)"/> performs both.</para>
/// <para>
/// Disposing the transaction before its stage has been committed rolls it back. If the process ends after staging and before the stage has been committed, the
/// files the transaction added or deleted are resolved by a cleanup pass through the callback passed to
/// <see cref="IFileRepoCleaner.CleanAsync(TimeSpan, Func{FileId, Task{IndeterminateResolution}}?, CancellationToken)"/>.</para>
/// <para>
/// How the deletions are made durable is up to the implementation, and the behavior described here does not depend on it. The local
/// <c>FileRepo</c> writes them to one journal file, except on a repository whose format version is 1, which has no journals: there it writes and holds
/// a marker per deleted file, as an ordinary transaction does, at a cost that grows with the number of files.</para>
/// </remarks>
public interface IFileRepoStagedTransaction : IAsyncDisposable
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
    /// The file is added exactly as by <see cref="IFileRepoTransaction.AddAsync(Stream, string, bool, IFileProcessingPipelineSelector, CancellationToken)"/>
    /// and is committed or rolled back together with the transaction's deletions.</para>
    /// <para>
    /// The files of the returned group know their sizes without storage access or a network round trip (see
    /// <see cref="IRepoFile.GetLengthAsync(CancellationToken)"/>).</para>
    /// </remarks>
    /// <exception cref="FileProcessingException">A processor in the pipeline rejected or failed to process the source.</exception>
    /// <exception cref="FileSourceUnchangedException">The pipeline made no changes to the source and its
    /// <see cref="FileProcessingPipeline.ThrowWhenMainSourceUnchanged"/> option is set.</exception>
    /// <exception cref="InvalidOperationException">The transaction has been staged or rolled back.</exception>
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
    /// The file must exist when this method is called. The deletion is recorded by the transaction and made durable by
    /// <see cref="StageAsync(CancellationToken)"/>; an implementation does not have to write anything before then.</para>
    /// <para>
    /// Use this method when the application believes the file exists, so that a mismatch surfaces as an exception. To delete a file that may already be gone,
    /// use <see cref="TryDeleteAsync(FileId, CancellationToken)"/> instead. Deleting a file that this transaction added discards the add. Deleting a file
    /// that this transaction has already deleted or discarded does nothing.</para>
    /// <para>
    /// Deleting a file that another transaction has also deleted succeeds, including when that deletion has committed and the file is pending cleanup.
    /// Implementations must make the deletions converge in every delete mode, whichever kind of transaction made them. Only a file that is gone, or never
    /// existed, throws.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet exists. An implementation either accepts its deletion, which then wins (once this
    /// transaction has committed, the file is deleted whatever the adding transaction does afterwards, and that transaction's commit succeeds without
    /// bringing the file back), or refuses it with an <see cref="IOException"/> for as long as the adding transaction is open, from this method or from
    /// <see cref="StageAsync(CancellationToken)"/>, which is before the application commits its database. A deletion that has committed is never undone
    /// by the add.</para>
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file was not found in the repository: it never existed, or its deletion has already been carried
    /// out.</exception>
    /// <exception cref="IOException">Another transaction is adding the file and has not finished, and the implementation does not accept the deletion of such
    /// a file. Nothing has been recorded.</exception>
    /// <exception cref="InvalidOperationException">The transaction has been staged or rolled back.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    Task DeleteAsync(FileId fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an existing file, or a file added by this transaction, by its file ID if it exists, and reports whether it did.
    /// </summary>
    /// <param name="fileId">The ID of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the file existed and its deletion is now recorded by this transaction (or was added by this transaction and has been
    /// discarded); <see langword="false"/> if no such file exists.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the idempotent form of <see cref="DeleteAsync(FileId, CancellationToken)"/> for callers that are retrying or replaying work that may already
    /// have run. Nothing is recorded when <see langword="false"/> is returned.</para>
    /// <para>
    /// A file that another transaction has deleted exists until its deletion has been carried out, so this method returns <see langword="true"/> for a file
    /// whose deletion has committed and is pending cleanup, and the deletions converge as described on
    /// <see cref="DeleteAsync(FileId, CancellationToken)"/>.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet exists as well: it is never reported as missing, and its deletion is accepted or
    /// refused as described on <see cref="DeleteAsync(FileId, CancellationToken)"/>.</para>
    /// </remarks>
    /// <exception cref="IOException">Another transaction is adding the file and has not finished, and the implementation does not accept the deletion of such
    /// a file. Nothing has been recorded.</exception>
    /// <exception cref="InvalidOperationException">The transaction has been staged or rolled back.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    Task<bool> TryDeleteAsync(FileId fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the transaction: makes the deletions it recorded durable and returns the stage that commits the transaction. Call this before committing the
    /// application's database, and commit the stage after.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the call before staging has started.</param>
    /// <returns>The stage that commits the transaction.</returns>
    /// <remarks>
    /// <para>
    /// Once the transaction is staged, files can no longer be added to it or deleted by it.</para>
    /// <para>
    /// A failure to stage is thrown, and so is a cancellation. Nothing has been staged in that case and the transaction is as it was before the call: it can
    /// be staged again or rolled back, disposing it rolls it back, and the application must not commit its database.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transaction has already been staged, or it has been rolled back.</exception>
    /// <exception cref="IOException">The deletions could not be made durable, or one of them is of a file that another transaction is adding and has not
    /// finished, which the implementation does not accept (see <see cref="DeleteAsync(FileId, CancellationToken)"/>).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before staging had started. Nothing has been staged.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    Task<IFileRepoStage> StageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the transaction and commits the stage, for callers that have no database commit to make between the two steps.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the call before staging, or before the commit, has started.</param>
    /// <remarks>
    /// <para>
    /// This is equivalent to calling <see cref="StageAsync(CancellationToken)"/> and then <see cref="IFileRepoStage.CommitAsync(CancellationToken)"/> on the
    /// returned stage, and fails in the same ways.</para>
    /// <para>
    /// The stage is not returned, so a commit that failed cannot be made again. When this method throws, dispose the transaction: that rolls it back, unless
    /// the commit had started and failed, in which case it leaves the transaction's files for a cleanup pass as described on
    /// <see cref="IFileRepoStage.CommitAsync(CancellationToken)"/>. Call the two methods separately if a failed commit is to be retried.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transaction has already been staged, or it has been rolled back.</exception>
    /// <exception cref="IOException">The transaction could not be staged or committed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before staging, or before the commit, had started.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has been disposed.</exception>
    Task StageAndCommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the transaction, discarding the files it added and the deletions it recorded. The transaction cannot be used afterwards.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <remarks>
    /// <para>
    /// Rolling back is possible before and after staging. Once <see cref="IFileRepoStage.CommitAsync(CancellationToken)"/> has been called, this method does
    /// nothing, because the application has committed its database by then.</para>
    /// <para>
    /// A failure to roll back an individual file is not thrown: the file is left for the cleaner and the failure is reported through
    /// <see cref="IFileRepo.TransactionCompletionFailed"/>.</para>
    /// </remarks>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
