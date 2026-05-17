using Microsoft.Extensions.Options;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoCleanerTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void Construction_FromOptions_AndIOptions_Succeeds()
    {
        var repoDir = NewRepoDir();
        var options = new FileRepoCleaningOptions(repoDir);
        var cleaner1 = new FileRepoCleaner(options);
        cleaner1.Options.BaseDirectory.ShouldBe(repoDir);

        var ioptions = Options.Create(new FileRepoCleaningOptions(repoDir));
        var cleaner2 = new FileRepoCleaner(ioptions);
        cleaner2.Options.BaseDirectory.ShouldBe(repoDir);

        var cleaner3 = new FileRepoCleaner(repoDir, o => o.MarkerFileLogging = LoggingMode.None);
        cleaner3.Options.MarkerFileLogging.ShouldBe(LoggingMode.None);
    }

    [TestMethod]
    public async Task Instance_RemovesAgedDeleteMarker_WithoutLiveFileRepo()
    {
        // Set up a repo with default DeferredUntilClean mode and add+delete a file in a transaction.
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, o => o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60));
        repo.EnsureCreated();
        FileId fileId;

        try
        {
            fileId = await AddSampleFileAsync(repo);

            await using var txn = await repo.BeginTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);

            // Delete marker should exist now.
            var deleteMarker = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                .CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
            deleteMarker.State.ShouldBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }

        // Now clean using a standalone cleaner with no live FileRepo.
        await new FileRepoCleaner(repoDir).CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        var fileDir = repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);
        fileDir.State.ShouldNotBe(EntryState.Exists);
    }

    [TestMethod]
    public async Task ConcurrentCleans_SecondThrowsInvalidOperation()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, o => o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60));
        repo.EnsureCreated();

        try
        {
            repo.EnsureInitialized();

            // Manually open the clean lock to simulate an in-progress clean.
            var cleanLock = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                .CombineFile(FileRepoPaths.CleanupLockFileName, PathOptions.None);

            using var heldLock = cleanLock.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

            await Should.ThrowAsync<InvalidOperationException>(
                async () => await new FileRepoCleaner(repoDir).CleanAsync(TimeSpan.Zero, TestContext.CancellationToken));
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task IndeterminateResolveDelete_WithPositiveDelay_WritesDeleteMarkerAndKeepsFile()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, o => o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60));
        repo.EnsureCreated();

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            // Manufacture an indeterminate marker by writing one directly (simulating a crashed transaction).
            var indeterminateMarker = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                .CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
            using (indeterminateMarker.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            await repo.CleanAsync(TimeSpan.FromHours(1), id => IndeterminateResolution.Delete, TestContext.CancellationToken);

            // Indeterminate marker removed, delete marker written, data file still present.
            indeterminateMarker.State.ShouldNotBe(EntryState.Exists);

            var deleteMarker = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                .CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
            deleteMarker.State.ShouldBe(EntryState.Exists);

            var fileDir = repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);
            fileDir.State.ShouldBe(EntryState.Exists);

            // Second clean with zero delay should physically delete the file.
            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            fileDir.State.ShouldNotBe(EntryState.Exists);
            deleteMarker.State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task IndeterminateResolveDelete_WithZeroDelay_DeletesImmediately()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, o => o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60));
        repo.EnsureCreated();

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            var indeterminateMarker = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                .CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
            using (indeterminateMarker.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            await repo.CleanAsync(TimeSpan.Zero, id => IndeterminateResolution.Delete, TestContext.CancellationToken);

            var fileDir = repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);
            fileDir.State.ShouldNotBe(EntryState.Exists);
            indeterminateMarker.State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task DeleteMode_Immediate_TxnCommitDeleteWritesNoMarker()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, o => {
            o.DeleteMode = DeleteMode.Immediate;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });
        repo.EnsureCreated();

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            var deleteMarker = repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                .CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
            deleteMarker.State.ShouldNotBe(EntryState.Exists);

            var fileDir = repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);
            fileDir.State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_FileRepoCleaner_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
