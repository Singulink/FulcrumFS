namespace FulcrumFS;

[PrefixTestClass]
public sealed class VariantRetirementTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task DeleteVariants_RealDataVariant_IsRetired()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var fileId = await AddBaseFileAsync(repo);

        await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData(), TestContext.CancellationToken);
        (await repo.GetVariantAsync(fileId, "a")).ShouldNotBeNull();

        await repo.DeleteVariantsAsync(fileId, "a");

        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));

        var group = await repo.GetGroupAsync(fileId);
        group.VariantFiles.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DeleteVariants_PreservesUnlistedAliasDependent_ByPromotion()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var fileId = await AddBaseFileAsync(repo);

        // a = real data; b = alias -> a.
        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        byte[] originalData = await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"));

        // Retire only the source 'a'. The unlisted dependent 'b' must be preserved by promoting it to a standalone data file.
        await repo.DeleteVariantsAsync(fileId, "a");

        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));

        var b = await repo.GetVariantAsync(fileId, "b");
        b.VariantId.ShouldBe("b");
        b.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);

        byte[] promotedData = await ReadAllAsync(b);
        promotedData.ShouldBe(originalData);

        // No rebase marker should remain after a completed promotion.
        var groupDir = b.Path.ParentDirectory;
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DeleteVariants_MultipleSurvivors_AllRemainResolvable()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var fileId = await AddBaseFileAsync(repo);

        // a = real data; b and c are both aliases -> a.
        var pipeline = VariantPipelines.RealData()
            .WithVariant("b", VariantPipelines.Alias())
            .WithVariant("c", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        byte[] originalData = await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"));

        await repo.DeleteVariantsAsync(fileId, "a");

        var b = await repo.GetVariantAsync(fileId, "b");
        var c = await repo.GetVariantAsync(fileId, "c");

        // Both survivors resolve to the same promoted data file with the original content.
        b.Path.PathExport.ShouldBe(c.Path.PathExport);
        (await ReadAllAsync(b)).ShouldBe(originalData);
        (await ReadAllAsync(c)).ShouldBe(originalData);

        var groupDir = b.Path.ParentDirectory;
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DeleteVariants_ListedSourceAndDependentTogether_RemovesBoth()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        await repo.DeleteVariantsAsync(fileId, "a", "b");

        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "b"));

        (await repo.GetGroupAsync(fileId)).VariantFiles.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DeleteVariants_IsIdempotent()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var fileId = await AddBaseFileAsync(repo);

        await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData(), TestContext.CancellationToken);

        // Empty list is a no-op.
        await repo.DeleteVariantsAsync(fileId);
        (await repo.GetVariantAsync(fileId, "a")).ShouldNotBeNull();

        // Never-existed variant id is silently skipped.
        await repo.DeleteVariantsAsync(fileId, "does-not-exist");
        (await repo.GetVariantAsync(fileId, "a")).ShouldNotBeNull();

        // First real delete retires it; a second delete of the same id is a no-op.
        await repo.DeleteVariantsAsync(fileId, "a");
        await repo.DeleteVariantsAsync(fileId, "a");

        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
    }

    [TestMethod]
    public async Task DeleteVariants_DeferredMode_FetchFailsImmediately_DataLingersUntilClean()
    {
        using var repo = CreateRepo(DeleteMode.DeferredUntilClean);
        var fileId = await AddBaseFileAsync(repo);

        var added = await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData(), TestContext.CancellationToken);
        var dataFile = added[0].Path;
        dataFile.State.ShouldBe(EntryState.Exists);

        await repo.DeleteVariantsAsync(fileId, "a");

        // Retirement is visible to fetches immediately.
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));

        // But the underlying data file lingers in deferred mode until a clean runs.
        dataFile.State.ShouldBe(EntryState.Exists);

        await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        dataFile.State.ShouldNotBe(EntryState.Exists);
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
    }

    [TestMethod]
    public async Task DeleteVariants_DeferredMode_PreservedDependentResolvesThroughClean()
    {
        using var repo = CreateRepo(DeleteMode.DeferredUntilClean);
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        byte[] originalData = await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"));

        await repo.DeleteVariantsAsync(fileId, "a");

        // 'b' must remain resolvable both before and after the residual cleanup of 'a'.
        (await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"))).ShouldBe(originalData);

        await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        var b = await repo.GetVariantAsync(fileId, "b");
        (await ReadAllAsync(b)).ShouldBe(originalData);
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
    }

    private static async Task<byte[]> ReadAllAsync(RepoFileInfo info)
    {
        await using var stream = info.Open();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private async Task<FileId> AddBaseFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    private static FileRepo CreateRepo(DeleteMode deleteMode)
    {
        var dir = _appDir.CombineDirectory("RepoRoot_VariantRetirement_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(recursive: true);

        dir.Create();

        var repo = new FileRepo(dir, options => {
            options.DeleteMode = deleteMode;
            options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });
        repo.EnsureCreated();
        return repo;
    }
}
