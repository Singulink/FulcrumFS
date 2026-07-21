namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoTransactionTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot_FileRepoTransaction");
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static readonly FileRepo _repo = new(_repoDir, options => {
        options.DeleteMode = DeleteMode.Immediate;
        options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
    });

    private static bool _initialized;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ConcurrentDeletes_SameFileId_BothSucceed_FirstCommits()
    {
        ResetRepository();

        var fileId = await AddSampleFileAsync();

        await using var txnA = await _repo.BeginTransactionAsync();
        await using var txnB = await _repo.BeginTransactionAsync();

        await txnA.DeleteAsync(fileId, TestContext.CancellationToken);
        await txnB.DeleteAsync(fileId, TestContext.CancellationToken);

        await txnA.CommitAsync(TestContext.CancellationToken);
        await txnB.CommitAsync(TestContext.CancellationToken);

        // After both commits and Clean, the file should be gone.
        await _repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await _repo.GetAsync(fileId));
    }

    [TestMethod]
    public async Task ConcurrentDeletes_SameFileId_CommitAndRollback()
    {
        ResetRepository();

        var fileId = await AddSampleFileAsync();

        await using var txnA = await _repo.BeginTransactionAsync();
        await using var txnB = await _repo.BeginTransactionAsync();

        await txnA.DeleteAsync(fileId, TestContext.CancellationToken);
        await txnB.DeleteAsync(fileId, TestContext.CancellationToken);

        // First commits the delete; second rolls back. The committed delete wins - file is deleted.
        await txnA.CommitAsync(TestContext.CancellationToken);
        await txnB.RollbackAsync(TestContext.CancellationToken);

        await _repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await _repo.GetAsync(fileId));
    }

    [TestMethod]
    public async Task ConcurrentDeletes_SameFileId_BothRollback_FileSurvives()
    {
        ResetRepository();

        var fileId = await AddSampleFileAsync();

        await using (var txnA = await _repo.BeginTransactionAsync())
        await using (var txnB = await _repo.BeginTransactionAsync())
        {
            await txnA.DeleteAsync(fileId, TestContext.CancellationToken);
            await txnB.DeleteAsync(fileId, TestContext.CancellationToken);

            await txnA.RollbackAsync(TestContext.CancellationToken);
            await txnB.RollbackAsync(TestContext.CancellationToken);
        }

        // File should still be accessible after both rollbacks.
        (await _repo.GetAsync(fileId)).ShouldNotBeNull();
    }

    private async Task<FileId> AddSampleFileAsync()
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await _repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    private static void ResetRepository()
    {
        lock (_repo)
        {
            if (_initialized)
                return;

            _initialized = true;

            if (_repoDir.Exists)
                _repoDir.Delete(true);

            _repoDir.Create();
            _repo.EnsureCreated();
        }
    }
}
