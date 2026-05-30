namespace FulcrumFS;

[PrefixTestClass]
public sealed class VariantAliasTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AliasMarker_WrittenWhenVariantSourceUnchanged()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        var results = await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);

        results.Count.ShouldBe(1);
        results[0].VariantId.ShouldBe("thumb");

        var main = await repo.GetAsync(fileId);

        // The alias resolves transparently to the main file's data path.
        results[0].Path.PathExport.ShouldBe(main.Path.PathExport);

        // The on-disk marker uses the documented format: thumb.$main.jpg.alias
        var groupDir = main.Path.ParentDirectory;
        var markers = groupDir.GetChildFiles($"*{FileRepoPaths.AliasMarkerExtension}").ToList();
        markers.Count.ShouldBe(1);
        markers[0].Name.ShouldBe($"thumb.{FileRepoPaths.MainFileName}.jpg{FileRepoPaths.AliasMarkerExtension}");
    }

    [TestMethod]
    public async Task GetVariant_FollowsSingleHopAlias()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);

        var main = await repo.GetAsync(fileId);
        var variant = await repo.GetVariantAsync(fileId, "thumb");

        variant.VariantId.ShouldBe("thumb");
        variant.Path.PathExport.ShouldBe(main.Path.PathExport);
    }

    [TestMethod]
    public async Task GetOrAddVariant_SecondCallHitsFastPath_WithoutReprocessing()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        var processor = new NoChangeCountingProcessor();
        var pipeline = processor.ToPipeline(aliasWhenVariantSourceUnchanged: true);

        var first = await repo.GetOrAddVariantAsync(fileId, "thumb", pipeline, TestContext.CancellationToken);
        first[0].VariantId.ShouldBe("thumb");
        processor.InvocationCount.ShouldBe(1);

        // Second call must short-circuit on the fast path and not invoke the pipeline again.
        var second = await repo.GetOrAddVariantAsync(fileId, "thumb", pipeline, TestContext.CancellationToken);
        second[0].VariantId.ShouldBe("thumb");
        processor.InvocationCount.ShouldBe(1);

        var main = await repo.GetAsync(fileId);
        second[0].Path.PathExport.ShouldBe(main.Path.PathExport);
    }

    [TestMethod]
    public async Task AliasChain_CompressesToRootDataFile()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        // a = real data; b = alias (child of a); c = alias (child of b). Chain compression must make both b and c point at a's data file.
        var bPipeline = VariantPipelines.Alias().WithVariant("c", VariantPipelines.Alias());
        var aPipeline = VariantPipelines.RealData().WithVariant("b", bPipeline);

        var results = await repo.AddVariantAsync(fileId, "a", aPipeline, TestContext.CancellationToken);
        results.Select(r => r.VariantId).ShouldBe(["a", "b", "c"]);

        var a = await repo.GetVariantAsync(fileId, "a");
        var b = await repo.GetVariantAsync(fileId, "b");
        var c = await repo.GetVariantAsync(fileId, "c");

        b.Path.PathExport.ShouldBe(a.Path.PathExport);
        c.Path.PathExport.ShouldBe(a.Path.PathExport);

        // Verify the markers encode 'a' directly (compression), not a chain through 'b'.
        var groupDir = a.Path.ParentDirectory;
        string aExt = a.Path.Extension.TrimStart('.');

        var bMarker = groupDir.GetChildFiles($"b.*{FileRepoPaths.AliasMarkerExtension}").Single();
        bMarker.Name.ShouldBe($"b.a.{aExt}{FileRepoPaths.AliasMarkerExtension}");

        var cMarker = groupDir.GetChildFiles($"c.*{FileRepoPaths.AliasMarkerExtension}").Single();
        cMarker.Name.ShouldBe($"c.a.{aExt}{FileRepoPaths.AliasMarkerExtension}");
    }

    [TestMethod]
    public async Task GetGroup_ListsAliasResolvedVariantsAlongsideRealData()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        var aPipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", aPipeline, TestContext.CancellationToken);

        var group = await repo.GetGroupAsync(fileId);

        group.MainFile.VariantId.ShouldBeNull();
        group.VariantFiles.Select(v => v.VariantId).OrderBy(v => v, StringComparer.Ordinal).ShouldBe(["a", "b"]);

        var a = group.VariantFiles.Single(v => v.VariantId == "a");
        var b = group.VariantFiles.Single(v => v.VariantId == "b");

        // The alias 'b' resolves to 'a's real data file path.
        b.Path.PathExport.ShouldBe(a.Path.PathExport);
    }

    [TestMethod]
    public async Task AddVariant_ExistingAliasIsTreatedAsCollision()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TryAddVariant_ExistingAliasReturnsNull()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);

        var result = await repo.TryAddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);
        result.ShouldBeNull();
    }

    [TestMethod]
    public async Task AddVariant_RealDataFromExistingSource_Succeeds()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        // 'a' = real data. Adding 'b' (real data) sourced from the pre-existing 'a' must succeed: the existing source is not a collision.
        await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData([7, 8, 9]), TestContext.CancellationToken);

        var added = await repo.AddVariantAsync(fileId, "b", sourceVariantId: "a", VariantPipelines.RealData([4, 5, 6]), TestContext.CancellationToken);

        added.Count.ShouldBe(1);
        added[0].VariantId.ShouldBe("b");

        var b = await repo.GetVariantAsync(fileId, "b");
        b.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
        (await ReadAllAsync(b)).ShouldBe([4, 5, 6]);
    }

    [TestMethod]
    public async Task AddVariant_AliasFromExistingSource_WritesAlias()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        // 'a' = real data. Adding 'b' as a non-mutating (alias) variant sourced from the pre-existing 'a' must write an alias resolving to 'a's data.
        await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData([7, 8, 9]), TestContext.CancellationToken);

        var added = await repo.AddVariantAsync(fileId, "b", sourceVariantId: "a", VariantPipelines.Alias(), TestContext.CancellationToken);

        added.Count.ShouldBe(1);
        added[0].VariantId.ShouldBe("b");

        var a = await repo.GetVariantAsync(fileId, "a");
        var b = await repo.GetVariantAsync(fileId, "b");

        // The alias 'b' resolves transparently to 'a's real data file.
        b.Path.PathExport.ShouldBe(a.Path.PathExport);
        (await ReadAllAsync(b)).ShouldBe([7, 8, 9]);

        // An on-disk alias marker exists for 'b' naming 'a' as its source: b.a.<ext>.alias
        var groupDir = a.Path.ParentDirectory;
        var markers = groupDir.GetChildFiles($"b.a*{FileRepoPaths.AliasMarkerExtension}").ToList();
        markers.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task TryAddVariant_FromExistingSource_Succeeds()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData([7, 8, 9]), TestContext.CancellationToken);

        var result = await repo.TryAddVariantAsync(fileId, "b", sourceVariantId: "a", VariantPipelines.Alias(), TestContext.CancellationToken);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result[0].VariantId.ShouldBe("b");
    }

    [TestMethod]
    public async Task AddVariant_NonExistentSource_Throws()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        await Should.ThrowAsync<RepoFileNotFoundException>(
            async () => await repo.AddVariantAsync(fileId, "b", sourceVariantId: "missing", VariantPipelines.Alias(), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddVariant_CreatedVariantCollision_WithExistingSource_Throws()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        // 'a' = source, 'b' already created. Re-adding 'b' from source 'a' must still throw: the created variant 'b' collides even though the source is fine.
        await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData([7, 8, 9]), TestContext.CancellationToken);
        await repo.AddVariantAsync(fileId, "b", sourceVariantId: "a", VariantPipelines.Alias(), TestContext.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await repo.AddVariantAsync(fileId, "b", sourceVariantId: "a", VariantPipelines.Alias(), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task DanglingAlias_SourceRemovedOutOfBand_GetVariantThrows_GroupReportsBroken()
    {
        using var repo = CreateRepo();
        var fileId = await AddBaseFileAsync(repo);

        // 'a' = real data, 'b' = alias pointing at 'a'.
        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        // Capture 'a's extension before deletion so the broken-alias assertion can verify the parsed marker source-extension field.
        var aBeforeDelete = await repo.GetVariantAsync(fileId, "a");
        string aExt = aBeforeDelete.Path.Extension;

        // Remove 'a's data file out-of-band (no delete marker / retirement marker written), leaving 'b's alias marker orphaned on disk.
        aBeforeDelete.Path.Delete();

        // GetVariantAsync on the dangling alias must fail-fast with DanglingAliasException carrying the parsed source fields.
        var ex = await Should.ThrowAsync<DanglingAliasException>(async () => await repo.GetVariantAsync(fileId, "b"));
        ex.FileId.ShouldBe(fileId);
        ex.VariantId.ShouldBe("b");
        ex.SourceVariantId.ShouldBe("a");
        ex.SourceExtension.ShouldBe(aExt);

        // GetGroupAsync omits the dangling alias from VariantFiles and surfaces it through DanglingAliases instead. 'a's data file is also gone so it does not
        // appear in VariantFiles either.
        var group = await repo.GetGroupAsync(fileId);
        group.VariantFiles.Select(v => v.VariantId).ShouldNotContain("b");
        group.VariantFiles.Select(v => v.VariantId).ShouldNotContain("a");

        group.DanglingAliases.Count.ShouldBe(1);
        var broken = group.DanglingAliases[0];
        broken.VariantId.ShouldBe("b");
        broken.SourceVariantId.ShouldBe("a");
        broken.SourceExtension.ShouldBe(aExt);
    }

    [TestMethod]
    public async Task ConcurrentDeleteSourceAndAddAlias_SerializedBySourceLock()
    {
        // A single race samples only one non-deterministic interleaving (and the two tasks might not even overlap). To meaningfully exercise the source-lock
        // serialization we run the race many times with a start barrier that maximizes real overlap, using fresh state each iteration. Every interleaving must
        // converge on one of exactly two consistent outcomes; any other result (e.g. a dangling 'c' alias pointing at the deleted 'a') fails the invariant.
        const int iterations = 200;

        // Track that both outcomes are actually exercised, otherwise the test would silently pass even if one branch were unreachable.
        int deleteWonCount = 0;
        int addWonCount = 0;

        using var repo = CreateRepo();

        for (int i = 0; i < iterations; i++)
        {
            // Each iteration gets a fresh file so 'a'/'c' from prior rounds can't interfere.
            var fileId = await AddBaseFileAsync(repo);

            // 'a' = real data with known content; we race a delete of 'a' against adding a new alias 'c' that sources from 'a'.
            await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData([7, 8, 9]), TestContext.CancellationToken);
            byte[] originalData = await ReadAllAsync(await repo.GetVariantAsync(fileId, "a"));

            // The barrier releases both tasks as close together as possible to maximize the chance they actually contend on 'a's source lock.
            using var gate = new Barrier(2);

            var delete = Task.Run(async () =>
            {
                gate.SignalAndWait();

                try { await repo.DeleteVariantsAsync(fileId, "a"); }
                catch (RepoFileNotFoundException) { }
            });

            var add = Task.Run(async () =>
            {
                gate.SignalAndWait();

                try { await repo.GetOrAddVariantAsync(fileId, "c", sourceVariantId: "a", VariantPipelines.Alias(), TestContext.CancellationToken); }
                catch (RepoFileNotFoundException) { }
            });

            await Task.WhenAll(delete, add);

            // Regardless of ordering, 'a' is gone afterwards.
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));

            // 'c' is either absent (delete won the race) or, if the add committed first, it was promoted to real data by the delete's rebase - never a dangling
            // alias pointing at the deleted 'a'.
            try
            {
                var c = await repo.GetVariantAsync(fileId, "c");
                c.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
                (await ReadAllAsync(c)).ShouldBe(originalData);
                addWonCount++;
            }
            catch (RepoFileNotFoundException)
            {
                // Delete won: 'c' never materialized. Consistent.
                deleteWonCount++;
            }
        }

        // Both interleavings must be observed across the run; if either count is zero the race never actually contended and the test proves nothing.
        deleteWonCount.ShouldBeGreaterThan(0, "the delete-wins branch was never exercised");
        addWonCount.ShouldBeGreaterThan(0, "the add-wins (rebase-promotion) branch was never exercised");
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
        var dir = _appDir.CombineDirectory("RepoRoot_VariantAlias_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

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
