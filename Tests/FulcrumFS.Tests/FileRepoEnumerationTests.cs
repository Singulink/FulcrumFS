namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoEnumerationTests
{
    // File-system timestamps come from a coarser clock than DateTime.UtcNow (a timer tick on Windows, a jiffy on Linux), so a directory created just after
    // the test starts can carry a creation time slightly before the start time.
    private static readonly TimeSpan TimestampSlack = TimeSpan.FromSeconds(1);

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task EmptyRepository_EnumeratesNothing()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);

        try
        {
            (await CollectAsync(repo)).ShouldBeEmpty();
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Enumerate_ReportsEveryFileGroupWithMarkerFlags()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);

        try
        {
            var start = DateTime.UtcNow;

            var liveId = await AddSampleFileAsync(repo);
            var deletedId = await AddSampleFileAsync(repo);
            var indeterminateId = await AddSampleFileAsync(repo);

            // Deferred delete: the group stays on disk with a main-file delete marker.
            await using (var txn = await repo.BeginTransactionAsync())
            {
                await txn.DeleteAsync(deletedId, TestContext.CancellationToken);
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            // Manufacture an indeterminate marker (simulating a crashed transaction).
            using (CleanupFile(repoDir, indeterminateId + FileRepoPaths.IndeterminateMarkerExtension).OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            // A variant delete hint must not mark the whole file as pending deletion.
            string variantHintName = RepoFileSystem.GetFileIdAndVariantString(liveId, "thumbnail") + FileRepoPaths.DeleteMarkerExtension;
            using (CleanupFile(repoDir, variantHintName).OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            var entries = await CollectAsync(repo);
            var end = DateTime.UtcNow;

            entries.Count.ShouldBe(3);
            entries.Select(e => e.FileId).ShouldBe([liveId, deletedId, indeterminateId], ignoreOrder: true);

            var live = entries.Single(e => e.FileId == liveId);
            live.IsPendingDeletion.ShouldBeFalse();
            live.IsIndeterminate.ShouldBeFalse();

            var deleted = entries.Single(e => e.FileId == deletedId);
            deleted.IsPendingDeletion.ShouldBeTrue();
            deleted.IsIndeterminate.ShouldBeFalse();

            var indeterminate = entries.Single(e => e.FileId == indeterminateId);
            indeterminate.IsPendingDeletion.ShouldBeFalse();
            indeterminate.IsIndeterminate.ShouldBeTrue();

            foreach (var entry in entries)
            {
                entry.CreationTimeUtc.Kind.ShouldBe(DateTimeKind.Utc);
                entry.CreationTimeUtc.ShouldBeInRange(start - TimestampSlack, end + TimestampSlack);
            }
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Enumerate_SkipsEntriesOutsideTheLayout()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            var filesDir = repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None);
            var groupDir = filesDir.Combine(fileId.RelativeDirectory);
            var shardDir = groupDir.ParentDirectory!;

            // A foreign first-level directory holding what would otherwise be a valid group directory for another ID.
            var foreignId = FileId.CreateSecure();
            filesDir.CombineDirectory("zz", PathOptions.None).Combine(foreignId.RelativeDirectory).Create();

            // A non-GUID directory and a stray file inside a real shard, and a stray file at the top of the files directory.
            shardDir.CombineDirectory("not-a-file-id", PathOptions.None).Create();
            using (shardDir.CombineFile(FileId.CreateSecure().ToString(), PathOptions.None).OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (filesDir.CombineFile("readme.txt", PathOptions.None).OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            // A valid file ID in a shard it does not map to: the repository would never look for it there.
            FileId misplacedId;

            do
            {
                misplacedId = FileId.CreateSecure();
            }
            while (misplacedId.RelativeDirectory.ParentDirectory!.Equals(fileId.RelativeDirectory.ParentDirectory));

            shardDir.CombineDirectory(misplacedId.ToString(), PathOptions.None).Create();

            // An uppercase file ID in the shard it maps to: group directory names are always lowercase.
            var uppercaseId = FileId.CreateSecure();
            var uppercaseShardDir = filesDir.Combine(uppercaseId.RelativeDirectory).ParentDirectory!;
            uppercaseShardDir.CombineDirectory(uppercaseId.ToString().ToUpperInvariant(), PathOptions.None).Create();

            var entries = await CollectAsync(repo);

            entries.Select(e => e.FileId).ShouldBe([fileId]);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Cancellation_BeforeFirstMoveNext_Throws()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);

        try
        {
            await AddSampleFileAsync(repo);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await using var enumerator = repo.EnumerateFilesAsync(cts.Token).GetAsyncEnumerator(TestContext.CancellationToken);
            await Should.ThrowAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Cancellation_DuringWalk_ThrowsAtNextShard()
    {
        var repoDir = NewRepoDir();

        // Sequential IDs shard on the low 16 bits of their millisecond timestamp, and each one is at least a millisecond after the last, so each file is in
        // its own shard and the walk passes a shard boundary after the first entry.
        var repo = CreateRepo(repoDir, FileIdMode.Sequential);

        try
        {
            await AddSampleFileAsync(repo);
            await AddSampleFileAsync(repo);

            using var cts = new CancellationTokenSource();
            await using var enumerator = repo.EnumerateFilesAsync().WithCancellation(cts.Token).GetAsyncEnumerator();

            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            await cts.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            repo.Dispose();
        }
    }

    private async Task<List<RepoFileEntry>> CollectAsync(FileRepo repo)
    {
        var entries = new List<RepoFileEntry>();

        await foreach (var entry in repo.EnumerateFilesAsync(TestContext.CancellationToken))
            entries.Add(entry);

        return entries;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    private static FileRepo CreateRepo(IAbsoluteDirectoryPath repoDir, FileIdMode fileIdMode = FileIdMode.Secure)
    {
        var repo = new FileRepo(repoDir, RepoFormatVersion.Latest, o => {
            o.DeleteMode = DeleteMode.DeferredUntilClean;
            o.FileIdMode = fileIdMode;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });

        repo.EnsureCreated();
        return repo;
    }

    private static IAbsoluteFilePath CleanupFile(IAbsoluteDirectoryPath repoDir, string name) =>
        repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None).CombineFile(name, PathOptions.None);

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory(
            "RepoRoot_FileRepoEnumeration_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
