using System.Globalization;
using FulcrumFS.Utilities;

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
    // A marker that records more than the deletions pending through it
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A deletion whose commit fails before it has recorded the deletion leaves the file's marker as the only record of a deletion whose database has
    /// committed. Another pending deletion of the file that is rolled back afterwards must leave the marker for the cleaner, although no deletion is pending
    /// through it any more. One that commits records the deletion itself and removes the marker, as always.
    /// </summary>
    [TestMethod]
    [DataRow(false, DeleteMode.Immediate, false)]
    [DataRow(false, DeleteMode.Immediate, true)]
    [DataRow(false, DeleteMode.DeferredUntilClean, false)]
    [DataRow(false, DeleteMode.DeferredUntilClean, true)]
    [DataRow(true, DeleteMode.Immediate, false)]
    [DataRow(true, DeleteMode.Immediate, true)]
    [DataRow(true, DeleteMode.DeferredUntilClean, false)]
    [DataRow(true, DeleteMode.DeferredUntilClean, true)]
    public async Task PendingDeletion_WhoseCommitFails_KeepsTheMarker_WhenAnotherIsRolledBack(bool staged, DeleteMode deleteMode, bool otherCommits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, staged ? RepoFormatVersion.Version1 : RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);

            await using var failing = await DeleteInNewTransactionAsync(repo, staged, fileId);
            await using var other = await DeleteInNewTransactionAsync(repo, staged, fileId);

            // The application of the first transaction commits its database, and the repository commit fails before it has recorded the deletion: it could
            // neither carry the deletion out nor write the file's delete marker. The failure is reported, and the marker is all that records the deletion.
            int commits = 0;

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.DeleteCommitStarted && Interlocked.Increment(ref commits) is 1)
                    throw new IOException("Simulated failure to record a deletion.");
            };

            await failing.CommitAsync();
            repo.DebugStepHook = null;

            failures.ShouldHaveSingleItem().Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
            IsHeld(marker).ShouldBeTrue();

            if (otherCommits)
            {
                await other.CommitAsync();

                ShouldBeDeleted(repoDir, fileId, deleteMode);
                failures.ShouldHaveSingleItem();
                return;
            }

            await other.RollbackAsync();

            // No deletion is pending any more, but the marker records the deletion whose commit failed, so it stays for the cleaner.
            marker.State.ShouldBe(EntryState.Exists);
            IsHeld(marker).ShouldBeFalse();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            // The file's row is gone, so the application's answer is to delete the file.
            (await CleanAnsweringAsync(repo, IndeterminateResolution.Delete)).ShouldBe([fileId]);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldHaveSingleItem();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    /// <summary>
    /// A marker that was on disk before a deletion of this instance opened it, here left by a process that ended with the deletion of the file pending after
    /// its database had committed, records something that none of this instance's deletions knows about. Their rollbacks leave it for the cleaner, which asks
    /// about the file. A deletion that commits records the deletion itself and removes the marker, as always.
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task MarkerOnDiskBeforeADeletionOpenedIt_IsLeftByTheRollbacks_ForTheCleaner(bool staged, bool anotherCommits)
    {
        var repoDir = NewRepoDir();
        var formatVersion = staged ? RepoFormatVersion.Version1 : RepoFormatVersion.Version2;
        var fileId = await AddFileAndEndWithItsDeletionPendingAsync(repoDir, formatVersion, staged);
        var marker = IndeterminateMarker(repoDir, fileId);

        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            IsHeld(marker).ShouldBeFalse();

            await using var rolledBack = await DeleteInNewTransactionAsync(repo, staged, fileId);

            if (anotherCommits)
            {
                await using var committing = await DeleteInNewTransactionAsync(repo, staged, fileId);

                await rolledBack.RollbackAsync();
                IsHeld(marker).ShouldBeTrue();

                await committing.CommitAsync();

                ShouldBeDeleted(repoDir, fileId, DeleteMode.Immediate);
                failures.ShouldBeEmpty();
                return;
            }

            await rolledBack.RollbackAsync();

            marker.State.ShouldBe(EntryState.Exists);
            IsHeld(marker).ShouldBeFalse();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            (await CleanAnsweringAsync(repo, IndeterminateResolution.Delete)).ShouldBe([fileId]);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A marker that something else removes
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A deletion that takes an unheld marker while a cleanup pass is resolving the file (after the pass found the marker unheld and before it removes it:
    /// here from the resolution callback, which answers Keep because the file's row is still there) has its deletion pending through that marker, so the
    /// pass must leave the marker. The deletion then commits, or the process ends after the deletion's database has committed and the next pass deletes the
    /// file.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deletion_ThatTakesTheMarkerWhileACleanupPassResolvesTheFile_KeepsTheMarker(bool processEnds)
    {
        var repoDir = NewRepoDir();

        // A process ended with a deletion of the file pending before its database had committed: the row is still there.
        var fileId = await AddFileAndEndWithItsDeletionPendingAsync(repoDir, RepoFormatVersion.Version2, staged: false);
        var marker = IndeterminateMarker(repoDir, fileId);

        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        FileRepoTransaction? deleting = null;

        try
        {
            await repo.CleanAsync(
                TimeSpan.Zero,
                async id =>
                {
                    // The application deletes the file at this moment, and its row is still there for the answer.
                    deleting = await repo.BeginTransactionAsync();
                    await deleting.DeleteAsync(id, TestContext.CancellationToken);

                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            deleting.ShouldNotBeNull();
            marker.State.ShouldBe(EntryState.Exists);
            IsHeld(marker).ShouldBeTrue();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            if (!processEnds)
            {
                await deleting.CommitAsync(TestContext.CancellationToken);

                ShouldBeDeleted(repoDir, fileId, DeleteMode.Immediate);
                failures.ShouldBeEmpty();
                return;
            }

            // The application commits its database, and the process ends before the repository commit.
            repo.Dispose();
            await Should.ThrowAsync<ObjectDisposedException>(() => deleting.CommitAsync(TestContext.CancellationToken));
        }
        finally
        {
            if (deleting is not null)
                await deleting.DisposeAsync();

            repo.Dispose();
        }

        var reopened = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);

        try
        {
            (await CleanAnsweringAsync(reopened, IndeterminateResolution.Delete)).ShouldBe([fileId]);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            reopened.Dispose();
        }
    }

    /// <summary>
    /// A marker that something else removes just after a deletion has opened it leaves the deletion holding a marker that is no longer on disk. On Linux and
    /// macOS that is what a cleanup pass in another process can do, because opening a file and locking it are two steps there: the deletion opens the
    /// marker, the pass removes it and lets it go, and the deletion's lock then succeeds on the removed file. The deletion must notice and open the marker
    /// again, so that its deletion is pending through a marker on disk.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deletion_WhoseMarkerIsRemovedAsItIsOpened_OpensItAgain(bool commits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);
            int opened = 0;

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.DeleteMarkerOpened && Interlocked.Increment(ref opened) is 1)
                    marker.Delete();
            };

            await using var txn = await repo.BeginTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            repo.DebugStepHook = null;

            marker.State.ShouldBe(EntryState.Exists);
            IsHeld(marker).ShouldBeTrue();

            if (commits)
            {
                await txn.CommitAsync(TestContext.CancellationToken);
                ShouldBeDeleted(repoDir, fileId, DeleteMode.Immediate);
            }
            else
            {
                await txn.RollbackAsync(TestContext.CancellationToken);
                ShouldBeCommitted(repoDir, fileId);
                CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            }

            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Findings of the review of the marker rules
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An immediate commit that cannot delete the file (something has a file of the group open, or a part of it cannot be removed) records the deletion in
    /// the file's delete marker instead, and that is not a failure. The delete marker is the deletion's record from then on, so it is flushed to disk before
    /// the commit returns: the rollback of another pending deletion of the file, which no longer counts the committed one, may then remove the indeterminate
    /// marker, and the cleaner finishes the deletion from the delete marker.
    /// </summary>
    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    public async Task ImmediateCommit_ThatCannotDeleteTheFile_FlushesItsDeleteMarker_BeforeAnotherPendingDeletionIsRolledBack()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName is "root")
            throw new AssertInconclusiveException("Directory permissions do not keep root from deleting the file group.");

        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        IDisposable? blocker = null;

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var deleteMarker = DeleteMarker(repoDir, fileId);

            await using var committing = await DeleteInNewTransactionAsync(repo, staged: false, fileId);
            await using var other = await DeleteInNewTransactionAsync(repo, staged: false, fileId);

            blocker = BlockGroupDeletion(repoDir, fileId);
            var flushed = new List<string>();

            DurableIO.Observer = path =>
            {
                lock (flushed)
                    flushed.Add(path);
            };

            try
            {
                await committing.CommitAsync();
            }
            finally
            {
                DurableIO.Observer = null;
            }

            failures.ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            deleteMarker.State.ShouldBe(EntryState.Exists);
            flushed.ShouldContain(path => path.EndsWith(deleteMarker.Name, StringComparison.Ordinal));

            await other.RollbackAsync();

            // The rollback may remove the indeterminate marker, but the deletion is recorded on disk.
            deleteMarker.State.ShouldBe(EntryState.Exists);

            blocker.Dispose();
            blocker = null;

            (await CleanAnsweringAsync(repo, IndeterminateResolution.Keep)).ShouldBeEmpty();

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            DurableIO.Observer = null;
            blocker?.Dispose();
            repo.Dispose();
        }
    }

    /// <summary>
    /// A marker that a deletion creates records nothing but the deletions pending through it, even when another deletion of the file is pending through a
    /// marker that was not theirs and that a commit has since removed (having recorded the deletion first). So the last of them to be rolled back removes it,
    /// and no cleanup pass has to ask about the file.
    /// </summary>
    [TestMethod]
    public async Task MarkerCreatedAfterACommitRemovedOneThatWasNotTheirs_IsRemovedByTheLastRollback()
    {
        var repoDir = NewRepoDir();
        var fileId = await AddFileAndEndWithItsDeletionPendingAsync(repoDir, RepoFormatVersion.Version2, staged: false);
        var marker = IndeterminateMarker(repoDir, fileId);

        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            // Two deletions take the marker that was on disk, which is not theirs.
            await using var committing = await DeleteInNewTransactionAsync(repo, staged: false, fileId);
            await using var pending = await DeleteInNewTransactionAsync(repo, staged: false, fileId);

            // One commits: the deletion is recorded in the file's delete marker, and the old marker is removed.
            await committing.CommitAsync();
            marker.State.ShouldNotBe(EntryState.Exists);
            DeleteMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            // A third deletion creates a fresh marker while the second is still pending.
            await using var later = await DeleteInNewTransactionAsync(repo, staged: false, fileId);
            marker.State.ShouldBe(EntryState.Exists);

            await pending.RollbackAsync();
            await later.RollbackAsync();

            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(DeleteMarker(repoDir, fileId));

            // With a delay that keeps the delete marker, a pass has nothing to ask about.
            (await CleanAnsweringAsync(repo, IndeterminateResolution.Keep, TimeSpan.FromHours(1))).ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    /// <summary>
    /// A deletion that takes an unheld marker while a cleanup pass resolves the file, and the callback answers Delete (no record references the file): the
    /// pass deletes the file, or records its deletion with a delete delay, before it removes the marker, so the deletion is never without a record; the
    /// transaction's own deletion then has nothing left to do, and its commit reports nothing.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deletion_ThatTakesTheMarkerWhileACleanupPassDeletesTheFile_FindsTheFileDeleted(bool withDelay)
    {
        var repoDir = NewRepoDir();
        var fileId = await AddFileAndEndWithItsDeletionPendingAsync(repoDir, RepoFormatVersion.Version2, staged: false);

        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        FileRepoTransaction? deleting = null;

        try
        {
            await repo.CleanAsync(
                withDelay ? TimeSpan.FromHours(1) : TimeSpan.Zero,
                async id =>
                {
                    deleting = await repo.BeginTransactionAsync();
                    await deleting.DeleteAsync(id, TestContext.CancellationToken);

                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            deleting.ShouldNotBeNull();

            if (withDelay)
            {
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
                DeleteMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            }
            else
            {
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            }

            await deleting.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            if (deleting is not null)
                await deleting.DisposeAsync();

            repo.Dispose();
        }
    }

    /// <summary>
    /// A deletion whose marker is removed every time it opens it gives up when the wait is over, with an exception that says so, records nothing, and leaves
    /// the transaction usable.
    /// </summary>
    [TestMethod]
    public async Task Deletion_WhoseMarkerIsRemovedEachTimeItIsOpened_GivesUpWhenTheWaitIsOver()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.DeleteMarkerOpened)
                    marker.Delete();
            };

            await using var txn = await repo.BeginTransactionAsync();

            var ex = await Should.ThrowAsync<RepoFileBusyException>(() => txn.DeleteAsync(fileId, TestContext.CancellationToken));
            repo.DebugStepHook = null;

            ex.Message.ShouldBe(
                $"The deletion of file ID '{fileId}' could not be recorded because the file's indeterminate marker was removed each time it was opened, " +
                $"for {FileRepo.MarkerInUseMaxWait.TotalMilliseconds:0} ms. Nothing was recorded, and the deletion can be made again.");

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            // The transaction is usable, and the same deletion succeeds once the marker stays.
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            IsHeld(marker).ShouldBeTrue();

            await txn.RollbackAsync(TestContext.CancellationToken);
            ShouldBeCommitted(repoDir, fileId);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    /// <summary>
    /// A commit of a deletion that cannot take the file's lock (the repository was disposed, or cannot be accessed, as it commits) releases its handle
    /// without the lock, and its deletion is not recorded: the marker is its only record. The rollback of another pending deletion of the file that had
    /// already passed the repository's initialization check and was waiting for the lock must then leave the marker, although it is the last pending
    /// deletion, so that the cleaner learns of the committed deletion.
    /// </summary>
    [TestMethod]
    public async Task CommitThatCannotTakeTheFilesLock_LeavesTheMarker_WhenARollbackWasWaitingForTheLock()
    {
        // The committing transaction is a staged one on a version 1 repository: its commit reaches the repository first through the commit of the
        // deletion. An ordinary transaction reaches the same code when the repository goes away between two of its files.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version1);
        FileId fileId;

        try
        {
            fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);

            await using var committing = await repo.BeginStagedTransactionAsync();
            await committing.DeleteAsync(fileId, TestContext.CancellationToken);
            var stage = await committing.StageAsync(TestContext.CancellationToken);

            await using var rollingBack = await repo.BeginTransactionAsync();
            await rollingBack.DeleteAsync(fileId, TestContext.CancellationToken);

            // A third deletion holds the file's lock in the debug step and then finds the marker in use, so that it never counts: the rollback passes the
            // initialization check and waits for the lock while the repository is disposed.
            using var entered = new SemaphoreSlim(0);
            using var gate = new SemaphoreSlim(0);
            using var cancellation = new CancellationTokenSource();
            int opened = 0;

            repo.DebugStepHook = step =>
            {
                if (step is not DebugStep.DeleteMarkerOpened || Interlocked.Increment(ref opened) is not 1)
                    return;

                entered.Release();
                gate.Wait(TestContext.CancellationToken);

                int inUse = OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : OperatingSystem.IsLinux() ? 11 : 35;
                throw new IOException("Simulated marker in use.", inUse);
            };

            await using var holdingTheLock = await repo.BeginTransactionAsync();
            var holdingDelete = Task.Run(() => holdingTheLock.DeleteAsync(fileId, cancellation.Token), TestContext.CancellationToken);
            await entered.WaitAsync(TestContext.CancellationToken);

            var rollback = Task.Run(() => rollingBack.RollbackAsync(TestContext.CancellationToken), TestContext.CancellationToken);
            await Task.Delay(500, TestContext.CancellationToken);
            rollback.IsCompleted.ShouldBeFalse();

            // The application of the committing transaction has committed its database, and the repository is disposed (the host is stopping) as the
            // stage commits.
            repo.Dispose();
            await Should.ThrowAsync<ObjectDisposedException>(() => stage.CommitAsync(TestContext.CancellationToken));

            cancellation.Cancel();
            gate.Release();

            var ex = await Should.ThrowAsync<Exception>(() => holdingDelete);
            (ex is OperationCanceledException or RepoFileBusyException).ShouldBeTrue(ex.GetType().FullName);

            await rollback;
            repo.DebugStepHook = null;

            // The marker records the committed deletion.
            marker.State.ShouldBe(EntryState.Exists);
            IsHeld(marker).ShouldBeFalse();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }

        var reopened = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version1);

        try
        {
            (await CleanAnsweringAsync(reopened, IndeterminateResolution.Delete)).ShouldBe([fileId]);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            reopened.Dispose();
        }
    }

    /// <summary>
    /// A transaction whose commit is cut short by the repository's disposal, and which the application then disposes (its database has committed the
    /// deletion), lets go of its handle without the file's lock. The rollback of another pending deletion of the file that was already waiting for the lock
    /// must leave the marker, which is the only record of the committed deletion, because after the disposal the count no longer says what the marker
    /// records.
    /// </summary>
    [TestMethod]
    public async Task TransactionDisposedAfterItsCommitWasCutShortByDisposal_LeavesTheMarker_WhenARollbackWasWaitingForTheLock()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        FileId fileId;

        try
        {
            fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);

            var committing = await repo.BeginTransactionAsync();
            await committing.DeleteAsync(fileId, TestContext.CancellationToken);

            await using var rollingBack = await repo.BeginTransactionAsync();
            await rollingBack.DeleteAsync(fileId, TestContext.CancellationToken);

            // A third deletion holds the file's lock in the debug step and then finds the marker in use, so that it never counts: the rollback passes the
            // initialization check and waits for the lock while the repository is disposed.
            using var entered = new SemaphoreSlim(0);
            using var gate = new SemaphoreSlim(0);
            using var cancellation = new CancellationTokenSource();
            int opened = 0;

            repo.DebugStepHook = step =>
            {
                if (step is not DebugStep.DeleteMarkerOpened || Interlocked.Increment(ref opened) is not 1)
                    return;

                entered.Release();
                gate.Wait(TestContext.CancellationToken);

                int inUse = OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : OperatingSystem.IsLinux() ? 11 : 35;
                throw new IOException("Simulated marker in use.", inUse);
            };

            await using var holdingTheLock = await repo.BeginTransactionAsync();
            var holdingDelete = Task.Run(() => holdingTheLock.DeleteAsync(fileId, cancellation.Token), TestContext.CancellationToken);
            await entered.WaitAsync(TestContext.CancellationToken);

            var rollback = Task.Run(() => rollingBack.RollbackAsync(TestContext.CancellationToken), TestContext.CancellationToken);
            await Task.Delay(500, TestContext.CancellationToken);
            rollback.IsCompleted.ShouldBeFalse();

            // The application has committed its database. The repository is disposed (the host is stopping): the commit throws, and the application
            // disposes the transaction, as it does with any transaction it is done with.
            repo.Dispose();
            await Should.ThrowAsync<ObjectDisposedException>(() => committing.CommitAsync(TestContext.CancellationToken));
            await committing.DisposeAsync();

            cancellation.Cancel();
            gate.Release();

            var ex = await Should.ThrowAsync<Exception>(() => holdingDelete);
            (ex is OperationCanceledException or RepoFileBusyException).ShouldBeTrue(ex.GetType().FullName);

            await rollback;
            repo.DebugStepHook = null;

            marker.State.ShouldBe(EntryState.Exists);
            IsHeld(marker).ShouldBeFalse();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }

        var reopened = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);

        try
        {
            (await CleanAnsweringAsync(reopened, IndeterminateResolution.Delete)).ShouldBe([fileId]);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            reopened.Dispose();
        }
    }

    /// <summary>
    /// Deleting a file that the same transaction added discards the add. A failure to delete the file is the failure of a rollback: it is reported, not
    /// thrown, the file is left indeterminate for the cleaner, and the add stays discarded, so that the transaction's commit does not commit the file.
    /// </summary>
    [TestMethod]
    public async Task DiscardOfAnAdd_ThatFails_IsReportedNotThrown_AndTheCommitDoesNotCommitTheFile()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var txn = await repo.BeginTransactionAsync();
            FileId fileId;

            await using (var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport))
                fileId = (await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;

            var marker = IndeterminateMarker(repoDir, fileId);
            var failure = new IOException("Simulated failure to discard an add.");

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.AddRollbackStarted)
                    throw failure;
            };

            (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
            repo.DebugStepHook = null;

            var reported = failures.ShouldHaveSingleItem();
            reported.Operation.ShouldBe(RepoTransactionCompletionOperation.Rollback);
            reported.Exception.ShouldBeSameAs(failure);

            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            marker.State.ShouldBe(EntryState.Exists);
            IsHeld(marker).ShouldBeFalse();

            // Deleting it again does nothing, and the commit leaves it indeterminate.
            (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
            await txn.CommitAsync(TestContext.CancellationToken);
            marker.State.ShouldBe(EntryState.Exists);

            // No record references the file, so the cleaner deletes it.
            (await CleanAnsweringAsync(repo, IndeterminateResolution.Delete)).ShouldBe([fileId]);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldHaveSingleItem();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    /// <summary>
    /// An immediate staged commit that cannot delete a file is not a failure: the committed journal records the deletion, and the cleaner finishes it. The
    /// delete marker that the attempt writes is not the deletion's record there, so it is not flushed, and a flush failure could not be reported.
    /// </summary>
    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    public async Task StagedImmediateCommit_ThatCannotDeleteTheFile_IsNotAFailure_BecauseTheJournalRecordsIt()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName is "root")
            throw new AssertInconclusiveException("Directory permissions do not keep root from deleting the file group.");

        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        IDisposable? blocker = null;

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            await using var txn = await repo.BeginStagedTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            var stage = await txn.StageAsync(TestContext.CancellationToken);

            blocker = BlockGroupDeletion(repoDir, fileId);

            DurableIO.Observer = path =>
            {
                if (path.EndsWith(FileRepoPaths.DeleteMarkerExtension, StringComparison.Ordinal))
                    throw new IOException("Simulated delete marker flush failure.");
            };

            try
            {
                await stage.CommitAsync(TestContext.CancellationToken);
            }
            finally
            {
                DurableIO.Observer = null;
            }

            failures.ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.CommittedJournalExtension).ShouldHaveSingleItem();

            blocker.Dispose();
            blocker = null;

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            DurableIO.Observer = null;
            blocker?.Dispose();
            repo.Dispose();
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
    /// Adds a file and deletes it in a transaction, and ends the instance with the deletion pending, which leaves what a process that ended there leaves: the
    /// file, and its marker on disk with nobody holding it. Whether the database of the deletion had committed is up to the test: it is what the resolution
    /// callback answers.
    /// </summary>
    private async Task<FileId> AddFileAndEndWithItsDeletionPendingAsync(IAbsoluteDirectoryPath repoDir, RepoFormatVersion formatVersion, bool staged)
    {
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var pending = await DeleteInNewTransactionAsync(repo, staged, fileId);

            repo.Dispose();

            await Should.ThrowAsync<ObjectDisposedException>(pending.CommitAsync);
            await pending.DisposeAsync();

            IndeterminateMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            return fileId;
        }
        finally
        {
            repo.Dispose();
        }
    }

    /// <summary>
    /// Runs a cleanup pass, with no delete delay unless one is specified, whose resolution callback gives the specified answer, and returns the files it
    /// was asked about.
    /// </summary>
    private async Task<List<FileId>> CleanAnsweringAsync(FileRepo repo, IndeterminateResolution resolution, TimeSpan? deleteDelay = null)
    {
        var asked = new List<FileId>();

        await repo.CleanAsync(
            deleteDelay ?? TimeSpan.Zero,
            id =>
            {
                asked.Add(id);
                return resolution;
            },
            TestContext.CancellationToken);

        return asked;
    }

    /// <summary>
    /// Makes a file group's directory undeletable until the returned object is disposed: on Windows by holding a data file of the group open without
    /// sharing its deletion, elsewhere by a subdirectory without permissions that holds a file.
    /// </summary>
    private static IDisposable BlockGroupDeletion(IAbsoluteDirectoryPath repoDir, FileId fileId)
    {
        var fileDir = FileDir(repoDir, fileId);

        if (OperatingSystem.IsWindows())
            return fileDir.GetChildFiles().First().OpenStream(FileMode.Open, FileAccess.Read, FileShare.Read);

        var blocker = fileDir.CombineDirectory("blocker", PathOptions.None);
        blocker.Create();
        File.WriteAllText(blocker.CombineFile("file", PathOptions.None).PathExport, "x");
        File.SetUnixFileMode(blocker.PathExport, UnixFileMode.None);

        return new PermissionsRestorer(blocker);
    }

    private sealed class PermissionsRestorer(IAbsoluteDirectoryPath directory) : IDisposable
    {
        public void Dispose()
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory.PathExport, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
