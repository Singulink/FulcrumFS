using FulcrumFS.Utilities;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoTransactionDeleteTests
{
    private const string DeleteHeader = "DELETE";
    private const string PendingDeleteHeader = "TRANSACTION PENDING DELETE";

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task TryDelete_UnknownFileId_ReturnsFalseAndWritesNothing()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var failures = TrackCompletionFailures(repo);

        try
        {
            // A fresh repository has no shard directories, so this also covers an ID whose shard directory does not exist.
            var fileId = FileId.CreateSequential();

            await using (var txn = await repo.BeginTransactionAsync())
            {
                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeFalse();
                IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Delete_UnknownFileId_ThrowsNotFound()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = FileId.CreateSequential();

            await using var txn = await repo.BeginTransactionAsync();

            var ex = await Should.ThrowAsync<RepoFileNotFoundException>(async () => await txn.DeleteAsync(fileId, TestContext.CancellationToken));
            ex.Message.ShouldBe($"File ID '{fileId}' was not found in the repository.");

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
    public async Task TryDelete_ExistingFile_ReturnsTrueAndCommitDeletes(DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
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
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 2)]
    public async Task DeletePendingFile_Succeeds_DeletionsConverge_ThenNotFoundAfterClean(bool useTryDelete, int fileCount)
    {
        // One deletion is committed with a delete marker and two with a committed journal, so both kinds of record are deleted again.
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, fileCount);
            await DeleteAndCommitAsync(repo, fileIds);

            // The group directories are still on disk, but the files are logically deleted.
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                foreach (var fileId in fileIds)
                {
                    if (useTryDelete)
                        (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
                    else
                        await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                }

                // The second deletion is in progress beside the committed first one.
                var entries = await CollectAsync(repo);
                entries.Select(e => e.FileId).ShouldBe(fileIds, ignoreOrder: true);
                entries.ShouldAllBe(e => e.IsPendingDeletion && e.IsIndeterminate);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);
            fileIds.ShouldAllBe(id => IndeterminateMarker(repoDir, id).State != EntryState.Exists);

            // Both commits recorded the deletion: in the one delete marker, or in a journal each.
            if (fileCount is 1)
            {
                CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(DeleteMarker(repoDir, fileIds[0]));
                CountEntries(DeleteMarker(repoDir, fileIds[0]), DeleteHeader).ShouldBe(2);
            }
            else
            {
                var journals = CleanupDir(repoDir).GetChildFiles().ToList();
                journals.Count.ShouldBe(2);

                foreach (var journal in journals)
                {
                    journal.Extension.ShouldBe(FileRepoPaths.CommittedJournalExtension);
                    ReadJournal(journal).ShouldBe(fileIds, ignoreOrder: true);
                }
            }

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            await using (var txn = await repo.BeginTransactionAsync())
            {
                foreach (var fileId in fileIds)
                {
                    var ex = await Should.ThrowAsync<RepoFileNotFoundException>(async () => await txn.DeleteAsync(fileId, TestContext.CancellationToken));
                    ex.Message.ShouldBe($"File ID '{fileId}' was not found in the repository.");

                    (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeFalse();
                }

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeletePendingFile_ImmediateMode_CommitCompletesTheUnfinishedDeletion(bool useTryDelete)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate);
        var failures = TrackCompletionFailures(repo);

        try
        {
            // In immediate mode a pending deletion is one that could not complete; its delete marker is written by hand here.
            var fileId = await AddSampleFileAsync(repo);
            File.WriteAllText(DeleteMarker(repoDir, fileId).PathExport, "DELETE");

            await using (var txn = await repo.BeginTransactionAsync())
            {
                if (useTryDelete)
                    (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
                else
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            await using (var txn = await repo.BeginTransactionAsync())
            {
                var ex = await Should.ThrowAsync<RepoFileNotFoundException>(async () => await txn.DeleteAsync(fileId, TestContext.CancellationToken));
                ex.Message.ShouldBe($"File ID '{fileId}' was not found in the repository.");
            }
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(FileRepoPaths.CleanupDirectoryName)]
    [DataRow(FileRepoPaths.FilesDirectoryName)]
    public async Task Delete_RepositoryDirectoryMissing_ThrowsIOException(string directoryName)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            repoDir.CombineDirectory(directoryName, PathOptions.None).Delete(recursive: true);

            // A repository that cannot be accessed is not reported as a missing file, by either kind of transaction.
            await using (var txn = await repo.BeginTransactionAsync())
            {
                await ShouldThrowInaccessibleAsync(() => txn.DeleteAsync(fileId, TestContext.CancellationToken));
                await ShouldThrowInaccessibleAsync(() => txn.TryDeleteAsync(fileId, TestContext.CancellationToken));
            }

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await ShouldThrowInaccessibleAsync(() => txn.DeleteAsync(fileId, TestContext.CancellationToken));
                await ShouldThrowInaccessibleAsync(() => txn.TryDeleteAsync(fileId, TestContext.CancellationToken));
            }
        }
        finally
        {
            repo.Dispose();
        }

        static async Task ShouldThrowInaccessibleAsync(Func<Task> action)
        {
            var ex = await Should.ThrowAsync<IOException>(action);
            ex.ShouldBeOfType<IOException>().Message.ShouldBe("Repository directories are not accessible.");
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task DeleteTwice_SameTransaction_SecondCallIsNoOp(DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            AssertDeletedOnce(repoDir, fileId, deleteMode);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task TryDeleteTwice_SameTransaction_SecondCallReturnsTrueAndIsNoOp(DeleteMode deleteMode)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            AssertDeletedOnce(repoDir, fileId, deleteMode);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate, false)]
    [DataRow(DeleteMode.Immediate, true)]
    [DataRow(DeleteMode.DeferredUntilClean, false)]
    [DataRow(DeleteMode.DeferredUntilClean, true)]
    public async Task RepeatDelete_SameTransaction_DoesNotConsultRepository(DeleteMode deleteMode, bool useTryDelete)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            await using var txnA = await repo.BeginTransactionAsync();
            await txnA.DeleteAsync(fileId, TestContext.CancellationToken);

            // Another transaction commits a delete of the same file, so the repository now reports it as gone (immediate) or still deletable (deferred). A
            // repeated delete in the first transaction that reached the repository would throw or return false for a file that is gone, and otherwise would
            // write to the indeterminate marker again and open a second marker handle that replaces (and leaks) the first.
            await using (var txnB = await repo.BeginTransactionAsync())
            {
                await txnB.DeleteAsync(fileId, TestContext.CancellationToken);
                await txnB.CommitAsync(TestContext.CancellationToken);
            }

            int markerEntries = CountMarkerEntries(IndeterminateMarker(repoDir, fileId), PendingDeleteHeader);

            if (useTryDelete)
                (await txnA.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
            else
                await txnA.DeleteAsync(fileId, TestContext.CancellationToken);

            CountMarkerEntries(IndeterminateMarker(repoDir, fileId), PendingDeleteHeader).ShouldBe(markerEntries);

            await txnA.CommitAsync(TestContext.CancellationToken);

            failures.ShouldBeEmpty();
            IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

            if (deleteMode is DeleteMode.Immediate)
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            else
                DeleteMarker(repoDir, fileId).State.ShouldBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task TryDelete_AddedInSameTransaction_ReturnsTrueAndDiscardsAdd()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var failures = TrackCompletionFailures(repo);

        try
        {
            FileId fileId;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                fileId = await AddSampleFileAsync(txn);
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);

                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileId));
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task DeleteAgain_AfterDiscardingAdd_IsNoOp()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var failures = TrackCompletionFailures(repo);

        try
        {
            await using (var txn = await repo.BeginTransactionAsync())
            {
                var fileId = await AddSampleFileAsync(txn);
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                // The discarded file is gone from the repository, but deleting it again within the transaction behaves like any repeated delete.
                (await txn.TryDeleteAsync(fileId, TestContext.CancellationToken)).ShouldBeTrue();
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            failures.ShouldBeEmpty();
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // How a deferred commit records its deletions
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(DeleteMode.DeferredUntilClean, 1)]
    [DataRow(DeleteMode.DeferredUntilClean, 2)]
    [DataRow(DeleteMode.DeferredUntilClean, 3)]
    [DataRow(DeleteMode.DeferredFilesOnly, 1)]
    [DataRow(DeleteMode.DeferredFilesOnly, 3)]
    public async Task DeferredCommit_RecordsSeveralDeletionsInOneJournal_AndASingleDeletionWithAMarker(DeleteMode deleteMode, int fileCount)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, deleteMode);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, fileCount);
            var live = await AddSampleFileAsync(repo);

            await DeleteAndCommitAsync(repo, fileIds);

            // The cleanup directory holds the one record of the deletions and nothing else: the indeterminate markers are gone.
            var record = CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem();

            if (fileCount < FileRepo.JournalCommitMinimumDeletions)
            {
                record.ShouldBe(DeleteMarker(repoDir, fileIds[0]));
            }
            else
            {
                record.Extension.ShouldBe(FileRepoPaths.CommittedJournalExtension);
                ReadJournal(record).ShouldBe(fileIds, ignoreOrder: true);
            }

            var entries = await CollectAsync(repo);
            entries.Select(e => e.FileId).ShouldBe([.. fileIds, live], ignoreOrder: true);
            entries.ShouldAllBe(e => e.IsPendingDeletion == (e.FileId != live) && !e.IsIndeterminate);

            // The record is subject to the delete delay like any other.
            await repo.CleanAsync(TimeSpan.FromHours(1), TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(record);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            FileDir(repoDir, live).State.ShouldBe(EntryState.Exists);
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task DeferredCommit_Version1Repository_RecordsEachDeletionWithAMarker()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean, RepoFormatVersion.Version1);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 3);

            await DeleteAndCommitAsync(repo, fileIds);

            // A version 1 repository must not contain journals.
            var deleteMarkers = fileIds.Select(id => DeleteMarker(repoDir, id).PathExport);
            CleanupDir(repoDir).GetChildFiles().Select(f => f.PathExport).ShouldBe(deleteMarkers, ignoreOrder: true);

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task ImmediateCommit_SeveralDeletions_DeletesEachFile_AndWritesNoRecord()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 3);

            await DeleteAndCommitAsync(repo, fileIds);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            failures.ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    public async Task DeferredCommit_JournalCannotBeWritten_RecordsEachDeletionWithAMarker_AndReportsTheFailure()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var failures = TrackCompletionFailures(repo);

        try
        {
            var fileIds = await AddSampleFilesAsync(repo, 2);

            // Throwing from the flush observer when the journal is flushed simulates a failure to make the journal durable.
            DurableIO.Observer = path =>
            {
                if (path.EndsWith(FileRepoPaths.StagedJournalExtension, StringComparison.Ordinal))
                    throw new IOException("Simulated journal flush failure.");
            };

            await DeleteAndCommitAsync(repo, fileIds);
            DurableIO.Observer = null;

            // The commit did not throw. It reported the failure, removed the journal it could not make durable and recorded each deletion with its own marker,
            // which also removed the indeterminate markers.
            var failure = failures.ShouldHaveSingleItem();
            failure.Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
            failure.Exception.ShouldBeOfType<IOException>().Message.ShouldBe("Simulated journal flush failure.");

            var deleteMarkers = fileIds.Select(id => DeleteMarker(repoDir, id).PathExport);
            CleanupDir(repoDir).GetChildFiles().Select(f => f.PathExport).ShouldBe(deleteMarkers, ignoreOrder: true);
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State == EntryState.Exists);

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
            fileIds.ShouldAllBe(id => FileDir(repoDir, id).State != EntryState.Exists);
            failures.ShouldHaveSingleItem();
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    private static void AssertDeletedOnce(IAbsoluteDirectoryPath repoDir, FileId fileId, DeleteMode deleteMode)
    {
        IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

        if (deleteMode is DeleteMode.Immediate)
        {
            FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            DeleteMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
        }
        else
        {
            CountEntries(DeleteMarker(repoDir, fileId), DeleteHeader).ShouldBe(1);
        }
    }

    private static int CountEntries(IAbsoluteFilePath marker, string header)
    {
        // Marker files are opened with shared access by the repository, so read with matching sharing.
        using var stream = marker.OpenStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().Split($"=============== {header} ===============").Length - 1;
    }

    private static int CountMarkerEntries(IAbsoluteFilePath marker, string header) => marker.State is EntryState.Exists ? CountEntries(marker, header) : 0;

    private static IReadOnlyList<FileId> ReadJournal(IAbsoluteFilePath journal)
    {
        RepoFileSystem.TryReadJournal(journal, out var fileIds).ShouldBeTrue();
        return fileIds;
    }

    private async Task<List<RepoFileEntry>> CollectAsync(FileRepo repo)
    {
        var entries = new List<RepoFileEntry>();

        await foreach (var entry in repo.EnumerateFilesAsync(TestContext.CancellationToken))
            entries.Add(entry);

        return entries;
    }

    private async Task DeleteAndCommitAsync(FileRepo repo, FileId[] fileIds)
    {
        await using var txn = await repo.BeginTransactionAsync();

        foreach (var fileId in fileIds)
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);

        await txn.CommitAsync(TestContext.CancellationToken);
    }

    private async Task<FileId[]> AddSampleFilesAsync(FileRepo repo, int count)
    {
        var fileIds = new FileId[count];

        await using var txn = await repo.BeginTransactionAsync();

        for (int i = 0; i < count; i++)
            fileIds[i] = await AddSampleFileAsync(txn);

        await txn.CommitAsync(TestContext.CancellationToken);
        return fileIds;
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

    private static FileRepo CreateRepo(IAbsoluteDirectoryPath repoDir, DeleteMode deleteMode, RepoFormatVersion formatVersion = RepoFormatVersion.Latest)
    {
        var repo = new FileRepo(repoDir, formatVersion, o => {
            o.DeleteMode = deleteMode;
            o.MarkerFileLogging = RepoLoggingMode.HumanReadable;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });

        repo.EnsureCreated();
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
        var dir = _appDir.CombineDirectory(
            "RepoRoot_FileRepoTransactionDelete_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
