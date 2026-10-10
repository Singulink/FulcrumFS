using System.Globalization;
using FulcrumFS.Utilities;

namespace FulcrumFS;

/// <summary>
/// Pins what has happened when a commit throws, reports a failure or is cancelled, for an ordinary transaction (<see cref="FileRepoTransaction.CommitAsync"/>)
/// and for a staged one (<see cref="FileRepoStagedTransaction.StageAsync"/>, <see cref="FileRepoStage.CommitAsync"/> and
/// <see cref="FileRepoStagedTransaction.StageAndCommitAsync"/>): the state every file of the transaction is left in, whether the caller learns of it by an
/// exception or through <see cref="FileRepo.TransactionCompletionFailed"/>, and what disposing the transaction does afterwards.
/// </summary>
/// <remarks>
/// An application commits its database before the repository, so whatever a commit of the repository does after that must never amount to "the add was rolled
/// back" or "the deletion did not happen and nothing records it". The failures of the journal's rename and of the deletion of an individual file during a
/// staged commit are covered by <see cref="FileRepoStagedTransactionTests"/>, and the journal an ordinary commit cannot write by
/// <see cref="FileRepoTransactionDeleteTests"/>.
/// </remarks>
[PrefixTestClass]
public sealed class CommitContractTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // ---------------------------------------------------------------------------------------------------------------
    // Ordinary transactions: a file that cannot be committed
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task Commit_AnAddThatCannotBeCommitted_IsReportedNotThrown_AndIsLeftIndeterminate(DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var deleted = await AddSampleFileAsync(repo);
            var failure = new IOException("Simulated failure to commit an add.");

            var txn = await repo.BeginTransactionAsync();
            FileId[] added = [await AddSampleFileAsync(txn), await AddSampleFileAsync(txn)];
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            FailFirst(repo, DebugStep.AddCommitStarted, failure);
            await txn.CommitAsync(TestContext.CancellationToken);
            repo.DebugStepHook = null;

            // The commit did not throw. It reported the failure and went on to commit everything else.
            var reported = failures.ShouldHaveSingleItem();
            reported.Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
            reported.Exception.ShouldBeSameAs(failure);

            var indeterminate = ShouldHaveOneIndeterminate(repoDir, added);
            var committed = added.Single(id => id != indeterminate);

            ShouldBeCommitted(repoDir, committed);
            ShouldBeDeleted(repoDir, deleted, deleteMode);

            // The file whose commit failed was not rolled back: it is still readable, and marked for the cleaner with a marker that nobody holds.
            (await repo.GetAsync(indeterminate)).FileId.ShouldBe(indeterminate);
            (await CollectAsync(repo)).Single(e => e.FileId == indeterminate).IsIndeterminate.ShouldBeTrue();

            // Disposing the transaction does nothing more: nothing of it is left to roll back.
            await txn.DisposeAsync();

            FileDir(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
            IndeterminateMarker(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
            ShouldBeCommitted(repoDir, committed);
            failures.ShouldHaveSingleItem();

            // The cleaner asks the application about that file only.
            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([indeterminate]);
            ShouldBeCommitted(repoDir, indeterminate);
            ShouldBeCommitted(repoDir, committed);
            FileDir(repoDir, deleted).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate, RepoFormatVersion.Version1, 2)]
    [DataRow(DeleteMode.Immediate, RepoFormatVersion.Version2, 2)]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version1, 2)]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2, 1)]
    public async Task Commit_ADeletionThatCannotBeCommitted_IsReportedNotThrown_AndLeavesTheFileIndeterminate(
        DeleteMode deleteMode, RepoFormatVersion formatVersion, int fileCount)
    {
        // Every case commits its deletions one by one: a deferred commit of two or more on a version 2 repository records them in a journal instead, which
        // FileRepoTransactionDeleteTests covers.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, fileCount);
            var failure = new IOException("Simulated failure to commit a deletion.");

            var txn = await repo.BeginTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            FailFirst(repo, DebugStep.DeleteCommitStarted, failure);
            await txn.CommitAsync(TestContext.CancellationToken);
            repo.DebugStepHook = null;

            var reported = failures.ShouldHaveSingleItem();
            reported.Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
            reported.Exception.ShouldBeSameAs(failure);

            // The deletion that failed has not happened and is not recorded as one: the file is readable and indeterminate. Everything else is committed.
            var indeterminate = ShouldHaveOneIndeterminate(repoDir, fileIds);
            DeleteMarker(repoDir, indeterminate).State.ShouldNotBe(EntryState.Exists);
            (await repo.GetAsync(indeterminate)).FileId.ShouldBe(indeterminate);

            foreach (var fileId in fileIds.Where(id => id != indeterminate))
                ShouldBeDeleted(repoDir, fileId, deleteMode);

            ShouldBeCommitted(repoDir, added);

            await txn.DisposeAsync();

            FileDir(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
            IndeterminateMarker(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
            failures.ShouldHaveSingleItem();

            // The application's database has committed the deletion, so its answer to the cleaner is to delete the file.
            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([indeterminate]);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            ShouldBeCommitted(repoDir, added);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Commit_Immediate_ADeletionThatCannotBeCompleted_IsNotAFailure_AndIsFinishedByTheCleaner()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var marker = IndeterminateMarker(repoDir, fileId);
            var blocker = CleanupDir(repoDir).CombineDirectory(marker.Name, PathOptions.None);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                // The deletion removes the file's group and then its markers. A directory in place of the indeterminate marker, put there once the
                // transaction has released the marker, is one thing it cannot remove.
                repo.DebugStepHook = step =>
                {
                    if (step is DebugStep.DeleteCommitStarted)
                    {
                        marker.Delete();
                        blocker.Create();
                    }
                };

                await txn.CommitAsync(TestContext.CancellationToken);
                repo.DebugStepHook = null;
            }

            // The deletion is committed, so nothing is reported: the delete marker records what is left of it for the cleaner, as in a deferred mode.
            failures.ShouldBeEmpty();
            DeleteMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileId));

            // A pass that cannot finish it either keeps the delete marker, and the pass after the obstacle is gone finishes it. Neither needs the callback.
            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);
            DeleteMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            blocker.Delete();
            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Commit_AnAddWhoseMarkerIsInUse_IsToleratedNotReported_AndIsResolvedByTheCleaner()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var txn = await repo.BeginTransactionAsync();
            var added = await AddSampleFileAsync(txn);
            var marker = IndeterminateMarker(repoDir, added);

            // Removing the marker is what commits the add. Something else has it open when the commit gets there (somebody reading it, a backup, a virus
            // scanner), without allowing its deletion, from the moment the transaction has released it.
            FileStream? inUse = null;

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.AddCommitStarted)
                    inUse = marker.OpenStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            };

            try
            {
                await txn.CommitAsync(TestContext.CancellationToken);
                repo.DebugStepHook = null;

                // Not a failure: the commit returns, nothing is reported, and the file is in the repository.
                inUse.ShouldNotBeNull();
                failures.ShouldBeEmpty();
                (await repo.GetAsync(added)).FileId.ShouldBe(added);

                if (!OperatingSystem.IsWindows())
                {
                    // An open handle keeps nobody from removing a file here, so the marker went and the add is committed.
                    ShouldBeCommitted(repoDir, added);
                    return;
                }

                // The marker could not be removed, so the file is left indeterminate, as accessible as a committed one.
                marker.State.ShouldBe(EntryState.Exists);
                (await CollectAsync(repo)).Single(e => e.FileId == added).IsIndeterminate.ShouldBeTrue();
            }
            finally
            {
                repo.DebugStepHook = null;

                if (inUse is not null)
                    await inUse.DisposeAsync();
            }

            // The next cleanup pass asks the application about the file, as about any indeterminate file, and removes the marker.
            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([added]);
            ShouldBeCommitted(repoDir, added);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ordinary transactions: a transaction that has been disposed
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Commit_OfADisposedTransaction_ThrowsObjectDisposed_AndCommitsNothing(bool committedFirst)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var existing = await AddSampleFileAsync(repo);

            var txn = await repo.BeginTransactionAsync();
            var added = await AddSampleFileAsync(txn);
            await txn.DeleteAsync(existing, TestContext.CancellationToken);

            if (committedFirst)
                await txn.CommitAsync(TestContext.CancellationToken);

            await txn.DisposeAsync();

            // A commit that returned normally here would tell the caller of a transaction that was rolled back that its files are in the repository.
            var ex = await Should.ThrowAsync<ObjectDisposedException>(() => txn.CommitAsync(TestContext.CancellationToken));
            ex.ObjectName.ShouldBe(typeof(FileRepoTransaction).FullName);

            if (committedFirst)
            {
                ShouldBeCommitted(repoDir, added);
                ShouldBeDeleted(repoDir, existing, DeleteMode.Immediate);
            }
            else
            {
                // Disposal rolled the transaction back, and the commit did not bring anything of it back.
                FileDir(repoDir, added).State.ShouldNotBe(EntryState.Exists);
                ShouldBeCommitted(repoDir, existing);
            }

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ordinary transactions: a handler of TransactionCompletionFailed that throws
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task Commit_WhenAHandlerThrows_ThrowsOnceEveryFileHasBeenAttempted_AndNothingIsRolledBack(DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version2);
        var handlerException = new InvalidOperationException("Thrown by the handler.");
        var failures = ThrowFromCompletionFailureHandler(repo, handlerException);
        var reportedToLaterHandler = TrackCompletionFailures(repo);

        try
        {
            var deleted = await AddSampleFileAsync(repo);
            var failure = new IOException("Simulated failure to commit an add.");

            var txn = await repo.BeginTransactionAsync();
            FileId[] added = [await AddSampleFileAsync(txn), await AddSampleFileAsync(txn)];
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            FailFirst(repo, DebugStep.AddCommitStarted, failure);
            var thrown = await Should.ThrowAsync<InvalidOperationException>(() => txn.CommitAsync(TestContext.CancellationToken));
            repo.DebugStepHook = null;

            thrown.ShouldBeSameAs(handlerException);
            failures.ShouldHaveSingleItem().Exception.ShouldBeSameAs(failure);

            // The handlers are called in turn, so the one that threw kept the failure from the handler subscribed after it.
            reportedToLaterHandler.ShouldBeEmpty();

            // The call threw, but only after it had done all it could: the other add and the deletion are committed, and the add that failed is where a
            // commit that does not throw leaves it.
            var indeterminate = ShouldHaveOneIndeterminate(repoDir, added);
            var committed = added.Single(id => id != indeterminate);

            ShouldBeCommitted(repoDir, committed);
            ShouldBeDeleted(repoDir, deleted, deleteMode);
            (await repo.GetAsync(indeterminate)).FileId.ShouldBe(indeterminate);

            // Nothing is left of the transaction: committing it again does nothing and reports nothing, and disposing it rolls nothing back.
            await txn.CommitAsync(TestContext.CancellationToken);
            await txn.DisposeAsync();

            failures.ShouldHaveSingleItem();
            FileDir(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
            IndeterminateMarker(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
            ShouldBeCommitted(repoDir, committed);
            ShouldBeDeleted(repoDir, deleted, deleteMode);

            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([indeterminate]);
            ShouldBeCommitted(repoDir, indeterminate);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    public async Task Commit_WhenAHandlerThrows_BecauseTheDeleteJournalCouldNotBeWritten_TheDeletionsAreCommittedAnyway()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);
        var handlerException = new InvalidOperationException("Thrown by the handler.");
        var failures = ThrowFromCompletionFailureHandler(repo, handlerException);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 3);

            var txn = await repo.BeginTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            DurableIO.Observer = path =>
            {
                if (path.EndsWith(FileRepoPaths.StagedJournalExtension, StringComparison.Ordinal))
                    throw new IOException("Simulated journal flush failure.");
            };

            var thrown = await Should.ThrowAsync<InvalidOperationException>(() => txn.CommitAsync(TestContext.CancellationToken));
            DurableIO.Observer = null;

            thrown.ShouldBeSameAs(handlerException);
            failures.ShouldHaveSingleItem().Exception.ShouldBeOfType<IOException>().Message.ShouldBe("Simulated journal flush failure.");

            // The exception says nothing about the deletions: each of them is committed, with a delete marker of its own in place of the journal.
            foreach (var fileId in fileIds)
                ShouldBeDeleted(repoDir, fileId, DeleteMode.DeferredUntilClean);

            await txn.DisposeAsync();

            foreach (var fileId in fileIds)
                ShouldBeDeleted(repoDir, fileId, DeleteMode.DeferredUntilClean);

            // No callback is needed to finish them.
            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldHaveSingleItem();
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ordinary transactions: cancellation
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Commit_TokenCancelledOnceTheCommitHasStarted_StillCommitsEverything()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var deleted = await AddSampleFileAsync(repo);
            using var cts = new CancellationTokenSource();

            await using var txn = await repo.BeginTransactionAsync();
            FileId[] added = [await AddSampleFileAsync(txn), await AddSampleFileAsync(txn)];
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            // Cancelled when the first file is being committed, with the rest of the commit still to come.
            int commitsStarted = 0;

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.AddCommitStarted && Interlocked.Increment(ref commitsStarted) is 1)
                    cts.Cancel();
            };

            await txn.CommitAsync(cts.Token);
            repo.DebugStepHook = null;

            commitsStarted.ShouldBe(2);
            cts.IsCancellationRequested.ShouldBeTrue();

            foreach (var fileId in added)
                ShouldBeCommitted(repoDir, fileId);

            FileDir(repoDir, deleted).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Commit_TokenAlreadyCancelled_WithNoOtherCallOnTheTransactionRunning_CommitsEverything()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var deleted = await AddSampleFileAsync(repo);

            await using var txn = await repo.BeginTransactionAsync();
            var added = await AddSampleFileAsync(txn);
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            // The token only cancels the wait for another call on the same transaction, and there is none to wait for.
            await txn.CommitAsync(new CancellationToken(canceled: true));

            ShouldBeCommitted(repoDir, added);
            FileDir(repoDir, deleted).State.ShouldNotBe(EntryState.Exists);
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
    public async Task Commit_CancelledWhileAnotherCallOnTheTransactionRuns_ThrowsAndCommitsNothing(bool commitAgain)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var deleted = await AddSampleFileAsync(repo);

            var txn = await repo.BeginTransactionAsync();
            var added = await AddSampleFileAsync(txn);
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            // An add that is still running holds the transaction, so the commit has to wait for it.
            var gate = new GateProcessor();
            var runningAdd = AddSampleFileAsync(txn, gate);
            await gate.Entered.WaitAsync(TestContext.CancellationToken);

            using var cts = new CancellationTokenSource();
            var commit = txn.CommitAsync(cts.Token);
            commit.IsCompleted.ShouldBeFalse();

            await cts.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => commit);

            gate.Release();
            var addedLate = await runningAdd;

            // Nothing was committed: the transaction still holds the markers of everything it added and deleted, as before the call.
            foreach (var fileId in new[] { added, addedLate, deleted })
            {
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
                IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();
            }

            if (commitAgain)
            {
                // The transaction is still open, so it can be committed after all.
                await txn.CommitAsync(TestContext.CancellationToken);
                await txn.DisposeAsync();

                ShouldBeCommitted(repoDir, added);
                ShouldBeCommitted(repoDir, addedLate);
                FileDir(repoDir, deleted).State.ShouldNotBe(EntryState.Exists);
            }
            else
            {
                // Disposing it rolls it back, like any transaction that was not committed. This is why the commit that follows the application's database
                // commit must not be given a token that can be cancelled: the files the database now references would be discarded.
                await txn.DisposeAsync();

                FileDir(repoDir, added).State.ShouldNotBe(EntryState.Exists);
                FileDir(repoDir, addedLate).State.ShouldNotBe(EntryState.Exists);
                ShouldBeCommitted(repoDir, deleted);
            }

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ordinary transactions: the repository is disposed
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Commit_AfterTheRepositoryWasDisposed_ThrowsObjectDisposed_AndLeavesTheFilesIndeterminate()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        FileId added;
        FileId deleted;

        try
        {
            deleted = await AddSampleFileAsync(repo);

            var txn = await repo.BeginTransactionAsync();
            added = await AddSampleFileAsync(txn);
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            repo.Dispose();

            await Should.ThrowAsync<ObjectDisposedException>(() => txn.CommitAsync(TestContext.CancellationToken));
            await txn.DisposeAsync();

            // Neither committed nor rolled back, and not reported either: both files are where a process that ended before the commit would leave them.
            failures.ShouldBeEmpty();

            foreach (var fileId in new[] { added, deleted })
            {
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
                IndeterminateMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);
                IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeFalse();
            }
        }
        finally
        {
            repo.Dispose();
        }

        // The next clean, by whoever opens the repository next, resolves them through the callback.
        var reopened = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);

        try
        {
            var asked = new List<FileId>();

            await reopened.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return id == added ? IndeterminateResolution.Keep : IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([added, deleted], ignoreOrder: true);
            ShouldBeCommitted(repoDir, added);
            FileDir(repoDir, deleted).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            reopened.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Staged transactions: staging
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    [DataRow(false)]
    [DataRow(true)]
    public async Task Stage_WhenTheJournalCannotBeWritten_Throws_AndNothingIsStaged(bool stageAgain)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            DurableIO.Observer = path =>
            {
                if (path.EndsWith(FileRepoPaths.StagedJournalExtension, StringComparison.Ordinal))
                    throw new IOException("Simulated journal flush failure.");
            };

            var ex = await Should.ThrowAsync<IOException>(() => txn.StageAsync(TestContext.CancellationToken));
            DurableIO.Observer = null;

            ex.Message.ShouldBe("Simulated journal flush failure.");

            // Nothing is staged: the journal that could not be made durable is gone, and the transaction is as it was before the call.
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, added));
            IsHeld(IndeterminateMarker(repoDir, added)).ShouldBeTrue();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            if (stageAgain)
            {
                var stage = await txn.StageAsync(TestContext.CancellationToken);
                await stage.CommitAsync(TestContext.CancellationToken);
                await txn.DisposeAsync();

                ShouldBeCommitted(repoDir, added);
                fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            }
            else
            {
                // The application must not commit its database, so it abandons the transaction, which rolls it back.
                await txn.DisposeAsync();

                FileDir(repoDir, added).State.ShouldNotBe(EntryState.Exists);
                fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);
            }

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Stage_Version1_WhenAMarkerCannotBeWritten_Throws_AndRemovesTheMarkersItWrote()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version1);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 3);

            await using var txn = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            // A directory in place of the second file's marker makes staging fail after it has written the first one.
            var blocker = CleanupDir(repoDir).CombineDirectory(fileIds[1] + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
            blocker.Create();

            // The fault is thrown as it is and at once: only a marker that is in use is waited for and reported as a busy file.
            var ex = await Should.ThrowAsync<IOException>(() => txn.StageAsync(TestContext.CancellationToken));
            ex.ShouldNotBeAssignableTo<RepoFileBusyException>();

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            // Nothing was staged, so the transaction can be staged again.
            blocker.Delete();
            var stage = await txn.StageAsync(TestContext.CancellationToken);
            await stage.CommitAsync(TestContext.CancellationToken);

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Staged transactions: committing the stage
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    [DataRow(DeleteMode.Immediate, true)]
    [DataRow(DeleteMode.Immediate, false)]
    [DataRow(DeleteMode.DeferredUntilClean, true)]
    [DataRow(DeleteMode.DeferredUntilClean, false)]
    public async Task StageCommit_WhenTheRenamedJournalCannotBeFlushed_Throws_AlthoughTheJournalIsCommitted(DeleteMode deleteMode, bool commitAgain)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);
            var stagedJournal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.StagedJournalExtension).ShouldHaveSingleItem();
            var committedJournal = stagedJournal.WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None);

            // The commit flushes the journal through the handle held since staging, which keeps the name it was opened with, once it has renamed it.
            DurableIO.Observer = path =>
            {
                if (path.EndsWith(FileRepoPaths.StagedJournalExtension, StringComparison.Ordinal))
                    throw new IOException("Simulated journal flush failure.");
            };

            var ex = await Should.ThrowAsync<IOException>(() => stage.CommitAsync(TestContext.CancellationToken));
            DurableIO.Observer = null;

            ex.Message.ShouldBe("Simulated journal flush failure.");

            // The call threw after the rename: the journal has its committed name already. Nothing else has been committed, and the transaction still
            // holds the journal and the marker of its add.
            stagedJournal.State.ShouldNotBe(EntryState.Exists);
            IsHeld(committedJournal).ShouldBeTrue();
            IsHeld(IndeterminateMarker(repoDir, added)).ShouldBeTrue();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            if (commitAgain)
            {
                // The call resumes with the flush and completes the commit.
                await stage.CommitAsync(TestContext.CancellationToken);
                await txn.DisposeAsync();

                ShouldBeCommitted(repoDir, added);

                if (deleteMode is DeleteMode.Immediate)
                {
                    fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
                    CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
                }
                else
                {
                    CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(committedJournal);
                    IsHeld(committedJournal).ShouldBeFalse();
                }
            }
            else
            {
                // Disposing the transaction does not roll it back. It releases the committed journal, which deletes its files without asking anyone, and the
                // marker of the add, which is left to the callback.
                await txn.DisposeAsync();

                committedJournal.State.ShouldBe(EntryState.Exists);
                IsHeld(committedJournal).ShouldBeFalse();
                IndeterminateMarker(repoDir, added).State.ShouldBe(EntryState.Exists);
                IsHeld(IndeterminateMarker(repoDir, added)).ShouldBeFalse();
                fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);
            }

            // The callback is never asked about the deletions, only about the add if its commit was abandoned.
            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            FileId[] expectedAsked = commitAgain ? [] : [added];
            asked.ShouldBe(expectedAsked);
            ShouldBeCommitted(repoDir, added);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StageCommit_AnAddThatCannotBeCommitted_IsReportedNotThrown_AndIsLeftIndeterminate(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var deleted = await AddSampleFileAsync(repo);
            var failure = new IOException("Simulated failure to commit an add.");

            var txn = await repo.BeginStagedTransactionAsync();
            FileId[] added = [await AddSampleFileAsync(txn), await AddSampleFileAsync(txn)];
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            FailFirst(repo, DebugStep.AddCommitStarted, failure);
            await stage.CommitAsync(TestContext.CancellationToken);
            repo.DebugStepHook = null;

            var reported = failures.ShouldHaveSingleItem();
            reported.Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
            reported.Exception.ShouldBeSameAs(failure);

            var indeterminate = ShouldHaveOneIndeterminate(repoDir, added);
            var committed = added.Single(id => id != indeterminate);

            ShouldBeCommitted(repoDir, committed);
            FileDir(repoDir, deleted).State.ShouldNotBe(EntryState.Exists);
            (await repo.GetAsync(indeterminate)).FileId.ShouldBe(indeterminate);

            // The transaction is committed: committing the stage again does nothing, and disposing the transaction rolls nothing back.
            await stage.CommitAsync(TestContext.CancellationToken);
            await txn.DisposeAsync();

            failures.ShouldHaveSingleItem();
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, indeterminate));
            FileDir(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);

            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([indeterminate]);
            ShouldBeCommitted(repoDir, indeterminate);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task StageCommit_Version1_ADeletionThatCannotBeCommitted_IsReportedNotThrown_AndLeavesTheFileIndeterminate(DeleteMode deleteMode)
    {
        // A version 1 repository has no journal, so each deletion of a staged transaction commits on its own, as in an ordinary transaction. On version 2
        // the committed journal records every deletion at once, and a file that then cannot be deleted is finished by the cleaner without the callback
        // (FileRepoStagedTransactionTests).
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, RepoFormatVersion.Version1);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);
            var failure = new IOException("Simulated failure to commit a deletion.");

            var txn = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            FailFirst(repo, DebugStep.DeleteCommitStarted, failure);
            await stage.CommitAsync(TestContext.CancellationToken);
            repo.DebugStepHook = null;

            failures.ShouldHaveSingleItem().Exception.ShouldBeSameAs(failure);

            var indeterminate = ShouldHaveOneIndeterminate(repoDir, fileIds);
            DeleteMarker(repoDir, indeterminate).State.ShouldNotBe(EntryState.Exists);
            ShouldBeDeleted(repoDir, fileIds.Single(id => id != indeterminate), deleteMode);

            await txn.DisposeAsync();

            FileDir(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
            IndeterminateMarker(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);

            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([indeterminate]);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldHaveSingleItem();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StageCommit_WhenAHandlerThrows_ThrowsOnceTheTransactionIsCommitted(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var handlerException = new InvalidOperationException("Thrown by the handler.");
        var failures = ThrowFromCompletionFailureHandler(repo, handlerException);

        try
        {
            var deleted = await AddSampleFileAsync(repo);
            var failure = new IOException("Simulated failure to commit an add.");

            var txn = await repo.BeginStagedTransactionAsync();
            FileId[] added = [await AddSampleFileAsync(txn), await AddSampleFileAsync(txn)];
            await txn.DeleteAsync(deleted, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            FailFirst(repo, DebugStep.AddCommitStarted, failure);
            var thrown = await Should.ThrowAsync<InvalidOperationException>(() => stage.CommitAsync(TestContext.CancellationToken));
            repo.DebugStepHook = null;

            thrown.ShouldBeSameAs(handlerException);
            failures.ShouldHaveSingleItem().Exception.ShouldBeSameAs(failure);

            var indeterminate = ShouldHaveOneIndeterminate(repoDir, added);
            ShouldBeCommitted(repoDir, added.Single(id => id != indeterminate));
            FileDir(repoDir, deleted).State.ShouldNotBe(EntryState.Exists);

            // The transaction is committed in spite of the exception: committing the stage again does nothing, and neither does a rollback or disposal.
            await stage.CommitAsync(TestContext.CancellationToken);
            await txn.RollbackAsync(TestContext.CancellationToken);
            await txn.DisposeAsync();

            failures.ShouldHaveSingleItem();
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, indeterminate));
            FileDir(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task StageCommit_AfterTheRepositoryWasDisposed_ThrowsObjectDisposed_AndRollsNothingBack()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);
        FileId[] fileIds;
        FileId added;

        try
        {
            fileIds = await AddSampleFilesAsync(repo, 2);

            var txn = await repo.BeginStagedTransactionAsync();
            added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);
            repo.Dispose();

            await Should.ThrowAsync<ObjectDisposedException>(() => stage.CommitAsync(TestContext.CancellationToken));
            await txn.DisposeAsync();

            // The journal is committed, which needs nothing of the repository instance, and that is as far as the commit came: no file is deleted, and
            // the add is neither committed nor rolled back.
            failures.ShouldBeEmpty();

            var journal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.CommittedJournalExtension).ShouldHaveSingleItem();
            IsHeld(journal).ShouldBeFalse();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);
            IndeterminateMarker(repoDir, added).State.ShouldBe(EntryState.Exists);
            IsHeld(IndeterminateMarker(repoDir, added)).ShouldBeFalse();
        }
        finally
        {
            repo.Dispose();
        }

        // The next clean carries out the committed deletions and asks about the add only.
        var reopened = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);

        try
        {
            var asked = new List<FileId>();

            await reopened.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Keep;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([added]);
            ShouldBeCommitted(repoDir, added);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            reopened.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Staged transactions: cancellation
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StageAndStageCommit_TokenAlreadyCancelled_WithNoOtherCallOnTheTransactionRunning_StageAndCommit(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);
            var cancelled = new CancellationToken(canceled: true);
            FileId added;

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                added = await AddSampleFileAsync(txn);
                await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken);

                var stage = await txn.StageAsync(cancelled);
                ShouldBeStaged(repoDir, formatVersion, fileIds[0]);

                await stage.CommitAsync(cancelled);
            }

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await txn.DeleteAsync(fileIds[1], TestContext.CancellationToken);
                await txn.StageAndCommitAsync(cancelled);
            }

            ShouldBeCommitted(repoDir, added);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task Stage_CancelledWhileAnotherCallOnTheTransactionRuns_ThrowsAndStagesNothing(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            await using var txn = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var gate = new GateProcessor();
            var runningAdd = AddSampleFileAsync(txn, gate);
            await gate.Entered.WaitAsync(TestContext.CancellationToken);

            using var cts = new CancellationTokenSource();
            var staging = txn.StageAsync(cts.Token);
            staging.IsCompleted.ShouldBeFalse();

            await cts.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => staging);

            // Nothing was staged: no record of the deletions was written, and the transaction is still open, so the add that was running completes.
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            gate.Release();
            var added = await runningAdd;

            var stage = await txn.StageAsync(TestContext.CancellationToken);
            ShouldBeStaged(repoDir, formatVersion, fileIds);

            await stage.CommitAsync(TestContext.CancellationToken);

            ShouldBeCommitted(repoDir, added);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StageCommit_TokenCancelledOnceTheCommitHasStarted_StillCommitsEverything(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);
            using var cts = new CancellationTokenSource();

            await using var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            // Cancelled before anything of the transaction has been committed, but after the commit has started.
            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.StagedCommitStarted)
                    cts.Cancel();
            };

            await stage.CommitAsync(cts.Token);
            repo.DebugStepHook = null;

            cts.IsCancellationRequested.ShouldBeTrue();
            ShouldBeCommitted(repoDir, added);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Staged transactions: StageAndCommitAsync
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    public async Task StageAndCommit_WhenStagingFails_Throws_AndTheTransactionIsStillOpen()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            await using var txn = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            DurableIO.Observer = path =>
            {
                if (path.EndsWith(FileRepoPaths.StagedJournalExtension, StringComparison.Ordinal))
                    throw new IOException("Simulated journal flush failure.");
            };

            await Should.ThrowAsync<IOException>(() => txn.StageAndCommitAsync(TestContext.CancellationToken));
            DurableIO.Observer = null;

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            // Nothing was staged, so the same call can be made again.
            await txn.StageAndCommitAsync(TestContext.CancellationToken);

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task StageAndCommit_WhenTheCommitFails_Throws_AndTheTransactionIsNeitherRolledBackNorStagedAgain()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            var txn = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            // The journal exists once the call has staged it. A file in the way of its rename then makes the commit fail.
            IAbsoluteFilePath? blocker = null;

            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.StagedCommitStarted)
                {
                    var journal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.StagedJournalExtension).Single();
                    blocker = journal.WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None);
                    File.WriteAllBytes(blocker.PathExport, []);
                }
            };

            await Should.ThrowAsync<IOException>(() => txn.StageAndCommitAsync(TestContext.CancellationToken));
            repo.DebugStepHook = null;

            var stagedJournal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.StagedJournalExtension).ShouldHaveSingleItem();
            IsHeld(stagedJournal).ShouldBeTrue();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            // The call does not return the stage, so the commit cannot be made again; the transaction is staged, so the call cannot be either. A rollback
            // does nothing once a commit has been attempted, and disposing the transaction leaves its files to the cleaner.
            blocker.ShouldNotBeNull().Delete();

            await Should.ThrowAsync<InvalidOperationException>(() => txn.StageAndCommitAsync(TestContext.CancellationToken));
            await txn.RollbackAsync(TestContext.CancellationToken);
            IsHeld(stagedJournal).ShouldBeTrue();

            await txn.DisposeAsync();

            stagedJournal.State.ShouldBe(EntryState.Exists);
            IsHeld(stagedJournal).ShouldBeFalse();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe(fileIds, ignoreOrder: true);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
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

    /// <summary>
    /// Makes the first occurrence of a debug step in the repository throw, which fails the commit of the one file that reached it.
    /// </summary>
    private static void FailFirst(FileRepo repo, DebugStep failingStep, Exception exception)
    {
        int count = 0;

        repo.DebugStepHook = step =>
        {
            if (step == failingStep && Interlocked.Increment(ref count) is 1)
                throw exception;
        };
    }

    /// <summary>
    /// Asserts that exactly one of the specified files is indeterminate, with its group on disk and an indeterminate marker that nobody holds, and returns
    /// it.
    /// </summary>
    private static FileId ShouldHaveOneIndeterminate(IAbsoluteDirectoryPath repoDir, FileId[] fileIds)
    {
        var indeterminate = fileIds.Where(id => IndeterminateMarker(repoDir, id).State is EntryState.Exists).ShouldHaveSingleItem();

        FileDir(repoDir, indeterminate).State.ShouldBe(EntryState.Exists);
        IsHeld(IndeterminateMarker(repoDir, indeterminate)).ShouldBeFalse();

        return indeterminate;
    }

    /// <summary>
    /// Asserts that a file is in the repository with nothing marking it: an add that committed, or a file whose deletion was rolled back.
    /// </summary>
    private static void ShouldBeCommitted(IAbsoluteDirectoryPath repoDir, FileId fileId)
    {
        FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        DeleteMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
    }

    /// <summary>
    /// Asserts that the deletion of a file committed on its own: the file is gone in immediate mode, and has a delete marker and no indeterminate marker in
    /// a deferred mode.
    /// </summary>
    private static void ShouldBeDeleted(IAbsoluteDirectoryPath repoDir, FileId fileId, DeleteMode deleteMode)
    {
        IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

        if (deleteMode is DeleteMode.Immediate)
        {
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            DeleteMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        }
        else
        {
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            DeleteMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        }
    }

    /// <summary>
    /// Asserts that the deletions of the specified files are staged and held: in one staged journal on version 2, and with an indeterminate marker per file
    /// on version 1.
    /// </summary>
    private static void ShouldBeStaged(IAbsoluteDirectoryPath repoDir, RepoFormatVersion formatVersion, params FileId[] fileIds)
    {
        if (formatVersion is RepoFormatVersion.Version1)
        {
            fileIds.ShouldAllBe(id => IsHeld(IndeterminateMarker(repoDir, id)));
        }
        else
        {
            var stagedJournal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.StagedJournalExtension).ShouldHaveSingleItem();
            RepoFileSystem.TryReadJournal(stagedJournal, out var listed).ShouldBeTrue();
            listed.ShouldBe(fileIds);
            IsHeld(stagedJournal).ShouldBeTrue();
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if a marker or journal exists and somebody has it open, which is how the cleaner tells that its transaction is active.
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

    private async Task<List<RepoFileEntry>> CollectAsync(FileRepo repo)
    {
        var entries = new List<RepoFileEntry>();

        await foreach (var entry in repo.EnumerateFilesAsync(TestContext.CancellationToken))
            entries.Add(entry);

        return entries;
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

    /// <summary>
    /// Subscribes a handler that records each completion failure and then fails with the specified exception, as a handler does that wants the caller of
    /// the commit to see the failure.
    /// </summary>
    private static List<RepoTransactionCompletionFailureInfo> ThrowFromCompletionFailureHandler(FileRepo repo, Exception exception)
    {
        var failures = new List<RepoTransactionCompletionFailureInfo>();

        repo.TransactionCompletionFailed += failure =>
        {
            lock (failures)
                failures.Add(failure);

            return Task.FromException(exception);
        };

        return failures;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo) => (await AddSampleFilesAsync(repo, 1))[0];

    private async Task<FileId[]> AddSampleFilesAsync(FileRepo repo, int count)
    {
        var fileIds = new FileId[count];

        await using var txn = await repo.BeginTransactionAsync();

        for (int i = 0; i < count; i++)
            fileIds[i] = await AddSampleFileAsync(txn);

        await txn.CommitAsync(TestContext.CancellationToken);
        return fileIds;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepoTransaction txn, IFileProcessingPipelineSelector? pipeline = null)
    {
        await using var source = OpenSample();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline ?? FileProcessingPipeline.Empty, TestContext.CancellationToken);
        return added.FileId;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepoStagedTransaction txn, IFileProcessingPipelineSelector? pipeline = null)
    {
        await using var source = OpenSample();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline ?? FileProcessingPipeline.Empty, TestContext.CancellationToken);
        return added.FileId;
    }

    private static FileStream OpenSample() => File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);

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
        var dir = _appDir.CombineDirectory("RepoRoot_CommitContract_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
