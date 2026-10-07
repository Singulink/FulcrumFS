using System.Globalization;
using FulcrumFS.Utilities;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoStagedTransactionTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // ---------------------------------------------------------------------------------------------------------------
    // Staging and committing
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task Immediate_StageThenCommit_FlushesTheDeletionRecord_AndDeletesEveryFile(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 50);
            var cleanupDir = CleanupDir(repoDir);

            await using var txn = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            // Recording the deletions writes nothing.
            cleanupDir.GetChildFiles().ShouldBeEmpty();

            var flushed = RecordFlushes();
            var stage = await txn.StageAsync(TestContext.CancellationToken);
            string[] duringStage = Snapshot(flushed);

            if (formatVersion is RepoFormatVersion.Version1)
            {
                // No journal on a version 1 repository: each deletion has its indeterminate marker, flushed and held as by an ordinary transaction.
                var markers = fileIds.Select(id => IndeterminateMarker(repoDir, id)).ToList();
                cleanupDir.GetChildFiles().Select(f => f.PathExport).ShouldBe(markers.Select(m => m.PathExport), ignoreOrder: true);
                markers.ShouldAllBe(m => IsHeld(m));
                duringStage.ShouldBe(markers.SelectMany(m => new[] { m.PathExport, cleanupDir.PathExport }));
            }
            else
            {
                var stagedJournal = cleanupDir.GetChildFiles().ShouldHaveSingleItem();
                stagedJournal.Extension.ShouldBe(FileRepoPaths.StagedJournalExtension);
                RepoFileSystem.TryReadJournal(stagedJournal, out var listed).ShouldBeTrue();
                listed.ShouldBe(fileIds);

                // One flush of the journal and then of the directory holding it, for all 50 deletions.
                duringStage.ShouldBe([stagedJournal.PathExport, cleanupDir.PathExport]);
            }

            await stage.CommitAsync(TestContext.CancellationToken);
            string[] duringCommit = Snapshot(flushed)[duringStage.Length..];

            if (formatVersion is RepoFormatVersion.Version1)
            {
                // An immediate deletion is not flushed, as in an ordinary transaction.
                duringCommit.ShouldBeEmpty();
            }
            else
            {
                // The commit flushed the renamed journal through the handle held since staging (which keeps the name it was opened with), then the cleanup
                // directory. The deletions themselves are not flushed.
                duringCommit.ShouldBe([duringStage[0], cleanupDir.PathExport]);
            }

            cleanupDir.GetChildFiles().ShouldBeEmpty();

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            failures.ShouldBeEmpty();
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version1)]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2)]
    [DataRow(DeleteMode.DeferredFilesOnly, RepoFormatVersion.Version1)]
    [DataRow(DeleteMode.DeferredFilesOnly, RepoFormatVersion.Version2)]
    public async Task Deferred_CommitLeavesDeleteRecord_CleanerDeletesOnceDelayHasPassed(DeleteMode deleteMode, RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 3);

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                var stage = await txn.StageAsync(TestContext.CancellationToken);
                await stage.CommitAsync(TestContext.CancellationToken);
            }

            ShouldHaveDeleteRecord(repoDir, formatVersion, fileIds, out var records);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            await repo.CleanAsync(TimeSpan.FromHours(1), TestContext.CancellationToken);

            records.ShouldAllBe(r => r.State == EntryState.Exists);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate, RepoFormatVersion.Version1)]
    [DataRow(DeleteMode.Immediate, RepoFormatVersion.Version2)]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version1)]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2)]
    public async Task StageAndCommit_DeletesInOneCall(DeleteMode deleteMode, RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                await txn.StageAndCommitAsync(TestContext.CancellationToken);
            }

            if (deleteMode is DeleteMode.Immediate)
            {
                CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

                foreach (var fileId in fileIds)
                    FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            }
            else
            {
                ShouldHaveDeleteRecord(repoDir, formatVersion, fileIds, out _);
            }

            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    public async Task Commit_RenamesJournalWhileHoldingIt_AndReleasesItAfterwards()
    {
        // Windows only renames an open file if every handle on it shares deletion, which the journal handle does; this checks the rename really happens
        // with the handle open, on every platform.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var cleanupDir = CleanupDir(repoDir);

            await using var txn = await repo.BeginStagedTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            var stage = await txn.StageAsync(TestContext.CancellationToken);

            var stagedJournal = cleanupDir.GetChildFiles().ShouldHaveSingleItem();
            var committedJournal = stagedJournal.WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None);
            IsHeld(stagedJournal).ShouldBeTrue();

            bool? stagedExistedAtCommitFlush = null;
            bool? committedHeldAtCommitFlush = null;

            RecordFlushes(path =>
            {
                if (path == cleanupDir.PathExport)
                {
                    stagedExistedAtCommitFlush = stagedJournal.State is EntryState.Exists;
                    committedHeldAtCommitFlush = IsHeld(committedJournal);
                }
            });

            await stage.CommitAsync(TestContext.CancellationToken);

            // When the rename was flushed, the journal had its committed name and was still held by the handle opened at staging.
            stagedExistedAtCommitFlush.ShouldBe(false);
            committedHeldAtCommitFlush.ShouldBe(true);

            // Once the commit has finished, the handle is closed.
            stagedJournal.State.ShouldNotBe(EntryState.Exists);
            committedJournal.State.ShouldBe(EntryState.Exists);
            IsHeld(committedJournal).ShouldBeFalse();

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
    public async Task AddsAndDeletes_CommittedTogetherByTheStage(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var existing = await AddSampleFileAsync(repo);

            await using var txn = await repo.BeginStagedTransactionAsync();

            var kept = await AddSampleFileAsync(txn);
            var discarded = await AddSampleFileAsync(txn);

            // Deleting an add of this transaction discards it; deleting it again, or deleting a file twice, is a no-op.
            (await txn.TryDeleteAsync(discarded, TestContext.CancellationToken)).ShouldBeTrue();
            FileDir(repoDir, discarded).State.ShouldNotBe(EntryState.Exists);
            (await txn.TryDeleteAsync(discarded, TestContext.CancellationToken)).ShouldBeTrue();
            await txn.DeleteAsync(existing, TestContext.CancellationToken);
            await txn.DeleteAsync(existing, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            // Only the deletion of the existing file is staged; the add that is kept is still indeterminate until the commit.
            ShouldBeStaged(repoDir, formatVersion, [existing]);
            IndeterminateMarker(repoDir, kept).State.ShouldBe(EntryState.Exists);

            // Nothing can be added or deleted after staging, and a transaction is staged once.
            await using (var source = OpenSample())
            {
                await Should.ThrowAsync<InvalidOperationException>(async () =>
                    await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken));
            }

            await Should.ThrowAsync<InvalidOperationException>(async () => await txn.DeleteAsync(kept, TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () => await txn.TryDeleteAsync(kept, TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () => await txn.StageAsync(TestContext.CancellationToken));

            await stage.CommitAsync(TestContext.CancellationToken);

            FileDir(repoDir, kept).State.ShouldBe(EntryState.Exists);
            (await repo.GetAsync(kept)).FileId.ShouldBe(kept);
            FileDir(repoDir, existing).State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, discarded).State.ShouldNotBe(EntryState.Exists);
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
    public async Task Stage_WithNoDeletes_WritesNoJournal_AndCommitCommitsTheAdds(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            // Only the add's own indeterminate marker is in the cleanup directory.
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, added));

            await stage.CommitAsync(TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);
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
    public async Task CommitTwice_IsNoOp_AndDisposeAfterCommitDoesNothing(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var existing = await AddSampleFileAsync(repo);

            var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);
            await txn.DeleteAsync(existing, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);
            await stage.CommitAsync(TestContext.CancellationToken);
            await stage.CommitAsync(TestContext.CancellationToken);

            ShouldHaveDeleteRecord(repoDir, formatVersion, [existing], out var records);
            var record = records.ShouldHaveSingleItem();

            // Disposing a committed transaction neither removes the delete record nor rolls back the add.
            await txn.DisposeAsync();
            await stage.CommitAsync(TestContext.CancellationToken);
            await Should.ThrowAsync<ObjectDisposedException>(async () => await txn.RollbackAsync(TestContext.CancellationToken));

            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(record);
            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);
            FileDir(repoDir, existing).State.ShouldBe(EntryState.Exists);
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rolling back
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false, RepoFormatVersion.Version1)]
    [DataRow(false, RepoFormatVersion.Version2)]
    [DataRow(true, RepoFormatVersion.Version1)]
    [DataRow(true, RepoFormatVersion.Version2)]
    public async Task RollbackAfterStage_RemovesDeletionRecordAndAdds_LeavesFilesIntact(bool byDispose, RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);
            ShouldBeStaged(repoDir, formatVersion, fileIds);

            if (byDispose)
            {
                await txn.DisposeAsync();
                await Should.ThrowAsync<ObjectDisposedException>(async () => await stage.CommitAsync(TestContext.CancellationToken));
            }
            else
            {
                await txn.RollbackAsync(TestContext.CancellationToken);
                await Should.ThrowAsync<InvalidOperationException>(async () => await stage.CommitAsync(TestContext.CancellationToken));
                await txn.DisposeAsync();
            }

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            FileDir(repoDir, added).State.ShouldNotBe(EntryState.Exists);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

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
    public async Task RollbackBeforeStage_LeavesNothingOnDisk(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            await using var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            await txn.RollbackAsync(TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            FileDir(repoDir, added).State.ShouldNotBe(EntryState.Exists);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            // A rolled back transaction cannot be used further.
            await Should.ThrowAsync<InvalidOperationException>(async () => await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () => await txn.StageAsync(TestContext.CancellationToken));

            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Operations_AfterDispose_ThrowObjectDisposed()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            var txn = await repo.BeginStagedTransactionAsync();
            await txn.DisposeAsync();

            await using (var source = OpenSample())
            {
                await Should.ThrowAsync<ObjectDisposedException>(async () =>
                    await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken));
            }

            await Should.ThrowAsync<ObjectDisposedException>(async () => await txn.DeleteAsync(fileId, TestContext.CancellationToken));
            await Should.ThrowAsync<ObjectDisposedException>(async () => await txn.TryDeleteAsync(fileId, TestContext.CancellationToken));
            await Should.ThrowAsync<ObjectDisposedException>(async () => await txn.StageAsync(TestContext.CancellationToken));
            await Should.ThrowAsync<ObjectDisposedException>(async () => await txn.StageAndCommitAsync(TestContext.CancellationToken));
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Missing files
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Delete_UnknownFile_ThrowsNotFound()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);

        try
        {
            var fileId = FileId.CreateSecure();

            await using var txn = await repo.BeginStagedTransactionAsync();

            var ex = await Should.ThrowAsync<RepoFileNotFoundException>(async () => await txn.DeleteAsync(fileId, TestContext.CancellationToken));
            ex.Message.ShouldBe($"File ID '{fileId}' was not found in the repository.");
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false, false, RepoFormatVersion.Version1)]
    [DataRow(false, false, RepoFormatVersion.Version2)]
    [DataRow(false, true, RepoFormatVersion.Version1)]
    [DataRow(false, true, RepoFormatVersion.Version2)]
    [DataRow(true, false, RepoFormatVersion.Version1)]
    [DataRow(true, false, RepoFormatVersion.Version2)]
    [DataRow(true, true, RepoFormatVersion.Version1)]
    [DataRow(true, true, RepoFormatVersion.Version2)]
    public async Task DeletePendingFiles_EitherKindOfTransaction_Succeeds_AndDeletionsConverge(
        bool firstStaged,
        bool secondStaged,
        RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            // Two files, so that an ordinary transaction also records its deletions in a journal on version 2. On version 1 both kinds write a delete marker
            // per file.
            var fileIds = await AddSampleFilesAsync(repo, 2);

            await DeleteInTransactionAsync(repo, fileIds, firstStaged);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            // The files are pending deletion, and deleting them again succeeds.
            await DeleteInTransactionAsync(repo, fileIds, secondStaged);

            failures.ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            // Each transaction recorded the deletions: in a committed journal of its own on version 2, in the files' delete markers on version 1.
            var records = CleanupDir(repoDir).GetChildFiles().ToList();

            if (formatVersion is RepoFormatVersion.Version1)
            {
                records.Select(r => r.PathExport).ShouldBe(fileIds.Select(id => DeleteMarker(repoDir, id).PathExport), ignoreOrder: true);
            }
            else
            {
                records.Count.ShouldBe(2);

                foreach (var record in records)
                {
                    record.Extension.ShouldBe(FileRepoPaths.CommittedJournalExtension);
                    RepoFileSystem.TryReadJournal(record, out var listed).ShouldBeTrue();
                    listed.ShouldBe(fileIds, ignoreOrder: true);
                }
            }

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);

            // Only now are the files gone, and a delete by either kind of transaction throws.
            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                var ex = await Should.ThrowAsync<RepoFileNotFoundException>(async () => await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken));
                ex.Message.ShouldBe($"File ID '{fileIds[0]}' was not found in the repository.");
                (await txn.TryDeleteAsync(fileIds[1], TestContext.CancellationToken)).ShouldBeFalse();
            }

            await using (var txn = await repo.BeginTransactionAsync())
            {
                var ex = await Should.ThrowAsync<RepoFileNotFoundException>(async () => await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken));
                ex.Message.ShouldBe($"File ID '{fileIds[0]}' was not found in the repository.");
                (await txn.TryDeleteAsync(fileIds[1], TestContext.CancellationToken)).ShouldBeFalse();
            }

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
    public async Task TryDelete_MissingFileRecordsNothing_PendingFileIsDeletedAgain(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            // In immediate mode a pending deletion is one that could not complete; its delete marker is written by hand here.
            var pending = await AddSampleFileAsync(repo);
            var deleteMarker = DeleteMarker(repoDir, pending);
            File.WriteAllText(deleteMarker.PathExport, "DELETE");

            var unknown = FileId.CreateSecure();

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                (await txn.TryDeleteAsync(unknown, TestContext.CancellationToken)).ShouldBeFalse();
                (await txn.TryDeleteAsync(pending, TestContext.CancellationToken)).ShouldBeTrue();

                // Only the pending file's deletion was recorded.
                var stage = await txn.StageAsync(TestContext.CancellationToken);
                ShouldBeStaged(repoDir, formatVersion, [pending]);
                IndeterminateMarker(repoDir, unknown).State.ShouldNotBe(EntryState.Exists);
                deleteMarker.State.ShouldBe(EntryState.Exists);

                // Committing completes the deletion that could not complete before, and removes its delete marker with it.
                await stage.CommitAsync(TestContext.CancellationToken);
            }

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            FileDir(repoDir, pending).State.ShouldNotBe(EntryState.Exists);
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Commit failures
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task CommitFailure_JournalRenameThrows_NothingCommitted_RetrySucceeds()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            await using var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            // A file in the way of the rename makes it fail.
            var stagedJournal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.StagedJournalExtension).ShouldHaveSingleItem();
            var blocker = stagedJournal.WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None);
            File.WriteAllBytes(blocker.PathExport, []);

            await Should.ThrowAsync<IOException>(async () => await stage.CommitAsync(TestContext.CancellationToken));

            // Nothing was committed: the staged journal is still held and the add is still indeterminate.
            IsHeld(stagedJournal).ShouldBeTrue();
            IsHeld(IndeterminateMarker(repoDir, added)).ShouldBeTrue();

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            // The application committed its database before calling CommitAsync, so a rollback no longer does anything.
            await txn.RollbackAsync(TestContext.CancellationToken);
            IsHeld(stagedJournal).ShouldBeTrue();
            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);

            blocker.Delete();
            await stage.CommitAsync(TestContext.CancellationToken);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task CommitFailure_ThenDispose_LeavesFilesIndeterminateForTheCleaner()
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

            var stage = await txn.StageAsync(TestContext.CancellationToken);

            var stagedJournal = CleanupDir(repoDir).GetChildFiles("*" + FileRepoPaths.StagedJournalExtension).ShouldHaveSingleItem();
            var blocker = stagedJournal.WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None);
            File.WriteAllBytes(blocker.PathExport, []);

            await Should.ThrowAsync<IOException>(async () => await stage.CommitAsync(TestContext.CancellationToken));
            await txn.DisposeAsync();

            // Disposing did not roll back: the journal and the add's indeterminate marker are released but left in place.
            stagedJournal.State.ShouldBe(EntryState.Exists);
            IsHeld(stagedJournal).ShouldBeFalse();
            IndeterminateMarker(repoDir, added).State.ShouldBe(EntryState.Exists);
            IsHeld(IndeterminateMarker(repoDir, added)).ShouldBeFalse();
            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);

            // The cleaner resolves them against the application's state: the deleted files go, the added file stays.
            blocker.Delete();
            await repo.CleanAsync(TimeSpan.Zero, id => id == added ? IndeterminateResolution.Keep : IndeterminateResolution.Delete, TestContext.CancellationToken);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate, RepoFormatVersion.Version1)]
    [DataRow(DeleteMode.Immediate, RepoFormatVersion.Version2)]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version1)]
    [DataRow(DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2)]
    public async Task CrashBetweenStageAndCommit_CleanerResolvesTheUnheldRecordThroughTheCallback(DeleteMode deleteMode, RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 3);

            var txn = await repo.BeginStagedTransactionAsync();
            var added = await AddSampleFileAsync(txn);

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            var stage = await txn.StageAsync(TestContext.CancellationToken);
            ShouldBeStaged(repoDir, formatVersion, fileIds);

            // The process ends once the application has committed its database and before the stage commits anything: disposing after the commit has
            // started releases everything without completing it, which leaves the on-disk state of a crash.
            repo.DebugStepHook = step =>
            {
                if (step is DebugStep.StagedCommitStarted)
                    throw new InvalidOperationException("Simulated crash.");
            };

            await Should.ThrowAsync<InvalidOperationException>(async () => await stage.CommitAsync(TestContext.CancellationToken));
            repo.DebugStepHook = null;
            await txn.DisposeAsync();

            ShouldBeStaged(repoDir, formatVersion, fileIds, held: false);
            IsHeld(IndeterminateMarker(repoDir, added)).ShouldBeFalse();

            // Every file the transaction touched is reported as indeterminate, whichever way its deletions were recorded.
            var entries = await CollectAsync(repo);
            entries.Select(e => e.FileId).ShouldBe([.. fileIds, added], ignoreOrder: true);
            entries.ShouldAllBe(e => e.IsIndeterminate && !e.IsPendingDeletion);

            // A pass without a callback cannot tell whether the transaction committed, so it leaves everything alone.
            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            ShouldBeStaged(repoDir, formatVersion, fileIds, held: false);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            // The callback resolves every file against the application's state: the database committed, so the deleted files go and the added one stays.
            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return id == added ? IndeterminateResolution.Keep : IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([.. fileIds, added], ignoreOrder: true);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            FileDir(repoDir, added).State.ShouldBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task CommitFailure_DeletionThrows_KeepsTheJournal_AndReportsTheFailure()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, RepoFormatVersion.Version2);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                var stage = await txn.StageAsync(TestContext.CancellationToken);

                // A directory in place of the first file's delete marker makes its deletion throw: the marker can neither be removed nor have the failure
                // logged to it.
                var blocker = CleanupDir(repoDir).CombineDirectory(fileIds[0] + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
                blocker.Create();

                try
                {
                    await stage.CommitAsync(TestContext.CancellationToken);
                }
                finally
                {
                    blocker.Delete();
                }
            }

            // The commit did not throw. It reported the failure and kept the committed journal, the record of the deletion that did not complete.
            failures.ShouldHaveSingleItem().Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().Extension.ShouldBe(FileRepoPaths.CommittedJournalExtension);
            FileDir(repoDir, fileIds[1]).State.ShouldNotBe(EntryState.Exists);

            // The cleaner finishes the deletion from the journal.
            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            failures.ShouldHaveSingleItem();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Concurrency
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task TwoStagedTransactions_OverlappingDeletes_BothCommit(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate, formatVersion);

        // Commit and rollback failures do not throw by default; surface them so that a failure in either commit fails the test.
        repo.TransactionCompletionFailed += failure => Task.FromException(new InvalidOperationException("Transaction completion failed.", failure.Exception));

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 20);

            await using var txnA = await repo.BeginStagedTransactionAsync();
            await using var txnB = await repo.BeginStagedTransactionAsync();

            foreach (var fileId in fileIds[..15])
                await txnA.DeleteAsync(fileId, TestContext.CancellationToken);

            foreach (var fileId in fileIds[5..])
                await txnB.DeleteAsync(fileId, TestContext.CancellationToken);

            var stageA = await txnA.StageAsync(TestContext.CancellationToken);
            var stageB = await txnB.StageAsync(TestContext.CancellationToken);

            await Task.WhenAll(stageA.CommitAsync(TestContext.CancellationToken), stageB.CommitAsync(TestContext.CancellationToken));

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Cleaner and enumeration (journals written by hand to simulate a crash)
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cleaner_UnheldStagedJournal_ResolvesEachFileThroughTheCallback(bool withDelay)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);

        try
        {
            var keep = await AddSampleFileAsync(repo);
            var delete = await AddSampleFileAsync(repo);
            var journal = WriteJournal(repoDir, FileRepoPaths.StagedJournalExtension, RepoFileSystem.FormatJournal([keep, delete]));

            var asked = new List<FileId>();
            var delay = withDelay ? TimeSpan.FromHours(1) : TimeSpan.Zero;

            await repo.CleanAsync(
                delay,
                id =>
                {
                    asked.Add(id);
                    return id == keep ? IndeterminateResolution.Keep : IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([keep, delete], ignoreOrder: true);
            journal.State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, keep).State.ShouldBe(EntryState.Exists);
            DeleteMarker(repoDir, keep).State.ShouldNotBe(EntryState.Exists);

            if (withDelay)
            {
                // A delete marker starts the delay, and a later pass deletes the file.
                FileDir(repoDir, delete).State.ShouldBe(EntryState.Exists);
                DeleteMarker(repoDir, delete).State.ShouldBe(EntryState.Exists);

                await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);
            }

            FileDir(repoDir, delete).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            FileDir(repoDir, keep).State.ShouldBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Cleaner_UnheldStagedJournal_WithoutCallback_IsLeftForAPassWithOne()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var journal = WriteJournal(repoDir, FileRepoPaths.StagedJournalExtension, RepoFileSystem.FormatJournal([fileId]));

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            journal.State.ShouldBe(EntryState.Exists);
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            await repo.CleanAsync(TimeSpan.Zero, _ => IndeterminateResolution.Delete, TestContext.CancellationToken);

            journal.State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Cleaner_HeldStagedJournal_IsSkipped()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var journal = WriteJournal(repoDir, FileRepoPaths.StagedJournalExtension, RepoFileSystem.FormatJournal([fileId]));
            var asked = new List<FileId>();

            using (journal.OpenStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                await repo.CleanAsync(
                    TimeSpan.Zero,
                    id =>
                    {
                        asked.Add(id);
                        return IndeterminateResolution.Delete;
                    },
                    TestContext.CancellationToken);
            }

            asked.ShouldBeEmpty();
            journal.State.ShouldBe(EntryState.Exists);
            FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

            // Released, the same journal is resolved.
            await repo.CleanAsync(TimeSpan.Zero, _ => IndeterminateResolution.Delete, TestContext.CancellationToken);

            journal.State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Cleaner_TornStagedJournal_IsRemoved_FilesUntouched()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);
            byte[] content = RepoFileSystem.FormatJournal(fileIds);
            var journal = WriteJournal(repoDir, FileRepoPaths.StagedJournalExtension, content[..^5]);
            var asked = new List<FileId>();

            // A torn journal was never staged, so its files are not indeterminate.
            (await CollectAsync(repo)).ShouldAllBe(e => !e.IsIndeterminate && !e.IsPendingDeletion);

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBeEmpty();
            journal.State.ShouldNotBe(EntryState.Exists);

            foreach (var fileId in fileIds)
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Cleaner_CommittedJournal_DeletesListedFiles_AndRemovesJournal()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);

        try
        {
            var existing = await AddSampleFilesAsync(repo, 10);
            var gone = await AddSampleFileAsync(repo);
            FileDir(repoDir, gone).Delete(recursive: true);

            // Leftover indeterminate markers of listed files are removed with them. With several of them, the pass is all but certain to delete some of
            // them before it reaches them in its listing of the cleanup directory.
            foreach (var fileId in existing)
                File.WriteAllBytes(IndeterminateMarker(repoDir, fileId).PathExport, []);

            var journal = WriteJournal(repoDir, FileRepoPaths.CommittedJournalExtension, RepoFileSystem.FormatJournal([.. existing, gone]));

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            foreach (var fileId in existing)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            FileDir(repoDir, gone).State.ShouldNotBe(EntryState.Exists);
            journal.State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Cleaner_TornCommittedJournal_IsLeftInPlace_AndReportedAfterTheRestOfThePass()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version2);

        try
        {
            var listed = await AddSampleFileAsync(repo);
            var deletedByMarker = await AddSampleFileAsync(repo);
            var deletedByStagedJournal = await AddSampleFileAsync(repo);

            await DeleteWithOrdinaryTransactionAsync(repo, deletedByMarker);
            WriteJournal(repoDir, FileRepoPaths.StagedJournalExtension, RepoFileSystem.FormatJournal([deletedByStagedJournal]));

            byte[] content = RepoFileSystem.FormatJournal([listed]);
            content[^3] ^= 1; // Damage the checksum.
            var journal = WriteJournal(repoDir, FileRepoPaths.CommittedJournalExtension, content);

            var ex = await Should.ThrowAsync<IOException>(async () =>
                await repo.CleanAsync(TimeSpan.Zero, _ => IndeterminateResolution.Delete, TestContext.CancellationToken));

            ex.Message.ShouldContain(journal.PathDisplay);

            // The damaged journal and its file are untouched.
            journal.State.ShouldBe(EntryState.Exists);
            File.ReadAllBytes(journal.PathExport).ShouldBe(content);
            FileDir(repoDir, listed).State.ShouldBe(EntryState.Exists);

            // The rest of the pass was processed, including the staged journals, which are resolved after every other entry.
            FileDir(repoDir, deletedByMarker).State.ShouldNotBe(EntryState.Exists);
            FileDir(repoDir, deletedByStagedJournal).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(journal);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task CommittedDeleteRecordInsideDelay_IsSkipped_AndItsFilesEnumerateAsPendingDeletion(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, formatVersion);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);
            var live = await AddSampleFileAsync(repo);

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                foreach (var fileId in fileIds)
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                var stage = await txn.StageAsync(TestContext.CancellationToken);

                // Until the transaction commits, its files are not pending deletion but indeterminate, on both formats: listed in the staged journal, or
                // marked by the per-file markers of a version 1 repository.
                var staged = await CollectAsync(repo);
                staged.ShouldAllBe(e => !e.IsPendingDeletion);
                staged.ShouldAllBe(e => e.IsIndeterminate == (e.FileId != live));

                await stage.CommitAsync(TestContext.CancellationToken);
            }

            await repo.CleanAsync(TimeSpan.FromHours(1), TestContext.CancellationToken);

            ShouldHaveDeleteRecord(repoDir, formatVersion, fileIds, out _);

            var entries = await CollectAsync(repo);
            entries.Select(e => e.FileId).ShouldBe([.. fileIds, live], ignoreOrder: true);

            foreach (var entry in entries)
            {
                entry.IsPendingDeletion.ShouldBe(entry.FileId != live);
                entry.IsIndeterminate.ShouldBeFalse();
            }

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

    private static bool IsHeld(IAbsoluteFilePath file)
    {
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

    private static List<string> RecordFlushes(Action<string>? onFlush = null)
    {
        var flushed = new List<string>();

        DurableIO.Observer = path =>
        {
            lock (flushed)
            {
                flushed.Add(path);
                onFlush?.Invoke(path);
            }
        };

        return flushed;
    }

    private static string[] Snapshot(List<string> flushed)
    {
        lock (flushed)
            return [.. flushed];
    }

    private async Task<List<RepoFileEntry>> CollectAsync(FileRepo repo)
    {
        var entries = new List<RepoFileEntry>();

        await foreach (var entry in repo.EnumerateFilesAsync(TestContext.CancellationToken))
            entries.Add(entry);

        return entries;
    }

    private async Task DeleteWithOrdinaryTransactionAsync(FileRepo repo, FileId fileId)
    {
        await using var txn = await repo.BeginTransactionAsync();
        await txn.DeleteAsync(fileId, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// Deletes files in one staged or ordinary transaction and commits it: the first file with <c>DeleteAsync</c> and the others with <c>TryDeleteAsync</c>,
    /// which must report each of them as existing.
    /// </summary>
    private async Task DeleteInTransactionAsync(FileRepo repo, FileId[] fileIds, bool staged)
    {
        if (staged)
        {
            await using var txn = await repo.BeginStagedTransactionAsync();
            await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken);

            foreach (var fileId in fileIds[1..])
                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();

            await txn.StageAndCommitAsync(TestContext.CancellationToken);
        }
        else
        {
            await using var txn = await repo.BeginTransactionAsync();
            await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken);

            foreach (var fileId in fileIds[1..])
                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();

            await txn.CommitAsync(TestContext.CancellationToken);
        }
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo) => (await AddSampleFilesAsync(repo, 1))[0];

    private async Task<FileId[]> AddSampleFilesAsync(FileRepo repo, int count)
    {
        var fileIds = new FileId[count];

        await using var txn = await repo.BeginTransactionAsync();

        for (int i = 0; i < count; i++)
        {
            await using var source = OpenSample();
            fileIds[i] = (await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;
        }

        await txn.CommitAsync(TestContext.CancellationToken);
        return fileIds;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepoStagedTransaction txn)
    {
        await using var source = OpenSample();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        return added.FileId;
    }

    private static FileStream OpenSample() => File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);

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
    /// Creates a repository at the specified format version. Staged transactions record their deletions in a journal on version 2 and with per-file markers
    /// on version 1, which is what a repository created by FulcrumFS 1.0.x or with default options has.
    /// </summary>
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

    /// <summary>
    /// Asserts that the specified deletions are staged and nothing else is: in one staged journal listing them on version 2, and with a flushed indeterminate
    /// marker per file on version 1, where no journal of any kind may exist. The records are held by the transaction unless <paramref name="held"/> is
    /// <see langword="false"/>. Indeterminate markers of files the transaction added are ignored.
    /// </summary>
    private static void ShouldBeStaged(IAbsoluteDirectoryPath repoDir, RepoFormatVersion formatVersion, FileId[] fileIds, bool held = true)
    {
        var cleanupDir = CleanupDir(repoDir);

        if (formatVersion is RepoFormatVersion.Version1)
        {
            ShouldHaveNoJournals(repoDir);

            foreach (var fileId in fileIds)
            {
                var marker = IndeterminateMarker(repoDir, fileId);
                marker.State.ShouldBe(EntryState.Exists);
                IsHeld(marker).ShouldBe(held);
            }
        }
        else
        {
            var stagedJournal = cleanupDir.GetChildFiles("*" + FileRepoPaths.StagedJournalExtension).ShouldHaveSingleItem();
            RepoFileSystem.TryReadJournal(stagedJournal, out var listed).ShouldBeTrue();
            listed.ShouldBe(fileIds);
            IsHeld(stagedJournal).ShouldBe(held);
            cleanupDir.GetChildFiles("*" + FileRepoPaths.CommittedJournalExtension).ShouldBeEmpty();

            foreach (var fileId in fileIds)
                IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        }
    }

    /// <summary>
    /// Asserts that the cleanup directory holds exactly the delete record of the specified committed deletions in a deferred delete mode: one committed journal
    /// listing them on version 2, and a delete marker per file on version 1, where no journal of any kind may exist. Returns the record files.
    /// </summary>
    private static void ShouldHaveDeleteRecord(
        IAbsoluteDirectoryPath repoDir,
        RepoFormatVersion formatVersion,
        FileId[] fileIds,
        out IReadOnlyList<IAbsoluteFilePath> records)
    {
        var entries = CleanupDir(repoDir).GetChildFiles().ToList();

        if (formatVersion is RepoFormatVersion.Version1)
        {
            ShouldHaveNoJournals(repoDir);
            entries.Select(e => e.PathExport).ShouldBe(fileIds.Select(id => DeleteMarker(repoDir, id).PathExport), ignoreOrder: true);
            records = entries;
        }
        else
        {
            // The committed journal is the only delete record: no per-file markers are written.
            var journal = entries.ShouldHaveSingleItem();
            journal.Extension.ShouldBe(FileRepoPaths.CommittedJournalExtension);
            RepoFileSystem.TryReadJournal(journal, out var listed).ShouldBeTrue();
            listed.ShouldBe(fileIds);
            records = [journal];
        }
    }

    private static void ShouldHaveNoJournals(IAbsoluteDirectoryPath repoDir)
    {
        CleanupDir(repoDir).GetChildFiles().ShouldAllBe(f =>
            f.Extension != FileRepoPaths.StagedJournalExtension && f.Extension != FileRepoPaths.CommittedJournalExtension);
    }

    private static IAbsoluteFilePath WriteJournal(IAbsoluteDirectoryPath repoDir, string extension, byte[] content)
    {
        var journal = CleanupDir(repoDir).CombineFile(Guid.CreateVersion7().ToString("D") + extension, PathOptions.None);
        File.WriteAllBytes(journal.PathExport, content);
        return journal;
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
        var dir = _appDir.CombineDirectory(
            "RepoRoot_FileRepoStagedTransaction_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
