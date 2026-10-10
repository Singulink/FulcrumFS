using System.Globalization;

namespace FulcrumFS;

/// <summary>
/// Pins that the indeterminate marker of a file stays on disk, and held, for as long as a transaction of the repository has a deletion of the file pending
/// through it: an ordinary transaction from the moment it deletes the file, and a staged transaction on a version 1 repository from the moment it is staged.
/// </summary>
/// <remarks>
/// An application commits its database before the repository, so between the two commits the marker is the only durable record that the file is to be
/// deleted: if the process ends there, the cleaner finds the marker with nobody holding it, asks the resolution callback and deletes the file. Several
/// transactions can have the deletion of one file pending, each with its own handle on the one marker, and one of them finishing must not take the marker away
/// from the others unless what it did made the deletion durable.
/// </remarks>
[PrefixTestClass]
public sealed class PendingDeleteMarkerTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    // How long a debug step waits for a call on another transaction that must not get through while the step is running. The call does get through in that
    // time when the repository does not keep it out, which is what makes the test fail then.
    private static readonly TimeSpan _blockedCallWait = TimeSpan.FromMilliseconds(150);

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // ---------------------------------------------------------------------------------------------------------------
    // Several pending deletions of one file
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false, false, DeleteMode.Immediate, false, false)]
    [DataRow(false, false, DeleteMode.Immediate, false, true)]
    [DataRow(false, false, DeleteMode.Immediate, true, false)]
    [DataRow(false, false, DeleteMode.Immediate, true, true)]
    [DataRow(false, false, DeleteMode.DeferredUntilClean, false, false)]
    [DataRow(false, false, DeleteMode.DeferredUntilClean, false, true)]
    [DataRow(false, false, DeleteMode.DeferredUntilClean, true, false)]
    [DataRow(false, false, DeleteMode.DeferredUntilClean, true, true)]
    [DataRow(true, true, DeleteMode.Immediate, false, false)]
    [DataRow(true, true, DeleteMode.Immediate, false, true)]
    [DataRow(true, true, DeleteMode.Immediate, true, false)]
    [DataRow(true, true, DeleteMode.Immediate, true, true)]
    [DataRow(true, true, DeleteMode.DeferredUntilClean, false, false)]
    [DataRow(true, true, DeleteMode.DeferredUntilClean, false, true)]
    [DataRow(true, true, DeleteMode.DeferredUntilClean, true, false)]
    [DataRow(true, true, DeleteMode.DeferredUntilClean, true, true)]
    [DataRow(false, true, DeleteMode.Immediate, false, false)]
    [DataRow(false, true, DeleteMode.Immediate, false, true)]
    [DataRow(false, true, DeleteMode.Immediate, true, false)]
    [DataRow(false, true, DeleteMode.Immediate, true, true)]
    [DataRow(false, true, DeleteMode.DeferredUntilClean, false, false)]
    [DataRow(false, true, DeleteMode.DeferredUntilClean, false, true)]
    [DataRow(false, true, DeleteMode.DeferredUntilClean, true, false)]
    [DataRow(false, true, DeleteMode.DeferredUntilClean, true, true)]
    public async Task TwoPendingDeletions_WhenOneRollsBack_TheMarkerStaysHeldUntilTheOtherHasFinished(
        bool firstIsStaged, bool secondIsStaged, DeleteMode deleteMode, bool firstRollsBack, bool otherCommits)
    {
        // A staged transaction records its deletions with the files' markers on a version 1 repository only, where an ordinary one does so as well.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, firstIsStaged || secondIsStaged ? RepoFormatVersion.Version1 : RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);

            await using var first = await DeleteInNewTransactionAsync(repo, firstIsStaged, fileId);
            await using var second = await DeleteInNewTransactionAsync(repo, secondIsStaged, fileId);

            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(marker);
            IsHeld(marker).ShouldBeTrue();

            var (rolledBack, other) = firstRollsBack ? (first, second) : (second, first);
            await rolledBack.RollbackAsync();

            // The other deletion is still pending, and the marker is all that records it: it is still there, and still held, so a clean pass leaves the
            // file alone whatever the callback would answer.
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(marker);
            IsHeld(marker).ShouldBeTrue();

            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            asked.ShouldBeEmpty();
            IsHeld(marker).ShouldBeTrue();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            if (otherCommits)
            {
                await other.CommitAsync();
                ShouldBeDeleted(repoDir, fileId, deleteMode);
            }
            else
            {
                // The last pending deletion is the one that takes the marker with it.
                await other.RollbackAsync();
                ShouldBeCommitted(repoDir, fileId);
                CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            }

            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task ThreePendingDeletions_RolledBackCommittedAndRolledBack_TheMarkerStaysUntilTheCommit(DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);

            await using var first = await DeleteInNewTransactionAsync(repo, staged: false, fileId);
            await using var second = await DeleteInNewTransactionAsync(repo, staged: false, fileId);
            await using var third = await DeleteInNewTransactionAsync(repo, staged: false, fileId);

            // Two deletions are still pending after the first rollback.
            await first.RollbackAsync();

            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(marker);
            IsHeld(marker).ShouldBeTrue();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            // A deletion that commits makes the deletion durable, so it takes the marker with it although another deletion is still pending.
            await second.CommitAsync();
            ShouldBeDeleted(repoDir, fileId, deleteMode);

            // The rollback of the last one finds nothing left to do and does not bring the file back.
            await third.RollbackAsync();
            ShouldBeDeleted(repoDir, fileId, deleteMode);
            failures.ShouldBeEmpty();

            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            asked.ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false, DeleteMode.Immediate)]
    [DataRow(false, DeleteMode.DeferredUntilClean)]
    [DataRow(true, DeleteMode.Immediate)]
    [DataRow(true, DeleteMode.DeferredUntilClean)]
    public async Task PendingDeletion_RolledBackWhileAnotherIsBeingCommitted_LeavesTheMarkerUntilThatDeletionIsRecorded(bool staged, DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, staged ? RepoFormatVersion.Version1 : RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);

            await using var committing = await DeleteInNewTransactionAsync(repo, staged, fileId);
            await using var rollingBack = await DeleteInNewTransactionAsync(repo, staged, fileId);

            // The rollback arrives when the commit has released the marker and has recorded nothing yet. If it removed the marker then, a process that
            // ended at this point would leave the file with nothing that says its deletion was committed by the application.
            Task? rollback = null;
            bool? rolledBackBeforeRecorded = null;
            bool? markerExistsBeforeRecorded = null;

            repo.DebugStepHook = step =>
            {
                if (step is not DebugStep.DeleteCommitStarted)
                    return;

                rollback = Task.Run(rollingBack.RollbackAsync, TestContext.CancellationToken);
                rolledBackBeforeRecorded = rollback.Wait(_blockedCallWait, TestContext.CancellationToken);
                markerExistsBeforeRecorded = marker.State is EntryState.Exists;
            };

            await committing.CommitAsync();
            repo.DebugStepHook = null;

            await rollback.ShouldNotBeNull();

            rolledBackBeforeRecorded.ShouldBe(false);
            markerExistsBeforeRecorded.ShouldBe(true);

            ShouldBeDeleted(repoDir, fileId, deleteMode);
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The process ends with a deletion pending
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task PendingDeletion_WhoseProcessEndsAfterAnotherWasRolledBack_IsResolvedByTheCleanerThroughTheCallback(bool staged, bool firstRollsBack)
    {
        var repoDir = NewRepoDir();
        var formatVersion = staged ? RepoFormatVersion.Version1 : RepoFormatVersion.Version2;
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);
        FileId fileId;

        try
        {
            fileId = await AddSampleFileAsync(repo);

            var first = await DeleteInNewTransactionAsync(repo, staged, fileId);
            var second = await DeleteInNewTransactionAsync(repo, staged, fileId);

            var (rolledBack, pending) = firstRollsBack ? (first, second) : (second, first);

            await rolledBack.RollbackAsync();
            await rolledBack.DisposeAsync();

            // The application of the other transaction commits its database, and the process ends before it commits the repository. An instance that goes
            // away with the transaction open leaves what a process that ended there leaves: the commit and the disposal that follow release the
            // transaction's handles and finish nothing.
            repo.Dispose();

            await Should.ThrowAsync<ObjectDisposedException>(pending.CommitAsync);
            await pending.DisposeAsync();

            failures.ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }

        // The row of the file is gone, so the application's answer is to delete the file, provided that the cleaner has something to ask about.
        var reopened = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);

        try
        {
            var asked = new List<FileId>();

            await reopened.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([fileId]);
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            reopened.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A transaction that has deleted one file and has the deletion pending through the file's indeterminate marker.
    /// </summary>
    private sealed class PendingDeletion(IAsyncDisposable transaction, Func<Task> commit, Func<Task> rollback) : IAsyncDisposable
    {
        public Task CommitAsync() => commit();

        public Task RollbackAsync() => rollback();

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    /// <summary>
    /// Deletes a file in a new transaction and leaves the deletion pending through the file's marker: an ordinary transaction that has deleted the file, or
    /// a staged transaction that has been staged, which writes the marker on a version 1 repository.
    /// </summary>
    private async Task<PendingDeletion> DeleteInNewTransactionAsync(FileRepo repo, bool staged, FileId fileId)
    {
        if (staged)
        {
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version1);

            var stagedTxn = await repo.BeginStagedTransactionAsync();
            await stagedTxn.DeleteAsync(fileId, TestContext.CancellationToken);
            var stage = await stagedTxn.StageAsync(TestContext.CancellationToken);

            return new PendingDeletion(
                stagedTxn, () => stage.CommitAsync(TestContext.CancellationToken), () => stagedTxn.RollbackAsync(TestContext.CancellationToken));
        }

        var txn = await repo.BeginTransactionAsync();
        await txn.DeleteAsync(fileId, TestContext.CancellationToken);

        return new PendingDeletion(txn, () => txn.CommitAsync(TestContext.CancellationToken), () => txn.RollbackAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// Asserts that a file is in the repository with nothing marking it.
    /// </summary>
    private static void ShouldBeCommitted(IAbsoluteDirectoryPath repoDir, FileId fileId)
    {
        FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        DeleteMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
    }

    /// <summary>
    /// Asserts that the deletion of a file committed on its own and left nothing else behind: the file is gone in immediate mode, and has a delete marker and
    /// no indeterminate marker in a deferred mode.
    /// </summary>
    private static void ShouldBeDeleted(IAbsoluteDirectoryPath repoDir, FileId fileId, DeleteMode deleteMode)
    {
        if (deleteMode is DeleteMode.Immediate)
        {
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        else
        {
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(DeleteMarker(repoDir, fileId));
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if a marker exists and somebody has it open, which is how the cleaner tells that its transaction is active.
    /// </summary>
    private static bool IsHeld(IAbsoluteFilePath file)
    {
        if (file.State is not EntryState.Exists)
            return false;

        try
        {
            using var probe = file.OpenStream(FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static List<RepoTransactionCompletionFailureInfo> TrackCompletionFailures(FileRepo repo)
    {
        var failures = new List<RepoTransactionCompletionFailureInfo>();

        repo.TransactionCompletionFailed += failure =>
        {
            lock (failures)
                failures.Add(failure);

            return Task.CompletedTask;
        };

        return failures;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    private static FileRepo CreateRepo(IAbsoluteDirectoryPath repoDir, DeleteMode deleteMode, RepoFormatVersion formatVersion)
    {
        var repo = new FileRepo(repoDir, formatVersion, o => {
            o.DeleteMode = deleteMode;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });

        repo.EnsureCreated();
        repo.FormatVersion.ShouldBe(formatVersion);
        return repo;
    }

    private static IAbsoluteDirectoryPath CleanupDir(IAbsoluteDirectoryPath repoDir) =>
        repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);

    private static IAbsoluteDirectoryPath FileDir(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);

    private static IAbsoluteFilePath IndeterminateMarker(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        CleanupDir(repoDir).CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);

    private static IAbsoluteFilePath DeleteMarker(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        CleanupDir(repoDir).CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_PendingDeleteMarker_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
