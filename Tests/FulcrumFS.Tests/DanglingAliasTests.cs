namespace FulcrumFS;

[PrefixTestClass]
public sealed class DanglingAliasTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task GetVariant_DanglingAlias_RaisesCorruptionDetectedEvent()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        a.Path.Delete();

        var captured = new List<RepoCorruptionInfo>();
        repo.CorruptionDetected += info =>
        {
            lock (captured)
                captured.Add(info);

            return Task.CompletedTask;
        };

        await Should.ThrowAsync<DanglingAliasException>(async () => await repo.GetVariantAsync(fileId, "b"));

        captured.Count.ShouldBe(1);
        captured[0].Kind.ShouldBe(RepoCorruptionKind.DanglingAlias);
        captured[0].FileId.ShouldBe(fileId);
        captured[0].VariantId.ShouldBe("b");
        captured[0].Message.ShouldNotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task GetGroup_DanglingAlias_RaisesEventOncePerDanglingAlias()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        // 'a' = real data with two alias dependents 'b' and 'c'. Deleting 'a' leaves two dangling aliases.
        var pipeline = VariantPipelines.RealData()
            .WithVariant("b", VariantPipelines.Alias())
            .WithVariant("c", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        a.Path.Delete();

        var captured = new List<RepoCorruptionInfo>();
        repo.CorruptionDetected += info =>
        {
            lock (captured)
                captured.Add(info);

            return Task.CompletedTask;
        };

        var group = await repo.GetGroupAsync(fileId);

        group.DanglingAliases.Select(b => b.VariantId).OrderBy(v => v, StringComparer.Ordinal).ShouldBe(["b", "c"]);
        captured.Count.ShouldBe(2);
        captured.Select(c => c.VariantId).OrderBy(v => v, StringComparer.Ordinal).ShouldBe(["b", "c"]);
        captured.ShouldAllBe(c => c.Kind == RepoCorruptionKind.DanglingAlias);
        captured.ShouldAllBe(c => c.FileId == fileId);
    }

    [TestMethod]
    public async Task AddVariant_OverDanglingAlias_SelfHeals()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        // Create 'a' real + 'b' alias, then break 'b' by deleting 'a'.
        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        var groupDir = a.Path.ParentDirectory;
        a.Path.Delete();

        // Sanity: 'b's alias marker is still on disk and currently broken.
        groupDir.GetChildFiles($"b.*{FileRepoPaths.AliasMarkerExtension}").Count().ShouldBe(1);

        var captured = new List<RepoCorruptionInfo>();
        repo.CorruptionDetected += info =>
        {
            lock (captured)
                captured.Add(info);

            return Task.CompletedTask;
        };

        // Adding 'b' over the dangling alias must succeed: the marker is treated as an empty slot and deleted under-lock; the new real data takes its place.
        var added = await repo.AddVariantAsync(fileId, "b", VariantPipelines.RealData([4, 5, 6]), TestContext.CancellationToken);
        added.Count.ShouldBe(1);
        added[0].VariantId.ShouldBe("b");

        var b = await repo.GetVariantAsync(fileId, "b");
        b.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
        (await ReadAllAsync(b)).ShouldBe([4, 5, 6]);

        // The stale alias marker is gone.
        groupDir.GetChildFiles($"b.*{FileRepoPaths.AliasMarkerExtension}").ShouldBeEmpty();

        // The self-heal still fires the corruption event so monitoring tooling sees that the underlying repository was in a broken state.
        captured.Count.ShouldBe(1);
        captured[0].Kind.ShouldBe(RepoCorruptionKind.DanglingAlias);
        captured[0].VariantId.ShouldBe("b");
    }

    [TestMethod]
    public async Task TryAddVariant_OverDanglingAlias_SelfHeals()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        a.Path.Delete();

        // TryAdd should not see the dangling alias as a collision: it must succeed (non-null) and produce real data, exactly mirroring AddVariant's self-heal.
        var result = await repo.TryAddVariantAsync(fileId, "b", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);

        var b = await repo.GetVariantAsync(fileId, "b");
        (await ReadAllAsync(b)).ShouldBe([1, 2, 3]);
    }

    [TestMethod]
    public async Task GetOrAddVariant_OverDanglingAlias_SelfHeals()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        a.Path.Delete();

        // GetOrAdd must not return the dangling alias as an existing variant; it must heal and produce real data.
        var result = await repo.GetOrAddVariantAsync(fileId, "b", VariantPipelines.RealData([9, 9, 9]), TestContext.CancellationToken);

        result.Count.ShouldBe(1);

        var b = await repo.GetVariantAsync(fileId, "b");
        b.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
        (await ReadAllAsync(b)).ShouldBe([9, 9, 9]);
    }

    [TestMethod]
    public async Task GetGroup_HealthyRepo_DanglingAliasesIsEmpty()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var group = await repo.GetGroupAsync(fileId);

        group.DanglingAliases.ShouldBeEmpty();
        group.VariantFiles.Select(v => v.VariantId).OrderBy(v => v, StringComparer.Ordinal).ShouldBe(["a", "b"]);
    }

    [TestMethod]
    public async Task CorruptionDetected_HealthyRepoOperations_NeverFires()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        int fired = 0;
        repo.CorruptionDetected += _ =>
        {
            Interlocked.Increment(ref fired);
            return Task.CompletedTask;
        };

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        await repo.GetAsync(fileId);
        await repo.GetVariantAsync(fileId, "a");
        await repo.GetVariantAsync(fileId, "b");
        await repo.GetGroupAsync(fileId);

        fired.ShouldBe(0);
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

    private static FileRepo CreateRepo()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_DanglingAlias_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(recursive: true);

        dir.Create();

        var repo = new FileRepo(dir, options => {
            options.DeleteMode = DeleteMode.Immediate;
            options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });
        repo.EnsureCreated();
        return repo;
    }
}
