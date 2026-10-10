using System.Diagnostics;

namespace FulcrumFS;

/// <summary>
/// Represents a staged transaction for a file repository: a transaction that records all of its deletions in a single journal file and is committed in two
/// calls, <see cref="StageAsync(CancellationToken)"/> before the application commits its database and
/// <see cref="FileRepoStage.CommitAsync(CancellationToken)"/> after.
/// </summary>
/// <remarks>
/// <para>
/// Use a staged transaction when one application transaction deletes many files. An ordinary <see cref="FileRepoTransaction"/> writes, flushes to disk and
/// holds open a marker for every file it deletes until it commits. A staged transaction records its deletions in memory, and
/// <see cref="StageAsync(CancellationToken)"/> writes them all to one journal file, flushes it once and holds it open until the transaction commits or rolls
/// back.</para>
/// <para>
/// The commit ordering rule is the same as for an ordinary transaction: call <see cref="StageAsync(CancellationToken)"/>, commit the database, then commit
/// the <see cref="FileRepoStage"/> it returned. A staged transaction has no commit method of its own, so it cannot be committed without being staged first.
/// When nothing needs to happen between the two steps, <see cref="StageAndCommitAsync(CancellationToken)"/> performs both. Disposing the transaction before
/// its stage has been committed rolls it back.</para>
/// <para>
/// Files added by a staged transaction are tracked per file, as in an ordinary transaction, and are committed or rolled back together with its deletions. If
/// the process ends after staging and before the stage has been committed, the cleaner resolves the files listed in the journal through the callback passed
/// to <see cref="FileRepo.CleanAsync(TimeSpan, Func{FileId, Task{IndeterminateResolution}}?, CancellationToken)"/>, as it does for indeterminate files.</para>
/// <para>
/// Other repository operations do not consult journals, except <see cref="FileRepo.EnumerateFilesAsync(CancellationToken)"/>: it reports a file whose
/// deletion is staged as indeterminate, and one whose deletion a staged transaction committed in a deferred delete mode as pending deletion. Another
/// transaction can still delete such a file, as it can any file pending deletion, and both deletions converge. Like any deleted main file, it stays readable
/// until a cleanup pass removes it.</para>
/// <para>
/// A repository whose <see cref="FileRepo.FormatVersion"/> is 1 must not contain journals, because FulcrumFS 1.0.x may clean it. On such a repository
/// <see cref="StageAsync(CancellationToken)"/> writes, flushes and holds the marker of each recorded deletion as an ordinary transaction does, and committing
/// the stage commits and rolls back each deletion as an ordinary transaction does, so committed deletions get the usual per-file delete markers. The API, the
/// commit ordering rule and the guarantees are the same; only the cost of staging grows with the number of files.</para>
/// </remarks>
public sealed class FileRepoStagedTransaction : IFileRepoStagedTransaction
{
    private readonly TransactionAddSet _adds;

    // The deletions recorded before staging, in the order they were recorded.
    private readonly List<FileId> _deleted = [];
    private readonly HashSet<FileId> _deletedSet = [];

    private readonly AsyncLock _sync = new();

    private State _state;

    /// <summary>
    /// The durable record of the deletions (the staged journal, or the per-file markers on a version 1 repository), held open from staging until the commit
    /// or rollback has finished. Keeping it open is what signals to cleanup operations that the transaction is still active and its files must not be
    /// resolved.
    /// </summary>
    private StagedDeleteRecord? _deletes;

    /// <summary>
    /// Set once <see cref="FileRepoStage.CommitAsync(CancellationToken)"/> has been called. The application has committed its database by then, so the
    /// transaction is never rolled back afterwards, even if the commit failed.
    /// </summary>
    private bool _isCommitStarted;

    private bool _isDisposed;

    /// <summary>
    /// Gets the file repository associated with this transaction.
    /// </summary>
    public FileRepo Repository
    {
        get {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return field;
        }
        private init;
    }

    /// <inheritdoc/>
    IFileRepo IFileRepoStagedTransaction.Repository => Repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoStagedTransaction"/> class for the specified file repository.
    /// </summary>
    internal FileRepoStagedTransaction(FileRepo repository)
    {
        Repository = repository;
        _adds = new TransactionAddSet(repository);
    }

    private enum State
    {
        Open,
        Staged,
        Committed,
        RolledBack,
    }

    /// <summary>
    /// Adds a new file to the repository with the specified file stream, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="stream">The source file stream.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline provider to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The resulting file group, including the main file and any auto-variants produced by the pipeline.</returns>
    /// <exception cref="InvalidOperationException">The transaction has been staged or rolled back.</exception>
    public TaskWithProgress<RepoFileGroupInfo> AddAsync(FileStream stream, bool leaveOpen, IFileProcessingPipelineSelector pipeline, CancellationToken cancellationToken = default)
    {
        string extension = FilePath.ParseAbsolute(stream.Name, PathOptions.None).Extension;
        return AddAsync(stream, extension, leaveOpen, pipeline, cancellationToken);
    }

    /// <summary>
    /// Adds a new file to the repository with the specified stream and extension, processing it through the specified file pipeline.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="extension">The source file extension.</param>
    /// <param name="leaveOpen">Indicates whether to leave the stream open after the operation completes.</param>
    /// <param name="pipeline">The processing pipeline provider to use.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The resulting file group, including the main file and any auto-variants produced by the pipeline.</returns>
    /// <remarks>
    /// The file is added exactly as by <see cref="FileRepoTransaction.AddAsync(Stream, string, bool, IFileProcessingPipelineSelector, CancellationToken)"/>
    /// and is committed or rolled back together with the transaction's deletions.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transaction has been staged or rolled back.</exception>
    public TaskWithProgress<RepoFileGroupInfo> AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        return new(cancellationToken, async (ct, progressCallback) =>
        {
            using var syncLock = await _sync.LockAsync(ct).ConfigureAwait(false);
            ThrowIfNotOpen();

            return await _adds.AddAsync(stream, extension, leaveOpen, pipeline, progressCallback, ct).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    TaskWithProgress<IRepoFileGroup> IFileRepoStagedTransaction.AddAsync(
        FileStream stream,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IRepoFileGroup>.Upcast(AddAsync(stream, leaveOpen, pipeline, cancellationToken));
    }

    /// <inheritdoc/>
    TaskWithProgress<IRepoFileGroup> IFileRepoStagedTransaction.AddAsync(
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IRepoFileGroup>.Upcast(AddAsync(stream, extension, leaveOpen, pipeline, cancellationToken));
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID.
    /// </summary>
    /// <param name="fileId">The ID of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <remarks>
    /// <para>
    /// The file must exist when this method is called, but nothing is written to the repository: the deletion is recorded in memory and written to the
    /// transaction's journal by <see cref="StageAsync(CancellationToken)"/>.</para>
    /// <para>
    /// Use this method when the application believes the file exists, so that a mismatch (a stale reference, or a referenced file that is missing) surfaces
    /// as an exception. To delete a file that may already be gone, for example when retrying or replaying work that may already have run, use
    /// <see cref="TryDeleteAsync(FileId, CancellationToken)"/> instead.</para>
    /// <para>
    /// Deleting a file that was added by this transaction discards the add. Deleting a file that this transaction has already deleted or discarded does
    /// nothing.</para>
    /// <para>
    /// Deleting a file that another transaction has also deleted succeeds, including when that deletion has committed and the file is pending cleanup: the
    /// deletions converge in every <see cref="FileRepoOptions.DeleteMode"/>, whichever kind of transaction made them. Only a file that is gone, or never
    /// existed, throws.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet exists, and its deletion is recorded like any other. On a repository with delete
    /// journals it wins: once this transaction has committed, the file is deleted (in <see cref="DeleteMode.Immediate"/> mode by the commit itself) whatever
    /// the adding transaction does afterwards, and that transaction's commit succeeds without bringing the file back. On a version 1 repository, where
    /// staging writes the file's indeterminate marker, <see cref="StageAsync(CancellationToken)"/> throws an <see cref="IOException"/> for as long as the
    /// adding transaction holds that marker.</para>
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file was not found in the repository: it never existed, or its deletion has already been carried
    /// out.</exception>
    /// <exception cref="InvalidOperationException">The transaction has been staged or rolled back.</exception>
    public async Task DeleteAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        await DeleteCoreAsync(fileId, throwIfNotFound: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an existing or tentatively added file from the repository by its file ID if it exists, and reports whether it did.
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
    /// Prefer <see cref="DeleteAsync(FileId, CancellationToken)"/> when the application believes the file exists: a file that is unexpectedly missing usually
    /// indicates an application bug or a missing referenced file, and it should surface rather than be ignored.</para>
    /// <para>
    /// A file that another transaction has added and not committed yet exists, so this method returns <see langword="true"/> for it, and its deletion is
    /// handled as described on <see cref="DeleteAsync(FileId, CancellationToken)"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transaction has been staged or rolled back.</exception>
    public Task<bool> TryDeleteAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        return DeleteCoreAsync(fileId, throwIfNotFound: false, cancellationToken);
    }

    /// <summary>
    /// Stages the transaction: writes the deletions it recorded to a delete journal, flushes the journal to disk and holds it open, and returns the stage
    /// that commits the transaction. Call this before committing the application's database, and commit the stage after.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the call while it waits for another call on this transaction to finish. Once
    /// staging has started it is not observed.</param>
    /// <remarks>
    /// <para>
    /// Once the transaction is staged, files can no longer be added to it or deleted by it. If it recorded no deletions, no journal is written and committing
    /// the stage only commits the files it added.</para>
    /// <para>
    /// A failure to write the journal is thrown, and so is a cancellation. Nothing has been staged in that case, and the journal that could not be completed
    /// is removed: the transaction can be staged again or rolled back, disposing it rolls it back, and the application must not commit its database.</para>
    /// <para>
    /// On a version 1 repository the per-file markers described on <see cref="FileRepoStagedTransaction"/> are written instead of a journal. A failure to
    /// write one is thrown in the same way, after the markers written before it have been removed again. The marker of a file that another transaction has
    /// added and not committed yet cannot be written, because that transaction holds it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transaction has already been staged, or it has been rolled back.</exception>
    /// <exception cref="IOException">The journal (or on a version 1 repository, a marker) could not be written.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before staging had started. Nothing has been staged.</exception>
    public async Task<FileRepoStage> StageAsync(CancellationToken cancellationToken = default)
    {
        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_state is State.Staged or State.Committed)
                throw new InvalidOperationException("The transaction has already been staged.");

            ThrowIfNotOpen();

            if (_deleted.Count > 0)
                _deletes = await Repository.TxnStageDeletesAsync(_deleted).ConfigureAwait(false);

            _state = State.Staged;
            return new FileRepoStage(this);
        }
    }

    /// <inheritdoc/>
    async Task<IFileRepoStage> IFileRepoStagedTransaction.StageAsync(CancellationToken cancellationToken) =>
        await StageAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Stages the transaction and commits the stage, for callers that have no database commit to make between the two steps.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the call while it waits for another call on this transaction to finish, as for the
    /// two methods it calls.</param>
    /// <remarks>
    /// <para>
    /// This is equivalent to calling <see cref="StageAsync(CancellationToken)"/> and then <see cref="FileRepoStage.CommitAsync(CancellationToken)"/> on the
    /// returned stage, and fails in the same ways.</para>
    /// <para>
    /// The stage is not returned, so a commit that failed cannot be made again, and calling this method again then throws an
    /// <see cref="InvalidOperationException"/> because the transaction is staged. When this method throws, dispose the transaction: that rolls it back,
    /// unless the commit had started and failed, in which case it leaves the transaction's files for the cleaner as described on
    /// <see cref="FileRepoStage.CommitAsync(CancellationToken)"/>. Call the two methods separately if a failed commit is to be retried.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transaction has already been staged, or it has been rolled back.</exception>
    /// <exception cref="IOException">The journal could not be written or committed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before staging, or before the commit, had started.</exception>
    public async Task StageAndCommitAsync(CancellationToken cancellationToken = default)
    {
        var stage = await StageAsync(cancellationToken).ConfigureAwait(false);
        await stage.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the transaction, discarding the files it added and the deletions it recorded, and deleting its journal if it was staged. The transaction
    /// cannot be used afterwards.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <remarks>
    /// <para>
    /// Rolling back is possible before and after staging. Once <see cref="FileRepoStage.CommitAsync(CancellationToken)"/> has been called, this method does
    /// nothing, because the application has committed its database by then: a commit that failed can still be retried, and if it is not, disposing the
    /// transaction leaves its files indeterminate for the cleaner to resolve.</para>
    /// <para>
    /// Failures to roll back individual files or to delete the journal are not thrown; they are reported through
    /// <see cref="FileRepo.TransactionCompletionFailed"/>, as for an ordinary transaction, and the affected files are left for the cleaner.</para>
    /// </remarks>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => RollbackAsync(Repository, cancellationToken);

    /// <summary>
    /// Disposes the transaction, rolling it back unless its stage has been committed.
    /// </summary>
    /// <remarks>
    /// If <see cref="FileRepoStage.CommitAsync(CancellationToken)"/> started the commit and failed, the transaction is not rolled back: its handles are
    /// released and its files are left for the cleaner, which resolves them through the resolution callback, or deletes the files the journal lists without
    /// asking if the journal had already been renamed to committed when the commit failed.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        var repository = Repository;
        _isDisposed = true;

        try
        {
            await RollbackAsync(repository, default).ConfigureAwait(false);
        }
        catch { }

        // Release whatever is still held: everything after a commit that failed and was not retried, or anything a failed rollback did not release.
        // Releasing without completing leaves the files indeterminate, which the cleaner resolves.
        using (await _sync.LockAsync().ConfigureAwait(false))
        {
            await _adds.ReleaseIndeterminateMarkersAsync().ConfigureAwait(false);

            if (_deletes is not null)
            {
                await _deletes.DisposeAsync().ConfigureAwait(false);
                _deletes = null;
            }
        }
    }

    /// <summary>
    /// Commits the staged transaction. Called by <see cref="FileRepoStage.CommitAsync(CancellationToken)"/>.
    /// </summary>
    internal async Task CommitStageAsync(CancellationToken cancellationToken)
    {
        var elc = new ExceptionListCapture(ex => ex is not ObjectDisposedException);
        FileRepo repository;

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_state is State.Committed)
                return;

            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_state is State.RolledBack)
                throw new InvalidOperationException("The transaction has been rolled back.");

            // A stage only exists once StageAsync has succeeded, and a transaction is staged at most once.
            Debug.Assert(_state is State.Staged, "The transaction must be staged.");

            repository = Repository;
            _isCommitStarted = true;

            repository.DebugStepHook?.Invoke(DebugStep.StagedCommitStarted);

            // 1. Commit the record of the deletions in its single step, if it has one: the journal's atomic rename, flushed to disk. This is the one
            //    completion step whose failure is thrown rather than reported: until it has succeeded no file has been committed or deleted, and the caller
            //    can retry. If the rename succeeded and its flush failed, the journal is committed on disk although this throws; a caller that disposes the
            //    transaction instead of retrying then leaves a committed journal, which the cleaner applies.
            _deletes?.Commit();

            // 2. Commit the adds, releasing their indeterminate marker handles.
            foreach (var fileId in _adds.FileIds)
                await elc.TryRunAsync(_adds.CommitAsync(fileId)).ConfigureAwait(false);

            // 3. Apply the deletions according to the delete mode and release the record.
            if (_deletes is not null)
            {
                await _deletes.ApplyAsync(elc).ConfigureAwait(false);
                _deletes = null;
            }

            _state = State.Committed;
        }

        if (elc.HasExceptions)
            await repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Commit, elc.ResultException).ConfigureAwait(false);
    }

    private async Task RollbackAsync(FileRepo repository, CancellationToken cancellationToken)
    {
        var elc = new ExceptionListCapture(ex => ex is not ObjectDisposedException);

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_state is State.Committed or State.RolledBack || _isCommitStarted)
                return;

            foreach (var fileId in _adds.FileIds)
                await elc.TryRunAsync(_adds.RollbackAsync(fileId)).ConfigureAwait(false);

            if (_deletes is not null)
            {
                await _deletes.DiscardAsync(elc).ConfigureAwait(false);
                _deletes = null;
            }

            _deleted.Clear();
            _deletedSet.Clear();
            _state = State.RolledBack;
        }

        if (elc.HasExceptions)
            await repository.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Rollback, elc.ResultException).ConfigureAwait(false);
    }

    private async Task<bool> DeleteCoreAsync(FileId fileId, bool throwIfNotFound, CancellationToken cancellationToken)
    {
        using var syncLock = await _sync.LockAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfNotOpen();

        // Deleting a file this transaction added discards the add; deleting it again after that is a no-op.
        if (await _adds.TryDiscardAsync(fileId).ConfigureAwait(false))
            return true;

        if (_deletedSet.Contains(fileId))
            return true;

        // The existence check is the same as an ordinary transaction's, but no marker is written: the journal written at staging records the deletion.
        if (!await Repository.TxnCheckDeleteAsync(fileId, throwIfNotFound).ConfigureAwait(false))
            return false;

        _deleted.Add(fileId);
        _deletedSet.Add(fileId);
        return true;
    }

    private void ThrowIfNotOpen()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_state is State.RolledBack)
            throw new InvalidOperationException("The transaction has been rolled back.");

        if (_state is not State.Open)
            throw new InvalidOperationException("The transaction has been staged. Files cannot be added or deleted after staging.");
    }
}
