namespace FulcrumFS;

/// <summary>
/// The durable record of the deletions of a staged <see cref="FileRepoStagedTransaction"/>, written when the transaction is staged and held open until it has
/// committed or rolled back. Not thread-safe: the owning transaction serializes every call through its own lock.
/// </summary>
/// <remarks>
/// <para>
/// On a repository whose format has delete journals, the record is one journal listing every deletion, committed by a single rename. A version 1 repository
/// must not contain journals, so there the record is the indeterminate marker of each deleted file, written, flushed and held exactly as an ordinary
/// <see cref="FileRepoTransaction"/> does, and each deletion is committed or rolled back the way an ordinary transaction does it. The guarantees are the same
/// either way; the markers cost a flush and a file handle per file.</para>
/// <para>
/// Holding the record open is what keeps the cleaner from resolving its files while the transaction can still commit or roll back. Releasing it without
/// finishing leaves them indeterminate for the cleaner to resolve through the resolution callback.</para>
/// </remarks>
internal abstract class StagedDeleteRecord : IAsyncDisposable
{
    /// <summary>
    /// Creates a record over a staged delete journal that lists the specified files.
    /// </summary>
    public static StagedDeleteRecord FromJournal(FileRepo repository, RepoFileSystem.DeleteJournal journal, IReadOnlyCollection<FileId> fileIds) =>
        new JournalRecord(repository, journal, fileIds);

    /// <summary>
    /// Opens the indeterminate marker of each of the specified files, flushed to disk, and returns the record holding them. The files are not checked again:
    /// a file that is gone or has been deleted since its deletion was recorded is handled by the commit, as in an ordinary transaction. A marker that is in
    /// use, normally because the transaction that added its file has not finished, is waited for briefly, which the token cancels. If a marker cannot be
    /// written or the wait is cancelled, the markers written so far are rolled back and the exception is propagated. Every access to the repository that
    /// this makes, the rollback included, waits within the staging's wait budget.
    /// </summary>
    public static async Task<StagedDeleteRecord> CreateMarkersAsync(
        FileRepo repository, IReadOnlyCollection<FileId> fileIds, AccessWaitBudget waitBudget, CancellationToken cancellationToken)
    {
        var record = new MarkerRecord(repository);

        try
        {
            foreach (var fileId in fileIds)
                record.Add(fileId, await repository.TxnOpenDeleteMarkerAsync(fileId, waitBudget, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            // Nothing has been staged, so the transaction can be staged again or rolled back: undo what was written. A marker that cannot be cleared is left
            // for the cleaner, which resolves it through the callback like any indeterminate file.
            await record.DiscardAsync(new ExceptionListCapture(null), waitBudget).ConfigureAwait(false);
            throw;
        }

        return record;
    }

    /// <summary>
    /// Commits the deletions in a single step, if the record has one, before anything else of the transaction is committed. A failure is thrown; nothing has
    /// been committed then, and calling the method again resumes where it failed.
    /// </summary>
    public abstract void Commit();

    /// <summary>
    /// Applies the committed deletions according to the repository's <see cref="FileRepoOptions.DeleteMode"/> and releases the record. Each access to the
    /// repository waits within the commit's <paramref name="waitBudget"/>.
    /// </summary>
    /// <remarks>
    /// A record of markers commits each deletion here: its failures are captured by <paramref name="elc"/>, and the affected files are left for the
    /// cleaner to resolve. A journal committed all of its deletions in <see cref="Commit"/>, and the cleaner carries out what is left of them without
    /// asking, so a deletion that cannot be carried out here is not a failure of the commit and is not captured, whether its step says so or throws.
    /// </remarks>
    public abstract Task ApplyAsync(ExceptionListCapture elc, AccessWaitBudget waitBudget);

    /// <summary>
    /// Rolls back the deletions and releases the record. Failures are captured by <paramref name="elc"/> and the affected files are left for the cleaner.
    /// Each access to the repository waits within the rollback's <paramref name="waitBudget"/>.
    /// </summary>
    public abstract Task DiscardAsync(ExceptionListCapture elc, AccessWaitBudget waitBudget);

    /// <summary>
    /// Releases whatever the record still holds without finishing it, which leaves its files indeterminate for the cleaner. Failures are ignored.
    /// </summary>
    public abstract ValueTask DisposeAsync();

    private sealed class JournalRecord(FileRepo repository, RepoFileSystem.DeleteJournal journal, IReadOnlyCollection<FileId> fileIds) : StagedDeleteRecord
    {
        private bool _isCommitted;

        // One atomic rename however many files the journal lists, flushed to disk. A journal that is already gone was resolved by the cleaner, which only
        // happens if the handle was lost, so its deletions are left to that resolution.
        public override void Commit() => _isCommitted = journal.Commit();

        public override async Task ApplyAsync(ExceptionListCapture elc, AccessWaitBudget waitBudget)
        {
            // In immediate mode the listed files are deleted now and the journal is removed once all of them are gone; a deletion that cannot complete keeps
            // the journal, the durable record of the deletion, for the cleaner to finish. In the deferred modes (both defer main files) the committed journal
            // stays as the delete record of its files, and the cleaner applies the delete delay to it.
            //
            // The committed journal has committed every deletion it lists: the cleaner carries out what is left of them without asking. So a deletion that
            // cannot be carried out here is not a failure of the commit and is not reported, whether its step says so or throws, as every one of them does
            // with a TimeoutException on a volume that has stopped answering; reporting those would tell the application that its commit failed when its
            // deletions are recorded and will be made. A disposed repository is still thrown, as by every other step of a commit.
            if (_isCommitted && repository.Options.DeleteMode is DeleteMode.Immediate)
            {
                bool allDeleted = true;

                foreach (var fileId in fileIds)
                {
                    try
                    {
                        if (!await repository.TxnCommitStagedDeleteAsync(fileId, waitBudget).ConfigureAwait(false))
                            allDeleted = false;
                    }
                    catch (Exception ex) when (ex is not ObjectDisposedException)
                    {
                        allDeleted = false;
                    }
                }

                // A journal that cannot be removed although all of its files are gone is the same case: it is still committed, and the cleaner removes a
                // committed journal once it finds the files it lists gone.
                if (allDeleted)
                {
                    try { journal.DeleteCommitted(); }
                    catch { }
                }
            }

            await elc.TryRunAsync(journal.DisposeAsync()).ConfigureAwait(false);
        }

        // Discarding the journal does not go through the repository's access check, so it has no wait to share.
        public override async Task DiscardAsync(ExceptionListCapture elc, AccessWaitBudget waitBudget) =>
            await elc.TryRunAsync(journal.DiscardAsync()).ConfigureAwait(false);

        public override async ValueTask DisposeAsync()
        {
            try { await journal.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }

    private sealed class MarkerRecord(FileRepo repository) : StagedDeleteRecord
    {
        // The shared handle on each deleted file's indeterminate marker, in the order the deletions were recorded. An entry is taken out before its deletion
        // is finished, so whatever is left is still held. The repository releases the handle of a deletion in the same step in which it finishes it.
        private readonly Queue<(FileId FileId, IAsyncDisposable Marker)> _markers = [];

        public void Add(FileId fileId, IAsyncDisposable marker) => _markers.Enqueue((fileId, marker));

        // Each deletion commits on its own, as in an ordinary transaction, so there is no single step that could fail before the others.
        public override void Commit() { }

        public override async Task ApplyAsync(ExceptionListCapture elc, AccessWaitBudget waitBudget)
        {
            while (_markers.TryDequeue(out var entry))
                await elc.TryRunAsync(repository.TxnCommitDeleteAsync(entry.FileId, entry.Marker, waitBudget)).ConfigureAwait(false);
        }

        public override async Task DiscardAsync(ExceptionListCapture elc, AccessWaitBudget waitBudget)
        {
            while (_markers.TryDequeue(out var entry))
                await elc.TryRunAsync(repository.TxnRollbackDeleteAsync(entry.FileId, entry.Marker, waitBudget)).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            while (_markers.TryDequeue(out var entry))
            {
                try { await entry.Marker.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }
}
