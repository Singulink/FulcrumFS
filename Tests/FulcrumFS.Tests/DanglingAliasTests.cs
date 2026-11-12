using System.Globalization;
using System.Runtime.CompilerServices;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class DanglingAliasTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // A real 'a' with the alias 'b', and the data file of 'a' removed by hand. It is what a fetch of 'b' has in its hands when a retirement of 'a' finished
    // between its read of b's marker and its look at a's data file, except that b's marker is still there: the tests replace it, as the retirement does
    // before it removes the data file, once the fetch has found the source missing and before it reads again.
    private sealed record MissingSourceAlias(FileId FileId, IAbsoluteDirectoryPath GroupDir, string Extension, byte[] SourceData)
    {
        public IAbsoluteFilePath Marker => GroupDir.GetChildFiles($"b.*{FileRepoPaths.AliasMarkerExtension}").Single();

        public IAbsoluteFilePath PromotedFile => GroupDir.CombineFile("b" + Extension, PathOptions.None);

        // What promoting a survivor does: the source's content is written into the marker, which is then renamed to the variant's data file.
        public void Promote()
        {
            var marker = Marker;
            File.WriteAllBytes(marker.PathExport, SourceData);
            marker.MoveTo(PromotedFile, overwrite: false);
        }

        // What re-pointing a survivor does: its marker is renamed to one that names another source.
        public void Repoint(string sourceVariantId)
        {
            string name = $"b.{sourceVariantId}{Extension}{FileRepoPaths.AliasMarkerExtension}";
            Marker.MoveTo(GroupDir.CombineFile(name, PathOptions.None), overwrite: false);
        }
    }

    // What one fetch of a variant answered, through GetVariantAsync or through the listing of GetGroupAsync (which has no exception for a single variant).
    private sealed record FetchResult(RepoFileInfo? File, RepoFileNotFoundException? Error, bool IsDangling);

    // What one add of a variant answered: the variants it returned (null from TryAddVariantAsync for a variant that exists), or the exception it threw.
    private sealed record AddResult(IReadOnlyList<RepoFileInfo>? Added, InvalidOperationException? Error);

#pragma warning disable RCS1194 // Implement exception constructors

    private sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException() : base("Simulated crash injected by test.") { }
    }

#pragma warning restore RCS1194

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
    [DataRow("Add", false)]
    [DataRow("TryAdd", false)]
    [DataRow("GetOrAdd", false)]
    [DataRow("Add", true)]
    [DataRow("TryAdd", true)]
    [DataRow("GetOrAdd", true)]
    public async Task AddVariant_OverDanglingAlias_SelfHeals_AndReportsItOnce_WhetherOrNotItsSourceIsRetired(string addMethod, bool sourceRetired)
    {
        using var repo = CreateRepo();
        var alias = await AddAliasWithMissingSourceAsync(repo);

        if (sourceRetired)
        {
            // 'a' is retired and its data is gone while 'b', which is not retired, still aliases it. A retirement keeps the data of a retired source for
            // as long as such an alias names it, so this is a dangling alias like that of a source that is not retired, which is how the fetches report it.
            var deleteMarker = alias.GroupDir.CombineFile("a" + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
            await File.WriteAllBytesAsync(deleteMarker.PathExport, [], TestContext.CancellationToken);
        }

        await Should.ThrowAsync<DanglingAliasException>(async () => await repo.GetVariantAsync(alias.FileId, "b"));

        var corruption = TrackCorruption(repo);
        var rereads = OnSourceMissing(repo, viaGroup: false);

        // Every way of adding 'b' treats the slot as empty once it has read it again and found the same marker with its source still missing.
        var result = await AddAsync(repo, alias.FileId, "b", addMethod, VariantPipelines.RealData([4, 5, 6]));

        result.Error.ShouldBeNull();
        result.Added.ShouldNotBeNull().ShouldHaveSingleItem().VariantId.ShouldBe("b");

        var b = await repo.GetVariantAsync(alias.FileId, "b");
        b.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
        (await ReadAllAsync(b)).ShouldBe([4, 5, 6]);
        alias.GroupDir.GetChildFiles($"b.*{FileRepoPaths.AliasMarkerExtension}").ShouldBeEmpty();

        // The add under the locks reads the slot twice, and reports the dangling alias once. GetOrAdd has read it twice before that, in its lock-free
        // fast path, which reports nothing and leaves the alias to the locked path.
        rereads.Value.ShouldBe(addMethod is "GetOrAdd" ? 2 : 1);

        var reported = corruption.ShouldHaveSingleItem();
        reported.Kind.ShouldBe(RepoCorruptionKind.DanglingAlias);
        reported.VariantId.ShouldBe("b");
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Fetch_SourceMissing_ThenAliasPromoted_ReturnsThePromotedFile(bool viaGroup)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        var rereads = OnSourceMissing(repo, viaGroup, _ => alias.Promote());

        var fetch = await FetchAsync(repo, alias.FileId, "b", viaGroup);

        fetch.Error.ShouldBeNull();
        fetch.File.ShouldNotBeNull().Path.PathExport.ShouldBe(alias.PromotedFile.PathExport);
        (await ReadAllAsync(fetch.File)).ShouldBe(alias.SourceData);
        fetch.IsDangling.ShouldBeFalse();
        rereads.Value.ShouldBe(1);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Fetch_SourceMissing_ThenAliasRepointed_ReturnsTheNewSource(bool viaGroup)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        // Another variant with data of its own, which the alias is re-pointed at.
        await repo.AddVariantAsync(alias.FileId, "x", VariantPipelines.RealData([7, 8, 9]), TestContext.CancellationToken);
        var x = await repo.GetVariantAsync(alias.FileId, "x");

        var rereads = OnSourceMissing(repo, viaGroup, _ => alias.Repoint("x"));

        var fetch = await FetchAsync(repo, alias.FileId, "b", viaGroup);

        fetch.Error.ShouldBeNull();
        fetch.File.ShouldNotBeNull().Path.PathExport.ShouldBe(x.Path.PathExport);
        (await ReadAllAsync(fetch.File)).ShouldBe([7, 8, 9]);
        fetch.IsDangling.ShouldBeFalse();
        rereads.Value.ShouldBe(1);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Fetch_SourceMissing_ThenAliasRetiredOrRemoved_IsNotFound_AndNotReported(bool viaGroup, bool retired)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        // The alias was retired with its source: its delete marker is there while its alias marker still is, and afterwards neither is.
        var rereads = OnSourceMissing(repo, viaGroup, _ =>
        {
            if (retired)
                File.WriteAllBytes(alias.GroupDir.CombineFile("b" + FileRepoPaths.DeleteMarkerExtension, PathOptions.None).PathExport, []);
            else
                alias.Marker.Delete();
        });

        var fetch = await FetchAsync(repo, alias.FileId, "b", viaGroup);

        fetch.File.ShouldBeNull();
        fetch.IsDangling.ShouldBeFalse();

        // The plain not-found of a variant that is gone, not its subtype that reports a dangling alias.
        if (!viaGroup)
            fetch.Error.ShouldNotBeNull().ShouldBeOfType<RepoFileNotFoundException>();

        rereads.Value.ShouldBe(1);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Fetch_SourceMissing_ThenAliasRepointedManyTimesAndPromoted_FollowsEveryChange(bool viaGroup)
    {
        const int Changes = 25;

        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        // Every read but the last finds the alias re-pointed at yet another source that is missing by then; the last change promotes it. A fetch that gave
        // up after a few reads would report a dangling alias, or not find the variant, somewhere along the way.
        var rereads = OnSourceMissing(repo, viaGroup, change =>
        {
            if (change < Changes)
                alias.Repoint("s" + change.ToString(CultureInfo.InvariantCulture));
            else
                alias.Promote();
        });

        var fetch = await FetchAsync(repo, alias.FileId, "b", viaGroup);

        fetch.Error.ShouldBeNull();
        fetch.File.ShouldNotBeNull().Path.PathExport.ShouldBe(alias.PromotedFile.PathExport);
        (await ReadAllAsync(fetch.File)).ShouldBe(alias.SourceData);
        fetch.IsDangling.ShouldBeFalse();
        rereads.Value.ShouldBe(Changes);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Fetch_SourceMissing_AndNothingChanges_ReportsTheDanglingAliasOnce_AfterReadingTwice(bool viaGroup)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        // The source is not retired, and nothing replaces the alias's marker: the second read finds what the first found.
        var rereads = OnSourceMissing(repo, viaGroup);

        var fetch = await FetchAsync(repo, alias.FileId, "b", viaGroup);

        fetch.File.ShouldBeNull();
        fetch.IsDangling.ShouldBeTrue();

        if (!viaGroup)
        {
            var ex = fetch.Error.ShouldNotBeNull().ShouldBeOfType<DanglingAliasException>();
            ex.VariantId.ShouldBe("b");
            ex.SourceVariantId.ShouldBe("a");
        }

        // One repeat, so two reads of the variant's slot (or two listings of the group): no more than it takes to see that nothing changed.
        rereads.Value.ShouldBe(1);

        var reported = corruption.ShouldHaveSingleItem();
        reported.Kind.ShouldBe(RepoCorruptionKind.DanglingAlias);
        reported.FileId.ShouldBe(alias.FileId);
        reported.VariantId.ShouldBe("b");
    }

    [TestMethod]
    [DataRow("b")]
    [DataRow("c")]
    public async Task GetVariant_OfASurvivor_WhileItsSourceIsRetiredFromStartToFinish_ReturnsThePromotedData(string survivor)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData()
            .WithVariant("b", VariantPipelines.Alias())
            .WithVariant("c", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        byte[] originalData = await ReadAllAsync(a);

        // The whole retirement of 'a' runs between the fetch's read of the survivor's alias marker and its look at the data file of 'a', which a fetch on
        // another thread can do: repositories delete immediately here. One survivor is promoted to a data file and the other re-pointed at it, so the fetch
        // of either goes on with a marker that is gone and a source that is gone.
        int markerReads = 0;
        int rereads = 0;

        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.VariantAliasMarkerRead && ++markerReads is 1)
                repo.DeleteVariantsAsync(fileId, "a").AsTask().GetAwaiter().GetResult();
            else if (step is DebugStep.VariantAliasSourceMissing)
                rereads++;
        };

        var fetched = await repo.GetVariantAsync(fileId, survivor);
        repo.DebugStepHook = null;

        // The retirement finished inside the fetch, and the fetch found the source missing and read again, once.
        a.Path.State.ShouldNotBe(EntryState.Exists);
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
        rereads.ShouldBe(1);

        // It returned what the survivor resolves to now, which is the promoted data file for both of them.
        fetched.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
        fetched.Path.PathExport.ShouldBe((await repo.GetVariantAsync(fileId, survivor)).Path.PathExport);
        fetched.Path.PathExport.ShouldBe((await repo.GetVariantAsync(fileId, "b")).Path.PathExport);
        fetched.Path.PathExport.ShouldBe((await repo.GetVariantAsync(fileId, "c")).Path.PathExport);
        (await ReadAllAsync(fetched)).ShouldBe(originalData);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("Add", 1)]
    [DataRow("TryAdd", 1)]
    [DataRow("GetOrAdd", 1)]
    [DataRow("GetOrAdd", 2)]
    public async Task Add_SourceMissing_ThenAliasPromoted_FindsTheExistingVariant(string addMethod, int changeOnReread)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        var rereads = OnSourceMissing(repo, viaGroup: false, reread =>
        {
            if (reread == changeOnReread)
                alias.Promote();
        });

        var result = await AddAsync(repo, alias.FileId, "b", addMethod, VariantPipelines.RealData([4, 5, 6]));

        ShouldBeTheExistingVariant(result, addMethod, "b", alias.PromotedFile);
        rereads.Value.ShouldBe(changeOnReread);
        corruption.ShouldBeEmpty();

        // The promoted data file is all there is of 'b', with the bytes it was promoted with: nothing was removed, replaced or added.
        alias.GroupDir.GetChildFiles("b.*").ShouldHaveSingleItem().PathExport.ShouldBe(alias.PromotedFile.PathExport);
        (await ReadAllAsync(await repo.GetVariantAsync(alias.FileId, "b"))).ShouldBe(alias.SourceData);
    }

    [TestMethod]
    [DataRow("Add", 1)]
    [DataRow("TryAdd", 1)]
    [DataRow("GetOrAdd", 1)]
    [DataRow("GetOrAdd", 2)]
    public async Task Add_SourceMissing_ThenAliasRepointed_FindsTheExistingVariant(string addMethod, int changeOnReread)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        // Another variant with data of its own, which the alias is re-pointed at.
        await repo.AddVariantAsync(alias.FileId, "x", VariantPipelines.RealData([7, 8, 9]), TestContext.CancellationToken);
        var x = await repo.GetVariantAsync(alias.FileId, "x");

        var rereads = OnSourceMissing(repo, viaGroup: false, reread =>
        {
            if (reread == changeOnReread)
                alias.Repoint("x");
        });

        var result = await AddAsync(repo, alias.FileId, "b", addMethod, VariantPipelines.RealData([4, 5, 6]));

        ShouldBeTheExistingVariant(result, addMethod, "b", x.Path);
        rereads.Value.ShouldBe(changeOnReread);
        corruption.ShouldBeEmpty();

        // The re-pointed marker is all there is of 'b', and it resolves to the data of 'x'.
        alias.GroupDir.GetChildFiles("b.*").ShouldHaveSingleItem().Extension.ShouldBe(FileRepoPaths.AliasMarkerExtension);
        (await ReadAllAsync(await repo.GetVariantAsync(alias.FileId, "b"))).ShouldBe([7, 8, 9]);
    }

    [TestMethod]
    [DataRow("Add", 1)]
    [DataRow("TryAdd", 1)]
    [DataRow("GetOrAdd", 1)]
    [DataRow("GetOrAdd", 2)]
    public async Task Add_SourceMissing_ThenAliasRemoved_AddsTheVariant_AndReportsNothing(string addMethod, int changeOnReread)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);

        // The alias was retired with its source and its retirement has finished: neither its alias marker nor its delete marker is left.
        var rereads = OnSourceMissing(repo, viaGroup: false, reread =>
        {
            if (reread == changeOnReread)
                alias.Marker.Delete();
        });

        var result = await AddAsync(repo, alias.FileId, "b", addMethod, VariantPipelines.RealData([4, 5, 6]));

        // An empty slot, which every way of adding fills.
        result.Error.ShouldBeNull();
        result.Added.ShouldNotBeNull().ShouldHaveSingleItem().VariantId.ShouldBe("b");
        (await ReadAllAsync(await repo.GetVariantAsync(alias.FileId, "b"))).ShouldBe([4, 5, 6]);

        rereads.Value.ShouldBe(changeOnReread);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("Add", 1)]
    [DataRow("TryAdd", 1)]
    [DataRow("GetOrAdd", 1)]
    [DataRow("GetOrAdd", 2)]
    public async Task Add_SourceMissing_ThenAliasRetired_ThrowsPendingCleanup_AndReportsNothing(string addMethod, int changeOnReread)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);
        var marker = alias.Marker;

        // The alias was retired with its source: its delete marker is there while its alias marker still is.
        var rereads = OnSourceMissing(repo, viaGroup: false, reread =>
        {
            if (reread == changeOnReread)
                File.WriteAllBytes(alias.GroupDir.CombineFile("b" + FileRepoPaths.DeleteMarkerExtension, PathOptions.None).PathExport, []);
        });

        var result = await AddAsync(repo, alias.FileId, "b", addMethod, VariantPipelines.RealData([4, 5, 6]));

        // A retired variant cannot be added again until its retirement has been cleaned up, whichever way it is added.
        result.Added.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldBeOfType<RepoVariantPendingCleanupException>();

        rereads.Value.ShouldBe(changeOnReread);
        corruption.ShouldBeEmpty();
        marker.State.ShouldBe(EntryState.Exists);
    }

    [TestMethod]
    public async Task GetOrAddVariant_OnAReadOnlyRepository_SourceMissing_ThenAliasPromoted_ReturnsTheExistingVariant()
    {
        using var repo = CreateRepo();
        var alias = await AddAliasWithMissingSourceAsync(repo);

        // A reader of the same repository returns the variants that exist and throws once it would have to add one. Taking the alias for one that is not
        // there, on the strength of a marker that a retirement has replaced, would make it throw for a variant that exists.
        using var reader = new FileRepo(repo.BaseDirectory, RepoFormatVersion.Latest, o => o.ReadOnly = true);
        var corruption = TrackCorruption(reader);
        var rereads = OnSourceMissing(reader, viaGroup: false, _ => alias.Promote());

        var result = await reader.GetOrAddVariantAsync(alias.FileId, "b", VariantPipelines.RealData([4, 5, 6]), TestContext.CancellationToken);

        result.ShouldHaveSingleItem().Path.PathExport.ShouldBe(alias.PromotedFile.PathExport);
        (await ReadAllAsync(result[0])).ShouldBe(alias.SourceData);
        rereads.Value.ShouldBe(1);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("Add", "b")]
    [DataRow("TryAdd", "b")]
    [DataRow("GetOrAdd", "b")]
    [DataRow("Add", "c")]
    [DataRow("TryAdd", "c")]
    [DataRow("GetOrAdd", "c")]
    public async Task Add_OfASurvivor_WhileACleanupPassFinishesItsSourcesRetirement_FindsTheExistingVariant(string addMethod, string survivor)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var (fileId, a, originalData) = await AddRealWithTwoAliasesAndCrashItsRetirementAsync(repo);

        // The add reads the survivor's alias marker, which still names 'a', and the whole cleanup pass runs before it looks at the data file of 'a': the
        // pass takes no variant locks, so the add's locks do not hold it back. The pass promotes one survivor to a data file, re-points the other at it
        // and removes the data file of 'a', so the add of either goes on with a marker that is gone and a source that is gone.
        int markerReads = 0;
        int rereads = 0;

        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.VariantAliasMarkerRead && ++markerReads is 1)
                repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken).GetAwaiter().GetResult();
            else if (step is DebugStep.VariantAliasSourceMissing)
                rereads++;
        };

        var result = await AddAsync(repo, fileId, survivor, addMethod, VariantPipelines.RealData([4, 5, 6]));
        repo.DebugStepHook = null;

        // The pass finished inside the add, and the add found the source missing and read the slot again, once.
        a.Path.State.ShouldNotBe(EntryState.Exists);
        rereads.ShouldBe(1);

        // Both survivors resolve to the promoted data file, which the add found as the variant that exists and left alone.
        var promoted = await repo.GetVariantAsync(fileId, "b");
        promoted.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
        (await repo.GetVariantAsync(fileId, "c")).Path.PathExport.ShouldBe(promoted.Path.PathExport);

        ShouldBeTheExistingVariant(result, addMethod, survivor, promoted.Path);
        (await ReadAllAsync(promoted)).ShouldBe(originalData);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("b")]
    [DataRow("c")]
    public async Task AddVariant_FromASurvivor_WhileACleanupPassFinishesItsSourcesRetirement_UsesWhatTheSurvivorResolvesToNow(string survivor)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var (fileId, a, originalData) = await AddRealWithTwoAliasesAndCrashItsRetirementAsync(repo);

        // The add of 'd' has the survivor as its source. It reads the survivor's marker twice, to check its variants and then to resolve its source, and
        // the whole cleanup pass runs after the second read, before the add looks at the data file of 'a', the root that the marker names.
        int markerReads = 0;
        int rereads = 0;

        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.VariantAliasMarkerRead && ++markerReads is 2)
                repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken).GetAwaiter().GetResult();
            else if (step is DebugStep.VariantAliasSourceMissing)
                rereads++;
        };

        var added = await repo.AddVariantAsync(fileId, "d", sourceVariantId: survivor, VariantPipelines.Alias(), TestContext.CancellationToken);
        repo.DebugStepHook = null;

        a.Path.State.ShouldNotBe(EntryState.Exists);
        rereads.ShouldBe(1);

        // The source is live, as the promoted data file or as an alias of it, so the add did not fail: 'd' aliases the promoted data file.
        var promoted = await repo.GetVariantAsync(fileId, "b");
        promoted.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);

        added.ShouldHaveSingleItem().VariantId.ShouldBe("d");
        var d = await repo.GetVariantAsync(fileId, "d");
        d.Path.PathExport.ShouldBe(promoted.Path.PathExport);
        (await ReadAllAsync(d)).ShouldBe(originalData);
        corruption.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task AddVariant_FromADanglingAlias_IsNotFound_AndReportsItOnce_WithoutReadingItAThirdTime()
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var alias = await AddAliasWithMissingSourceAsync(repo);
        var marker = alias.Marker;

        var rereads = OnSourceMissing(repo, viaGroup: false);

        // The check of the add's variants, the source among them, reads the source's slot twice and reports the dangling alias. Resolving the source then
        // finds the same marker without its root and needs no further read to know that the source is not there.
        var ex = await Should.ThrowAsync<RepoFileNotFoundException>(
            async () => await repo.AddVariantAsync(alias.FileId, "d", sourceVariantId: "b", VariantPipelines.Alias(), TestContext.CancellationToken));

        ex.ShouldBeOfType<RepoFileNotFoundException>();
        rereads.Value.ShouldBe(1);

        var reported = corruption.ShouldHaveSingleItem();
        reported.Kind.ShouldBe(RepoCorruptionKind.DanglingAlias);
        reported.VariantId.ShouldBe("b");

        // The add failed, so it removed nothing: the dangling marker of its source is still there.
        marker.State.ShouldBe(EntryState.Exists);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GetOrAddVariant_NestedAliasWhoseSourceGoesMissingWhileItsParentIsProcessed_IsReadAgainBeforeItIsReported(bool promoted)
    {
        using var repo = CreateRepo();
        var corruption = TrackCorruption(repo);
        var fileId = await AddBaseFileAsync(repo);

        await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias()), TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        var alias = new MissingSourceAlias(fileId, a.Path.ParentDirectory, a.Extension, await ReadAllAsync(a));
        var staleMarker = alias.Marker;

        var rereads = OnSourceMissing(repo, viaGroup: false);

        // 'p' does not exist, so the add takes its locks and checks its variants, which finds 'b' as an alias that resolves, and then runs the pipeline
        // of 'p' before it gets to 'b'. While that pipeline runs, the data file of 'a' goes: after 'b' has been promoted to a data file, as a cleanup pass
        // that finishes a retirement of 'a' does it, or with nothing done about 'b', which leaves it dangling.
        var processor = new ActingRewriteProcessor(
            () =>
            {
                if (promoted)
                    alias.Promote();

                a.Path.Delete();
            },
            [4, 5, 6]);

        var pipeline = processor.ToPipeline().WithVariant("b", VariantPipelines.Alias());
        var result = await repo.GetOrAddVariantAsync(fileId, "p", pipeline, TestContext.CancellationToken);

        result.Select(v => v.VariantId).ShouldBe(["p", "b"]);
        (await ReadAllAsync(result[0])).ShouldBe([4, 5, 6]);

        // The add held the marker of 'b' from its check, found its source missing and read the slot again, once.
        rereads.Value.ShouldBe(1);
        staleMarker.State.ShouldNotBe(EntryState.Exists);

        if (promoted)
        {
            // The promoted data file is the existing variant: returned as it is, not reported and not replaced.
            result[1].Path.PathExport.ShouldBe(alias.PromotedFile.PathExport);
            (await ReadAllAsync(result[1])).ShouldBe(alias.SourceData);
            alias.GroupDir.GetChildFiles("b.*").ShouldHaveSingleItem().PathExport.ShouldBe(alias.PromotedFile.PathExport);
            corruption.ShouldBeEmpty();
        }
        else
        {
            // The same marker with its source still missing: a dangling alias, reported once and replaced by the alias of 'p' that the pipeline declares.
            var reported = corruption.ShouldHaveSingleItem();
            reported.Kind.ShouldBe(RepoCorruptionKind.DanglingAlias);
            reported.VariantId.ShouldBe("b");

            result[1].Path.PathExport.ShouldBe(result[0].Path.PathExport);
            (await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"))).ShouldBe([4, 5, 6]);
        }
    }

    // A real 'a' with the aliases 'b' and 'c', and a retirement of 'a' that a crash interrupted right after its commit point: 'a' is retired with its data
    // file still in place, both survivors still alias it, and only a cleanup pass finishes the retirement.
    private async Task<(FileId FileId, RepoFileInfo A, byte[] OriginalData)> AddRealWithTwoAliasesAndCrashItsRetirementAsync(FileRepo repo)
    {
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData()
            .WithVariant("b", VariantPipelines.Alias())
            .WithVariant("c", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        byte[] originalData = await ReadAllAsync(a);

        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.DeleteMarkersWritten)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        a.Path.State.ShouldBe(EntryState.Exists);
        return (fileId, a, originalData);
    }

    // Adds a variant in one of the three ways, by the name of the method without its affixes.
    private async Task<AddResult> AddAsync(FileRepo repo, FileId fileId, string variantId, string addMethod, FileProcessingPipeline pipeline)
    {
        try
        {
            var added = addMethod switch
            {
                "Add" => await repo.AddVariantAsync(fileId, variantId, pipeline, TestContext.CancellationToken),
                "TryAdd" => await repo.TryAddVariantAsync(fileId, variantId, pipeline, TestContext.CancellationToken),
                _ => await repo.GetOrAddVariantAsync(fileId, variantId, pipeline, TestContext.CancellationToken),
            };

            return new AddResult(added, Error: null);
        }
        catch (InvalidOperationException ex)
        {
            return new AddResult(Added: null, ex);
        }
    }

    // What each way of adding answers for a variant that exists: AddVariantAsync throws, TryAddVariantAsync returns null, and GetOrAddVariantAsync returns
    // the variant with the data file it resolves to.
    private static void ShouldBeTheExistingVariant(AddResult result, string addMethod, string variantId, IAbsoluteFilePath expectedFile)
    {
        switch (addMethod)
        {
            case "Add":
                result.Error.ShouldNotBeNull().ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("already exists");
                break;

            case "TryAdd":
                result.Error.ShouldBeNull();
                result.Added.ShouldBeNull();
                break;

            default:
                result.Error.ShouldBeNull();
                var existing = result.Added.ShouldNotBeNull().ShouldHaveSingleItem();
                existing.VariantId.ShouldBe(variantId);
                existing.Path.PathExport.ShouldBe(expectedFile.PathExport);
                break;
        }
    }

    private async Task<MissingSourceAlias> AddAliasWithMissingSourceAsync(FileRepo repo)
    {
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        var a = await repo.GetVariantAsync(fileId, "a");
        byte[] sourceData = await ReadAllAsync(a);
        a.Path.Delete();

        return new MissingSourceAlias(fileId, a.Path.ParentDirectory, a.Extension, sourceData);
    }

    private static async Task<FetchResult> FetchAsync(FileRepo repo, FileId fileId, string variantId, bool viaGroup)
    {
        if (viaGroup)
        {
            var group = await repo.GetGroupAsync(fileId);
            var listed = group.VariantFiles.SingleOrDefault(v => v.VariantId == variantId);

            return new FetchResult(listed, Error: null, group.DanglingAliases.Any(d => d.VariantId == variantId));
        }

        try
        {
            return new FetchResult(await repo.GetVariantAsync(fileId, variantId), Error: null, IsDangling: false);
        }
        catch (RepoFileNotFoundException ex)
        {
            return new FetchResult(File: null, ex, ex is DanglingAliasException);
        }
    }

    /// <summary>
    /// Runs an action each time a fetch has found the source of an alias missing and is about to read again, passing how many times that has happened, and
    /// counts them: the fetch reads the variant's slot (or lists the group, for <paramref name="viaGroup"/>) once more than that. The repository calls the
    /// hook without a lock, so the action can change the group as a retirement that has just finished did.
    /// </summary>
    private static StrongBox<int> OnSourceMissing(FileRepo repo, bool viaGroup, Action<int>? action = null)
    {
        var step = viaGroup ? DebugStep.GroupAliasSourceMissing : DebugStep.VariantAliasSourceMissing;
        var rereads = new StrongBox<int>();

        repo.DebugStepHook = s =>
        {
            if (s != step)
                return;

            rereads.Value++;
            action?.Invoke(rereads.Value);
        };

        return rereads;
    }

    private static List<RepoCorruptionInfo> TrackCorruption(FileRepo repo)
    {
        var captured = new List<RepoCorruptionInfo>();

        repo.CorruptionDetected += info =>
        {
            lock (captured)
                captured.Add(info);

            return Task.CompletedTask;
        };

        return captured;
    }

    private static async Task<byte[]> ReadAllAsync(RepoFileInfo info)
    {
        await using var stream = info.Open(FileAccessPattern.Sequential);
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

        var repo = new FileRepo(dir, RepoFormatVersion.Latest, options => {
            options.DeleteMode = DeleteMode.Immediate;
            options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });
        repo.EnsureCreated();
        return repo;
    }
}
