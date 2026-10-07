using FulcrumFS.Utilities;

namespace FulcrumFS;

/// <summary>
/// Verifies that the data and markers a returned call relies on are flushed to disk, in the order the durability model requires, by observing every flush
/// through <see cref="DurableIO.Observer"/>. The observer is process-wide, so the tests in this class do not run in parallel with any other test.
/// </summary>
[PrefixTestClass]
[DoNotParallelize]
public sealed class DurabilityTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task TransactionalAdd_FlushesDataFilesThenDirectories_BeforeAddReturns()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            var flushed = RecordFlushes();
            var pipeline = FileProcessingPipeline.Empty.WithVariant("thumb", VariantPipelines.RealData());

            await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
            await using var txn = await repo.BeginTransactionAsync();
            var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken);
            string[] duringAdd = Snapshot(flushed);

            await txn.CommitAsync(TestContext.CancellationToken);

            var groupDir = added.MainFile.Path.ParentDirectory;
            var innerShardDir = groupDir.ParentDirectory!;
            var outerShardDir = innerShardDir.ParentDirectory!;
            outerShardDir.ParentDirectory.ShouldBe(repo.FilesDirectory);

            // The main file and the variant are flushed at their destination paths (covering data, metadata and the rename into place), then the group
            // directory and every directory above it whose entries the add may have created.
            string[] files = [added.MainFile.Path.PathExport, added.VariantFiles.ShouldHaveSingleItem().Path.PathExport];
            string[] directories = [groupDir.PathExport, innerShardDir.PathExport, outerShardDir.PathExport, repo.FilesDirectory.PathExport];

            foreach (string path in files.Concat(directories))
                duringAdd.ShouldContain(path);

            int lastFileFlush = files.Max(f => Array.IndexOf(duringAdd, f));
            int firstDirectoryFlush = directories.Min(d => Array.IndexOf(duringAdd, d));
            lastFileFlush.ShouldBeLessThan(firstDirectoryFlush);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task VariantAdd_FlushesNewDataFileAtTempPathBeforeMove_ThenGroupDirectory()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            var groupDir = repo.FilesDirectory.Combine(fileId.RelativeDirectory);
            var destination = groupDir.CombineFile("a.jpg", PathOptions.None);
            string tempDirPath = repoDir.CombineDirectory(FileRepoPaths.TempDirectoryName, PathOptions.None).PathExport;

            string? tempFlush = null;
            bool tempFileExistedAtFlush = false;
            bool destinationExistedAtFlush = true;

            var flushed = RecordFlushes(path =>
            {
                if (tempFlush is null && path.StartsWith(tempDirPath, StringComparison.Ordinal))
                {
                    tempFlush = path;
                    tempFileExistedAtFlush = File.Exists(path);
                    destinationExistedAtFlush = destination.State is EntryState.Exists;
                }
            });

            var added = await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData(), TestContext.CancellationToken);
            string[] afterAdd = Snapshot(flushed);

            added.ShouldHaveSingleItem().Path.ShouldBe(destination);

            // The new data file was flushed in the temp directory while it was still there, before it was moved into the group directory.
            tempFlush.ShouldNotBeNull();
            tempFileExistedAtFlush.ShouldBeTrue();
            destinationExistedAtFlush.ShouldBeFalse();
            afterAdd.ShouldNotContain(destination.PathExport);

            // The group directory, which received the moved file, is flushed after it.
            Array.LastIndexOf(afterAdd, groupDir.PathExport).ShouldBeGreaterThan(Array.IndexOf(afterAdd, tempFlush));
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task DeferredDelete_FlushesIndeterminateMarkerOnDelete_AndDeleteMarkerBeforeCommitReturns()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            var cleanupDir = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);
            var indeterminateMarker = cleanupDir.CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
            var deleteMarker = cleanupDir.CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);

            bool deleteMarkerFlushed = false;
            bool? indeterminateExistedAtDeleteMarkerFlush = null;
            bool? indeterminateExistedAtCleanupDirFlush = null;

            var flushed = RecordFlushes(path =>
            {
                if (path == deleteMarker.PathExport)
                {
                    deleteMarkerFlushed = true;
                    indeterminateExistedAtDeleteMarkerFlush = indeterminateMarker.State is EntryState.Exists;
                }
                else if (deleteMarkerFlushed && path == cleanupDir.PathExport)
                {
                    indeterminateExistedAtCleanupDirFlush = indeterminateMarker.State is EntryState.Exists;
                }
            });

            await using var txn = await repo.BeginTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            string[] afterDelete = Snapshot(flushed);

            await txn.CommitAsync(TestContext.CancellationToken);
            string[] afterCommit = Snapshot(flushed);

            // DeleteAsync made the indeterminate marker durable (the file, then its directory entry) before returning.
            int indeterminateFlush = Array.IndexOf(afterDelete, indeterminateMarker.PathExport);
            indeterminateFlush.ShouldBeGreaterThanOrEqualTo(0);
            Array.IndexOf(afterDelete, cleanupDir.PathExport).ShouldBeGreaterThan(indeterminateFlush);
            afterDelete.ShouldNotContain(deleteMarker.PathExport);

            // CommitAsync made the delete marker durable (the file, then its directory entry) before removing the indeterminate marker.
            int deleteMarkerFlush = Array.IndexOf(afterCommit, deleteMarker.PathExport);
            deleteMarkerFlush.ShouldBeGreaterThan(indeterminateFlush);
            Array.LastIndexOf(afterCommit, cleanupDir.PathExport).ShouldBeGreaterThan(deleteMarkerFlush);
            indeterminateExistedAtDeleteMarkerFlush.ShouldBe(true);
            indeterminateExistedAtCleanupDirFlush.ShouldBe(true);

            deleteMarker.State.ShouldBe(EntryState.Exists);
            indeterminateMarker.State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task DeferredDelete_SeveralFiles_CommitFlushesOneJournal_BeforeRemovingIndeterminateMarkers()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            FileId[] fileIds = [await AddSampleFileAsync(repo), await AddSampleFileAsync(repo), await AddSampleFileAsync(repo)];

            var cleanupDir = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);
            var indeterminateMarkers = fileIds.Select(id => cleanupDir.CombineFile(id + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None)).ToList();

            IAbsoluteFilePath? committedJournal = null;
            bool? committedExistedAtFirstJournalFlush = null;
            bool? committedExistedAtDirectoryFlush = null;
            bool? indeterminateExistedAtDirectoryFlush = null;

            var flushed = RecordFlushes(path =>
            {
                if (committedJournal is null && path.EndsWith(FileRepoPaths.StagedJournalExtension, StringComparison.Ordinal))
                {
                    committedJournal = FilePath.ParseAbsolute(path, PathOptions.None).WithExtension(FileRepoPaths.CommittedJournalExtension, PathOptions.None);
                    committedExistedAtFirstJournalFlush = committedJournal.State is EntryState.Exists;
                }
                else if (committedJournal is not null && path == cleanupDir.PathExport)
                {
                    committedExistedAtDirectoryFlush = committedJournal.State is EntryState.Exists;
                    indeterminateExistedAtDirectoryFlush = indeterminateMarkers.All(m => m.State is EntryState.Exists);
                }
            });

            await using var txn = await repo.BeginTransactionAsync();

            foreach (var fileId in fileIds)
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            int flushesBeforeCommit = Snapshot(flushed).Length;

            await txn.CommitAsync(TestContext.CancellationToken);
            string[] duringCommit = Snapshot(flushed)[flushesBeforeCommit..];

            var journal = cleanupDir.GetChildFiles().ShouldHaveSingleItem();
            journal.ShouldBe(committedJournal);
            string stagedPath = journal.WithExtension(FileRepoPaths.StagedJournalExtension, PathOptions.None).PathExport;

            // The journal's content was flushed under its staged name before the rename (the committed name did not exist yet), then the rename through the
            // same handle, which keeps the name it was opened with, and the cleanup directory. No delete marker was written or flushed.
            duringCommit.ShouldBe([stagedPath, stagedPath, cleanupDir.PathExport]);
            committedExistedAtFirstJournalFlush.ShouldBe(false);
            committedExistedAtDirectoryFlush.ShouldBe(true);

            // The indeterminate markers were removed only once the journal was durable.
            indeterminateExistedAtDirectoryFlush.ShouldBe(true);
            indeterminateMarkers.ShouldAllBe(m => m.State != EntryState.Exists);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task ImmediateDelete_CommitFlushesNothing()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.Immediate);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var flushed = RecordFlushes();

            await using var txn = await repo.BeginTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            int flushesAfterDelete = Snapshot(flushed).Length;

            await txn.CommitAsync(TestContext.CancellationToken);

            // A deletion is never flushed: a delete undone by a power loss comes back only with the (flushed) indeterminate marker, which drives it again.
            Snapshot(flushed).Length.ShouldBe(flushesAfterDelete);
            repo.FilesDirectory.Combine(fileId.RelativeDirectory).State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task VariantRetirement_FlushesDeleteMarkers_ThenSurvivorBeforeRename_ThenGroupDirectory()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            // a = real data with b = alias -> a (b survives the retirement of a, so it is promoted); d = unrelated real data retired alongside a.
            await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias()), TestContext.CancellationToken);
            await repo.AddVariantAsync(fileId, "d", VariantPipelines.RealData(), TestContext.CancellationToken);

            var groupDir = repo.FilesDirectory.Combine(fileId.RelativeDirectory);
            string deleteMarkerA = groupDir.CombineFile("a" + FileRepoPaths.DeleteMarkerExtension, PathOptions.None).PathExport;
            string deleteMarkerD = groupDir.CombineFile("d" + FileRepoPaths.DeleteMarkerExtension, PathOptions.None).PathExport;
            var chosenAlias = groupDir.CombineFile("b.a.jpg" + FileRepoPaths.AliasMarkerExtension, PathOptions.None);
            var chosenData = groupDir.CombineFile("b.jpg", PathOptions.None);
            var rebaseMarker = groupDir.CombineFile("a.b" + FileRepoPaths.RebaseMarkerExtension, PathOptions.None);

            chosenAlias.State.ShouldBe(EntryState.Exists);

            bool aliasFlushed = false;
            bool? chosenDataExistedAtAliasFlush = null;
            bool? rebaseMarkerExistedAtGroupDirFlush = null;

            var flushed = RecordFlushes(path =>
            {
                if (path == chosenAlias.PathExport)
                {
                    aliasFlushed = true;
                    chosenDataExistedAtAliasFlush = chosenData.State is EntryState.Exists;
                }
                else if (aliasFlushed && path == groupDir.PathExport)
                {
                    rebaseMarkerExistedAtGroupDirFlush ??= rebaseMarker.State is EntryState.Exists;
                }
            });

            await repo.DeleteVariantsAsync(fileId, "a", "d");
            string[] afterDelete = Snapshot(flushed);

            // The commit point: both in-group delete markers, then the group directory holding them.
            int markerA = Array.IndexOf(afterDelete, deleteMarkerA);
            int markerD = Array.IndexOf(afterDelete, deleteMarkerD);
            markerA.ShouldBeGreaterThanOrEqualTo(0);
            markerD.ShouldBeGreaterThanOrEqualTo(0);
            int lastMarker = Math.Max(markerA, markerD);
            int commitDirFlush = Array.IndexOf(afterDelete, groupDir.PathExport, lastMarker);
            commitDirFlush.ShouldBeGreaterThan(lastMarker);

            // The promoted survivor's copy is flushed under its alias name, before the rename to the materialized data file.
            int aliasFlush = Array.IndexOf(afterDelete, chosenAlias.PathExport);
            aliasFlush.ShouldBeGreaterThan(commitDirFlush);
            chosenDataExistedAtAliasFlush.ShouldBe(false);
            afterDelete.ShouldNotContain(chosenData.PathExport);

            // The group directory is flushed after the rename and before the rebase marker is dropped.
            Array.LastIndexOf(afterDelete, groupDir.PathExport).ShouldBeGreaterThan(aliasFlush);
            rebaseMarkerExistedAtGroupDirFlush.ShouldBe(true);

            (await repo.GetVariantAsync(fileId, "b")).Path.ShouldBe(chosenData);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task CleanerResolvesIndeterminateToDeferredDelete_FlushesHintBeforeRemovingIndeterminateMarker()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            // Manufacture an indeterminate marker (simulating a crashed transaction).
            var cleanupDir = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);
            var indeterminateMarker = cleanupDir.CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
            var deleteHint = cleanupDir.CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
            using (indeterminateMarker.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            bool hintFlushed = false;
            bool? indeterminateExistedAtHintFlush = null;
            bool? indeterminateExistedAtCleanupDirFlush = null;

            var flushed = RecordFlushes(path =>
            {
                if (path == deleteHint.PathExport)
                {
                    hintFlushed = true;
                    indeterminateExistedAtHintFlush = indeterminateMarker.State is EntryState.Exists;
                }
                else if (hintFlushed && path == cleanupDir.PathExport)
                {
                    indeterminateExistedAtCleanupDirFlush = indeterminateMarker.State is EntryState.Exists;
                }
            });

            await repo.CleanAsync(TimeSpan.FromHours(1), id => IndeterminateResolution.Delete, TestContext.CancellationToken);
            string[] afterClean = Snapshot(flushed);

            int hintFlush = Array.IndexOf(afterClean, deleteHint.PathExport);
            hintFlush.ShouldBeGreaterThanOrEqualTo(0);
            Array.LastIndexOf(afterClean, cleanupDir.PathExport).ShouldBeGreaterThan(hintFlush);
            indeterminateExistedAtHintFlush.ShouldBe(true);
            indeterminateExistedAtCleanupDirFlush.ShouldBe(true);

            deleteHint.State.ShouldBe(EntryState.Exists);
            indeterminateMarker.State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public void EnsureCreated_FlushesInfoFile()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, RepoFormatVersion.Latest);

        try
        {
            var flushed = RecordFlushes();
            repo.EnsureCreated();

            Snapshot(flushed).ShouldContain(repoDir.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None).PathExport);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public void EnsureInitialized_VersionOneInfoFile_FlushesUpgradedContentBeforeRenamingIt_ThenBaseDirectory()
    {
        const string versionOne = "version=1\ncreated=2025-03-04T05:06:07.0891011Z\n";

        var repoDir = NewRepoDir();
        CreateRepo(repoDir, DeleteMode.DeferredUntilClean).Dispose();

        var infoFile = repoDir.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None);
        var tempFile = repoDir.CombineFile(FileRepoPaths.InfoFileName + ".tmp", PathOptions.None);
        File.WriteAllText(infoFile.PathExport, versionOne);

        var repo = new FileRepo(repoDir, RepoFormatVersion.Version2);

        try
        {
            string? tempContentAtFlush = null;
            string? infoContentAtTempFlush = null;

            var flushed = RecordFlushes(path =>
            {
                if (path == tempFile.PathExport)
                {
                    tempContentAtFlush = ReadShared(tempFile);
                    infoContentAtTempFlush = ReadShared(infoFile);
                }
            });

            repo.EnsureInitialized();
            string[] flushes = Snapshot(flushed);

            // The upgraded content is flushed under the temporary name while the info file still has the old content, and the base directory is flushed
            // after the rename.
            int tempFlush = Array.IndexOf(flushes, tempFile.PathExport);
            tempFlush.ShouldBeGreaterThanOrEqualTo(0);
            Array.LastIndexOf(flushes, repoDir.PathExport).ShouldBeGreaterThan(tempFlush);

            infoContentAtTempFlush.ShouldBe(versionOne);
            tempContentAtFlush.ShouldBe("version=2\nread-compat-version=1\nclean-compat-version=2\ncreated=2025-03-04T05:06:07.0891011Z\n");
            ReadShared(infoFile).ShouldBe(tempContentAtFlush);
            tempFile.State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }

        static string ReadShared(IAbsoluteFilePath file)
        {
            using var stream = file.OpenStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    [TestMethod]
    public async Task TransactionalAdd_AliasVariantWhoseProcessorHoldsSourceStream_Succeeds()
    {
        // The alias variant's processor leaves its context holding a stream on the main file's staged data, which shares only reading and deletion. Unless
        // the context releases it, the main file's flush (which needs write access on Windows) fails with a sharing violation.
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            var flushed = RecordFlushes();
            var pipeline = FileProcessingPipeline.Empty.WithVariant("same", new OpenSourceStreamProcessor().ToPipeline(aliasWhenVariantSourceUnchanged: true));

            await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
            await using var txn = await repo.BeginTransactionAsync();
            var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);

            Snapshot(flushed).ShouldContain(added.MainFile.Path.PathExport);
            (await repo.GetVariantAsync(added.FileId, "same")).Path.ShouldBe(added.MainFile.Path);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task VariantAdd_AliasVariantWhoseProcessorHoldsSourceStream_Succeeds()
    {
        // As above, for a nested alias variant holding a stream on its parent variant's staged data, which is flushed at its temp path before the move.
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            var pipeline = VariantPipelines.RealData().WithVariant("same", new OpenSourceStreamProcessor().ToPipeline(aliasWhenVariantSourceUnchanged: true));

            var added = await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

            added.Count.ShouldBe(2);
            (await repo.GetVariantAsync(fileId, "same")).Path.ShouldBe((await repo.GetVariantAsync(fileId, "a")).Path);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public void FlushFileAndDirectory_OnExistingEntries_SucceedAndAreObserved()
    {
        var dir = NewRepoDir();
        var file = dir.CombineFile("data.bin", PathOptions.None);

        try
        {
            var flushed = RecordFlushes();

            using (var stream = file.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write([1, 2, 3]);
                DurableIO.FlushFile(stream);
            }

            DurableIO.FlushFile(file);
            DurableIO.FlushDirectory(dir);
            DurableIO.FlushNewFile(file);

            Snapshot(flushed).ShouldBe([file.PathExport, file.PathExport, dir.PathExport, file.PathExport, dir.PathExport]);
        }
        finally
        {
            DurableIO.Observer = null;
        }
    }

    [TestMethod]
    public void FlushDirectory_MissingDirectory_ThrowsOnUnix_IsNoOpOnWindows()
    {
        var missing = NewRepoDir().CombineDirectory("missing", PathOptions.None);

        if (OperatingSystem.IsWindows())
        {
            DurableIO.FlushDirectory(missing);
        }
        else
        {
            var ex = Should.Throw<IOException>(() => DurableIO.FlushDirectory(missing));
            ex.Message.ShouldContain(missing.PathDisplay);
            ex.HResult.ShouldBe(2); // ENOENT
        }
    }

    /// <summary>
    /// A processor that opens its source as a file stream and passes it through unchanged, leaving the context holding the stream.
    /// </summary>
    private sealed class OpenSourceStreamProcessor : FileProcessor
    {
        public override IReadOnlyList<string> AllowedFileExtensions => [];

        protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
        {
            var stream = await context.GetSourceAsSeekableStreamAsync(preferInMemory: false, maxInMemoryCopySize: 0);
            return FileProcessingResult.Stream(stream, context.Extension, hasChanges: false);
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

    private async Task<FileId> AddSampleFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    private static FileRepo CreateRepo(IAbsoluteDirectoryPath repoDir, DeleteMode deleteMode)
    {
        var repo = new FileRepo(repoDir, RepoFormatVersion.Latest, o =>
        {
            o.DeleteMode = deleteMode;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });

        // Commit and rollback failures do not throw by default; surface them so that a failed flush during a commit fails the test.
        repo.TransactionCompletionFailed += failure => Task.FromException(new InvalidOperationException("Transaction completion failed.", failure.Exception));

        repo.EnsureCreated();
        return repo;
    }

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_Durability_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
