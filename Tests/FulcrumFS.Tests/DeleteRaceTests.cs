using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace FulcrumFS;

/// <summary>
/// Pins what happens when one transaction deletes a file that another transaction of the same repository has added and not committed yet, in every order in
/// which the two can finish, and what a clean pass does with such a file.
/// </summary>
/// <remarks>
/// <para>
/// An application that commits its database before the repository makes the row of a new file visible before the file's add has committed, so another of its
/// transactions can delete the file in that window. What it needs is that a deletion that commits is never undone by the add: the file ends up deleted
/// whatever the adding transaction does afterwards, or the deletion is refused with an exception before anything is recorded.</para>
/// <para>
/// The local repository does one or the other depending on how the deleting transaction records its deletions. One that records them with the file's
/// indeterminate marker (an ordinary transaction when it deletes, and a staged transaction on a version 1 repository when it is staged) cannot open the
/// marker while the adding transaction holds it exclusively: it waits briefly for the add to finish, which it normally does within milliseconds, and is
/// refused with a <see cref="RepoFileBusyException"/> if the add is still open then. A staged transaction on a repository with delete journals does not
/// touch the marker, so its deletion is accepted and wins.</para>
/// </remarks>
[PrefixTestClass]
public sealed class DeleteRaceTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    // How long a debug step waits for a call on another transaction that must not get through while the step is running. The call does get through in that
    // time when the repository does not keep it out, which is what makes the test fail then.
    private static readonly TimeSpan _blockedCallWait = TimeSpan.FromMilliseconds(150);

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // ---------------------------------------------------------------------------------------------------------------
    // A deletion that waits for the add, and is refused if the add stays open
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1, false, false)]
    [DataRow(RepoFormatVersion.Version1, false, true)]
    [DataRow(RepoFormatVersion.Version1, true, false)]
    [DataRow(RepoFormatVersion.Version1, true, true)]
    [DataRow(RepoFormatVersion.Version2, false, false)]
    [DataRow(RepoFormatVersion.Version2, false, true)]
    [DataRow(RepoFormatVersion.Version2, true, false)]
    [DataRow(RepoFormatVersion.Version2, true, true)]
    public async Task OrdinaryDelete_OfAFileBeingAdded_WaitsForTheAdd_AndIsMadeAsIfTheAddHadFinishedFirst(
        RepoFormatVersion formatVersion, bool useTryDelete, bool addCommits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            await using var deleting = await repo.BeginTransactionAsync();

            // The add finishes while the deletion waits for the file's marker, which is normally a matter of milliseconds: here, when the deletion has
            // found the marker in use for the first time. The call is not made again; it goes on by itself.
            var waits = OnMarkerInUse(repo, () => CompleteAsync(adding, addCommits));

            if (addCommits)
            {
                // Accepted as the deletion of a committed file, with a marker of its own, and the commit deletes the file.
                (await DeleteAsync(deleting, fileId, useTryDelete)).ShouldBeTrue();

                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
                CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, fileId));
                IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();
            }
            else
            {
                // The add was rolled back, so there is no file: the answer of a deletion that was made after the rollback.
                if (useTryDelete)
                    (await deleting.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeFalse();
                else
                    await Should.ThrowAsync<RepoFileNotFoundException>(() => deleting.DeleteAsync(fileId, TestContext.CancellationToken));

                CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            }

            repo.DebugStepHook = null;
            waits.Value.ShouldBe(1);

            await deleting.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryDelete_OfAFileWhoseAddStaysOpen_ThrowsRepoFileBusyOnceTheWaitIsOver_AndRecordsNothing(bool useTryDelete)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            await using var deleting = await repo.BeginTransactionAsync();

            // Refused, and neither as a file that is missing nor with whatever the file system said: the file exists, and its marker is in use.
            long started = Stopwatch.GetTimestamp();
            var ex = await Should.ThrowAsync<RepoFileBusyException>(() => DeleteAsync(deleting, fileId, useTryDelete));
            var waited = Stopwatch.GetElapsedTime(started);

            ShouldSayThatTheFileIsBusy(ex, fileId);
            ShouldHaveWaitedForTheMarker(waited);

            // Nothing was recorded, and the add is undisturbed.
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, fileId));
            IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();
            (await repo.GetAsync(fileId)).FileId.ShouldBe(fileId);

            // The transaction is still usable: once the add has committed, it deletes the file, and its commit carries the deletion out.
            await adding.CommitAsync(TestContext.CancellationToken);
            ShouldBeCommitted(repoDir, fileId);

            (await DeleteAsync(deleting, fileId, useTryDelete)).ShouldBeTrue();
            await deleting.CommitAsync(TestContext.CancellationToken);

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
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryDelete_CancelledWhileItWaitsForTheAdd_ThrowsAndRecordsNothing(bool useTryDelete)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            await using var deleting = await repo.BeginTransactionAsync();
            using var cts = new CancellationTokenSource();

            // Cancelled when the deletion has found the marker in use and is about to wait.
            var waits = OnMarkerInUse(repo, cts.CancelAsync);

            if (useTryDelete)
                await Should.ThrowAsync<OperationCanceledException>(() => deleting.TryDeleteAsync(fileId, cts.Token));
            else
                await Should.ThrowAsync<OperationCanceledException>(() => deleting.DeleteAsync(fileId, cts.Token));

            repo.DebugStepHook = null;
            waits.Value.ShouldBe(1);

            // Nothing was recorded, and the add is undisturbed.
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, fileId));
            IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();

            // The transaction is as it was before the call, so it deletes the file once the add has committed.
            await adding.CommitAsync(TestContext.CancellationToken);

            (await DeleteAsync(deleting, fileId, useTryDelete)).ShouldBeTrue();
            await deleting.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryDelete_WhenTheMarkerCannotBeOpenedForAnotherReason_ThrowsAtOnce_AndNotAsBusy(bool useTryDelete)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            await using var deleting = await repo.BeginTransactionAsync();

            // Only a marker that is in use is waited for. A directory in place of the marker is a fault that waiting does not clear.
            var blocker = CleanupDir(repoDir).CombineDirectory(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
            blocker.Create();

            var waits = OnMarkerInUse(repo, () => Task.CompletedTask);

            var ex = await Should.ThrowAsync<IOException>(() => DeleteAsync(deleting, fileId, useTryDelete));
            ex.ShouldNotBeAssignableTo<RepoFileBusyException>();

            repo.DebugStepHook = null;
            waits.Value.ShouldBe(0);

            // Nothing was recorded, and the transaction deletes the file once the fault is gone.
            blocker.Delete();
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            (await DeleteAsync(deleting, fileId, useTryDelete)).ShouldBeTrue();
            await deleting.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StagedDelete_Version1_OfAFileBeingAdded_StagingWaitsForTheAdd(bool addCommits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version1);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var other = await AddSampleFileAsync(repo);

            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            // Recording the deletion writes nothing, so it is accepted here; staging is what writes the file's marker.
            await using var deleting = await repo.BeginStagedTransactionAsync();
            await deleting.DeleteAsync(other, TestContext.CancellationToken);
            (await deleting.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();

            // The add finishes while staging waits for the file's marker, and staging goes on by itself, whichever way the add finished: the deletions
            // are not checked again when they are staged, and the commit copes with a file that is gone.
            var waits = OnMarkerInUse(repo, () => CompleteAsync(adding, addCommits));

            var stage = await deleting.StageAsync(TestContext.CancellationToken);

            repo.DebugStepHook = null;
            waits.Value.ShouldBe(1);

            FileId[] staged = [other, fileId];
            CleanupDir(repoDir).GetChildFiles().ShouldBe(staged.Select(id => IndeterminateMarker(repoDir, id)), ignoreOrder: true);
            staged.ShouldAllBe(id => IsHeld(IndeterminateMarker(repoDir, id)));

            await stage.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, other).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task StagedDelete_Version1_OfAFileWhoseAddStaysOpen_StagingThrowsRepoFileBusyOnceTheWaitIsOver_AndStagesNothing()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version1);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var other = await AddSampleFileAsync(repo);

            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            await using var deleting = await repo.BeginStagedTransactionAsync();
            await deleting.DeleteAsync(other, TestContext.CancellationToken);
            (await deleting.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();

            long started = Stopwatch.GetTimestamp();
            var ex = await Should.ThrowAsync<RepoFileBusyException>(() => deleting.StageAsync(TestContext.CancellationToken));
            var waited = Stopwatch.GetElapsedTime(started);

            ShouldSayThatTheFileIsBusy(ex, fileId);
            ShouldHaveWaitedForTheMarker(waited);

            // Nothing was staged (the marker written for the other file is gone again), so the application must not commit its database. The add is
            // undisturbed.
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, fileId));
            IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();
            FileDir(repoDir, other).State.ShouldBe(EntryState.Exists);

            // The transaction is still open, so it can be staged again once the add has finished.
            await adding.CommitAsync(TestContext.CancellationToken);

            var stage = await deleting.StageAsync(TestContext.CancellationToken);
            await stage.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, other).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task StagedDelete_Version1_CancelledWhileStagingWaitsForTheAdd_ThrowsAndStagesNothing()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version1);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var other = await AddSampleFileAsync(repo);

            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            await using var deleting = await repo.BeginStagedTransactionAsync();
            await deleting.DeleteAsync(other, TestContext.CancellationToken);
            await deleting.DeleteAsync(fileId, TestContext.CancellationToken);

            using var cts = new CancellationTokenSource();
            var waits = OnMarkerInUse(repo, cts.CancelAsync);

            await Should.ThrowAsync<OperationCanceledException>(() => deleting.StageAsync(cts.Token));

            repo.DebugStepHook = null;
            waits.Value.ShouldBe(1);

            // Nothing was staged: the marker written for the other file before the wait is gone again, and the add is undisturbed.
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, fileId));
            IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();
            FileDir(repoDir, other).State.ShouldBe(EntryState.Exists);

            // The transaction is as it was before the call, so it can be staged once the add has finished.
            await adding.CommitAsync(TestContext.CancellationToken);

            var stage = await deleting.StageAsync(TestContext.CancellationToken);
            await stage.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, other).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A deletion that is accepted while the add is open
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(DeleteMode.Immediate, true, true)]
    [DataRow(DeleteMode.Immediate, true, false)]
    [DataRow(DeleteMode.Immediate, false, true)]
    [DataRow(DeleteMode.Immediate, false, false)]
    [DataRow(DeleteMode.DeferredUntilClean, true, true)]
    [DataRow(DeleteMode.DeferredUntilClean, true, false)]
    [DataRow(DeleteMode.DeferredUntilClean, false, true)]
    [DataRow(DeleteMode.DeferredUntilClean, false, false)]
    public async Task StagedDelete_OfAFileBeingAdded_IsAccepted_AndOnceItCommitsTheFileGoesWhateverTheAddDoes(
        DeleteMode deleteMode, bool deleteCommitsFirst, bool addCommits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            // A file whose add has not committed exists for a deletion. Nothing of it is touched until the deleting transaction commits.
            var deleting = await repo.BeginStagedTransactionAsync();
            (await deleting.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();

            var stage = await deleting.StageAsync(TestContext.CancellationToken);
            IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            if (deleteCommitsFirst)
            {
                await stage.CommitAsync(TestContext.CancellationToken);

                // The deletion has committed while the add is still open: the file is gone already, or recorded as deleted.
                await ShouldBeGoneOrPendingDeletionAsync(repo, repoDir, fileId, isGone: deleteMode is DeleteMode.Immediate);

                // The add finishes afterwards as if nothing had happened, and it does not bring the file back.
                await CompleteAsync(adding, addCommits);
            }
            else
            {
                await CompleteAsync(adding, addCommits);
                await stage.CommitAsync(TestContext.CancellationToken);
            }

            await adding.DisposeAsync();
            await deleting.DisposeAsync();

            // Neither transaction reported a failure, and the committed deletion stands: only a deferred deletion of a file whose add committed is still
            // on disk, as any deleted file is until the cleaner removes it.
            failures.ShouldBeEmpty();
            await ShouldBeGoneOrPendingDeletionAsync(repo, repoDir, fileId, isGone: deleteMode is DeleteMode.Immediate || !addCommits);

            // The cleaner finishes what is left without asking the application: nothing is indeterminate.
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
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileId));
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false, true, true)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, false, false)]
    [DataRow(true, true, true)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(true, false, false)]
    public async Task StagedDelete_OfAFileBeingAdded_ThatIsRolledBack_LeavesTheFileToItsAdd(
        bool rollBackAfterStaging, bool deleteRollsBackFirst, bool addCommits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            var deleting = await repo.BeginStagedTransactionAsync();
            await deleting.DeleteAsync(fileId, TestContext.CancellationToken);

            if (rollBackAfterStaging)
                await deleting.StageAsync(TestContext.CancellationToken);

            if (deleteRollsBackFirst)
            {
                await deleting.RollbackAsync(TestContext.CancellationToken);
                await CompleteAsync(adding, addCommits);
            }
            else
            {
                await CompleteAsync(adding, addCommits);
                await deleting.RollbackAsync(TestContext.CancellationToken);
            }

            await adding.DisposeAsync();
            await deleting.DisposeAsync();

            // The deletion never happened, so the file is whatever its add made of it.
            if (addCommits)
                ShouldBeCommitted(repoDir, fileId);
            else
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
    // A deletion that arrives while the add is being committed
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task OrdinaryDelete_ArrivingWhileTheAddIsCommitting_IsAcceptedOnceTheAddIsCommitted_AndKeepsItsMarkerUntilItHasFinished(
        bool deleteCommits, bool deleteFinishesFirst)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            // The adding transaction commits two files, one after the other, so that the deleting transaction can finish before that commit has returned.
            await using var adding = await repo.BeginTransactionAsync();
            FileId[] added = [await AddSampleFileAsync(adding), await AddSampleFileAsync(adding)];

            await using var deleting = await repo.BeginTransactionAsync();

            FileId? deleted = null;
            Task<bool>? deletion = null;
            bool? acceptedBeforeTheMarkerWasRemoved = null;
            ExceptionDispatchInfo? failureInHook = null;
            int addCommitsStarted = 0;

            repo.DebugStepHook = step =>
            {
                if (step is not DebugStep.AddCommitStarted)
                    return;

                if (Interlocked.Increment(ref addCommitsStarted) is 1)
                {
                    // The deletion arrives when the add's commit has released the marker of the file it is committing and has not removed it yet. A
                    // deletion that took that marker would have it removed from under it, so it must not get through until the marker is gone.
                    var fileId = added.Single(id => !IsHeld(IndeterminateMarker(repoDir, id)));

                    deleted = fileId;
                    deletion = Task.Run(() => deleting.TryDeleteAsync(fileId, TestContext.CancellationToken), TestContext.CancellationToken);
                    acceptedBeforeTheMarkerWasRemoved = deletion.Wait(_blockedCallWait, TestContext.CancellationToken);
                }
                else if (deleteFinishesFirst)
                {
                    // The commit has reached its second file, so the add of the first one is committed while the commit has not returned.
                    try
                    {
                        FinishDeletionAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        failureInHook = ExceptionDispatchInfo.Capture(ex);
                    }
                }
            };

            await adding.CommitAsync(TestContext.CancellationToken);
            repo.DebugStepHook = null;

            failureInHook?.Throw();

            if (!deleteFinishesFirst)
                await FinishDeletionAsync();

            acceptedBeforeTheMarkerWasRemoved.ShouldBe(false);
            ShouldBeCommitted(repoDir, added.Single(id => id != deleted));
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();

            async Task FinishDeletionAsync()
            {
                var fileId = deleted.ShouldNotBeNull();
                var marker = IndeterminateMarker(repoDir, fileId);

                // Accepted as the deletion of a committed file: it is pending with a marker of its own, which is on disk and held.
                (await deletion.ShouldNotBeNull()).ShouldBeTrue();

                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
                IsHeld(marker).ShouldBeTrue();

                if (deleteCommits)
                {
                    await deleting.CommitAsync(TestContext.CancellationToken);
                    FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
                }
                else
                {
                    await deleting.RollbackAsync(TestContext.CancellationToken);
                    ShouldBeCommitted(repoDir, fileId);
                }

                marker.State.ShouldNotBe(EntryState.Exists);
            }
        }
        finally
        {
            repo.DebugStepHook = null;
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The cleaner
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Clean_WhileTheAddIsOpen_NeitherAsksAboutTheFileNorTouchesIt(bool addCommits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            // The callback would have the file deleted, but the transaction holds the file's marker, so the pass skips it.
            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();

            await CompleteAsync(adding, addCommits);

            if (addCommits)
                ShouldBeCommitted(repoDir, fileId);
            else
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task Clean_WhileTheAddIsOpen_AppliesACommittedDeletionOfTheFile_AndTheAddStillFinishes(bool addCommits)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            await using (var deleting = await repo.BeginStagedTransactionAsync())
            {
                await deleting.DeleteAsync(fileId, TestContext.CancellationToken);
                await deleting.StageAndCommitAsync(TestContext.CancellationToken);
            }

            await ShouldBeGoneOrPendingDeletionAsync(repo, repoDir, fileId, isGone: false);

            // The committed journal is certain, so the pass deletes the file without asking, although its add is still open.
            var asked = new List<FileId>();

            Task CleanAsync() => repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            await CleanAsync();

            asked.ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileId));

            // The add finishes without an error either way, and leaves nothing that a later pass would ask about.
            await CompleteAsync(adding, addCommits);
            await CleanAsync();

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

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private Task CompleteAsync(FileRepoTransaction txn, bool commit) =>
        commit ? txn.CommitAsync(TestContext.CancellationToken) : txn.RollbackAsync(TestContext.CancellationToken);

    /// <summary>
    /// Runs an action the first time a deletion finds a file's marker in use and is about to wait for it, and counts how often a deletion gets there. The
    /// repository calls the hook without the file's lock, so the action can finish the transaction that holds the marker.
    /// </summary>
    private static StrongBox<int> OnMarkerInUse(FileRepo repo, Func<Task> firstTime)
    {
        var waits = new StrongBox<int>();

        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.DeleteMarkerInUse && Interlocked.Increment(ref waits.Value) is 1)
                firstTime().GetAwaiter().GetResult();
        };

        return waits;
    }

    /// <summary>
    /// Asserts that the exception of a deletion that was refused says what the repository knows: which file, that its marker is in use and what that
    /// normally means, and that nothing was recorded. The file system's exception, which names the marker's path, is inside it and not in its message.
    /// </summary>
    private static void ShouldSayThatTheFileIsBusy(RepoFileBusyException ex, FileId fileId)
    {
        ex.GetType().ShouldBe(typeof(RepoFileBusyException));

        ex.Message.ShouldBe(
            $"The deletion of file ID '{fileId}' could not be recorded because the file's indeterminate marker was still in use after " +
            $"{FileRepo.MarkerInUseMaxWait.TotalMilliseconds:0} ms, which normally means that the transaction that added the file has not committed or " +
            "rolled back yet. Nothing was recorded, and the deletion can be made again.");

        RepoFileSystem.IsInUse(ex.InnerException.ShouldBeOfType<IOException>()).ShouldBeTrue();
    }

    /// <summary>
    /// Asserts that a deletion that was refused gave up when the wait for the marker was over, not before and not long after. The bounds are loose because
    /// timers are; the time is written to the test's output.
    /// </summary>
    private void ShouldHaveWaitedForTheMarker(TimeSpan waited)
    {
        TestContext.WriteLine($"Waited {waited.TotalMilliseconds:0} ms for a marker that stayed in use.");

        waited.ShouldBeGreaterThan(FileRepo.MarkerInUseMaxWait - TimeSpan.FromMilliseconds(50));
        waited.ShouldBeLessThan(FileRepo.MarkerInUseMaxWait + TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Deletes a file with <c>TryDeleteAsync</c> or <c>DeleteAsync</c> and returns whether it existed, which <c>DeleteAsync</c> reports by not throwing.
    /// </summary>
    private async Task<bool> DeleteAsync(FileRepoTransaction txn, FileId fileId, bool useTryDelete)
    {
        if (useTryDelete)
            return await txn.TryDeleteAsync(fileId, TestContext.CancellationToken);

        await txn.DeleteAsync(fileId, TestContext.CancellationToken);
        return true;
    }

    /// <summary>
    /// Asserts what a committed deletion guarantees: the file is gone, or it is still on disk with a record of the deletion that the cleaner carries out, and
    /// is reported as pending deletion.
    /// </summary>
    private async Task ShouldBeGoneOrPendingDeletionAsync(FileRepo repo, IAbsoluteDirectoryPath repoDir, FileId fileId, bool isGone)
    {
        if (isGone)
        {
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileId));
            return;
        }

        FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

        var journal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.CommittedJournalExtension).ShouldHaveSingleItem();
        RepoFileSystem.TryReadJournal(journal, out var listed).ShouldBeTrue();
        listed.ShouldBe([fileId]);

        var entries = new List<RepoFileEntry>();

        await foreach (var entry in repo.EnumerateFilesAsync(TestContext.CancellationToken))
            entries.Add(entry);

        entries.ShouldHaveSingleItem().IsPendingDeletion.ShouldBeTrue();
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
        await using var txn = await repo.BeginTransactionAsync();
        var fileId = await AddSampleFileAsync(txn);
        await txn.CommitAsync(TestContext.CancellationToken);
        return fileId;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepoTransaction txn)
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
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
        var dir = _appDir.CombineDirectory("RepoRoot_DeleteRace_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
