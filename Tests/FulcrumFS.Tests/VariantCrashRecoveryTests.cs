namespace FulcrumFS;

/// <summary>
/// Exercises crash recovery of the multi-step variant retirement / rebase flow by injecting a simulated crash at precise checkpoints via
/// <see cref="FileRepo.DebugStepHook"/>, then verifying that recovery (cleaner rollforward, or the delete-path optimistic rollforward) converges on the same
/// final state a non-crashed run would produce. Because the flow is strictly forward-only, throwing at a checkpoint leaves exactly the on-disk state a real
/// crash would.
/// </summary>
[PrefixTestClass]
public sealed class VariantCrashRecoveryTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

#pragma warning disable RCS1194 // Implement exception constructors

    private sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException() : base("Simulated crash injected by test.") { }
    }

#pragma warning restore RCS1194

    [TestMethod]
    public async Task Crash_AfterRebaseMarker_BeforeMaterialize_CleanerResumes()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var (fileId, originalData) = await AddRealWithTwoAliasesAsync(repo);

        // Crash right after the rebase marker is pinned but before the chosen survivor is materialized.
        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.RebaseMarkerWritten)
                throw new SimulatedCrashException();
        };

        // The crash propagates out of the rebase, so the call throws. The committed delete marker and the pinned rebase marker are left behind for the cleaner.
        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        var groupDir = (await repo.GetAsync(fileId)).Path.ParentDirectory;

        // Crash state: 'a' is retired (delete marker committed), but the subtree is mid-rebase (marker present, survivors not yet promoted/re-pointed).
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldNotBeEmpty();
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));

        await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        await AssertSurvivorsResolvedAsync(repo, fileId, originalData, groupDir);
    }

    [TestMethod]
    public async Task Crash_AfterMaterialize_BeforeRepoint_CleanerResumes()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var (fileId, originalData) = await AddRealWithTwoAliasesAsync(repo);

        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.RebaseMaterialized)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        var groupDir = (await repo.GetAsync(fileId)).Path.ParentDirectory;

        // Crash state: chosen survivor materialized, but the remaining survivor not yet re-pointed and the marker still present.
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldNotBeEmpty();

        await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        await AssertSurvivorsResolvedAsync(repo, fileId, originalData, groupDir);
    }

    [TestMethod]
    public async Task Crash_AfterRepoint_BeforeResidueDelete_CleanerFinishesDeletion()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var (fileId, originalData) = await AddRealWithTwoAliasesAsync(repo);

        // This checkpoint is outside the rebase try/catch, so the crash propagates out of the delete call.
        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.DeleteResidueAboutToDelete)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        var groupDir = (await repo.GetAsync(fileId)).Path.ParentDirectory;

        // Crash state: rebase fully complete (marker dropped), but the retired source residue has not been physically deleted yet.
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();

        await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        await AssertSurvivorsResolvedAsync(repo, fileId, originalData, groupDir);
    }

    [TestMethod]
    public async Task Crash_MidRebase_OptimisticRollforwardOnNextDelete()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var (fileId, originalData) = await AddRealWithTwoAliasesAsync(repo);

        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.RebaseMaterialized)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        var groupDir = (await repo.GetAsync(fileId)).Path.ParentDirectory;
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldNotBeEmpty();

        // A subsequent mutating operation must roll the pending rebase forward before planning. Deleting a never-existed variant is a no-op for that variant
        // but still triggers the group-wide rollforward.
        await repo.DeleteVariantsAsync(fileId, "does-not-exist");

        // The pending rebase has been rolled forward to completion (no marker left) and both survivors resolve to the promoted data.
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();

        var b = await repo.GetVariantAsync(fileId, "b");
        var c = await repo.GetVariantAsync(fileId, "c");
        b.Path.PathExport.ShouldBe(c.Path.PathExport);
        (await ReadAllAsync(b)).ShouldBe(originalData);
        (await ReadAllAsync(c)).ShouldBe(originalData);
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
    }

    [TestMethod]
    public async Task Crash_MidSecondRebase_CleanerResumesBothSubtrees()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var fileId = await AddBaseFileAsync(repo);

        // Two independent real variants, each with two alias dependents.
        await repo.AddVariantAsync(
            fileId, "a", VariantPipelines.RealData([10, 20, 30]).WithVariant("b", VariantPipelines.Alias()).WithVariant("c", VariantPipelines.Alias()),
            TestContext.CancellationToken);
        await repo.AddVariantAsync(
            fileId, "x", VariantPipelines.RealData([40, 50, 60]).WithVariant("y", VariantPipelines.Alias()).WithVariant("z", VariantPipelines.Alias()),
            TestContext.CancellationToken);

        byte[] aData = await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"));
        byte[] xData = await ReadAllAsync(await repo.GetVariantAsync(fileId, "y"));

        // Plans are built in sorted order (a's subtree before x's), so crashing on the second rebase marker leaves a's subtree fully rebased and x's mid-rebase.
        int markerWrites = 0;
        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.RebaseMarkerWritten && ++markerWrites is 2)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a", "x"));
        repo.DebugStepHook = null;

        var groupDir = (await repo.GetAsync(fileId)).Path.ParentDirectory;
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldNotBeEmpty();

        await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        // Both subtrees resolved independently to their own promoted data.
        var b = await repo.GetVariantAsync(fileId, "b");
        var c = await repo.GetVariantAsync(fileId, "c");
        b.Path.PathExport.ShouldBe(c.Path.PathExport);
        (await ReadAllAsync(b)).ShouldBe(aData);
        (await ReadAllAsync(c)).ShouldBe(aData);

        var y = await repo.GetVariantAsync(fileId, "y");
        var z = await repo.GetVariantAsync(fileId, "z");
        y.Path.PathExport.ShouldBe(z.Path.PathExport);
        (await ReadAllAsync(y)).ShouldBe(xData);
        (await ReadAllAsync(z)).ShouldBe(xData);

        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "x"));
    }

    [TestMethod]
    public async Task Crash_MidRebase_OptimisticRollforwardOnNextAlias_SourcedAdd()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var (fileId, originalData) = await AddRealWithTwoAliasesAsync(repo);

        // Crash right after the rebase marker is pinned but before any survivor is materialized: both 'b' and 'c' still alias the doomed 'a'.
        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.RebaseMarkerWritten)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        var groupDir = (await repo.GetAsync(fileId)).Path.ParentDirectory;
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldNotBeEmpty();

        // GetOrAdd a new variant whose SOURCE is an alias caught in the half-finished rebase ('b' still points at the doomed 'a'). (Add mode would reject the
        // pre-existing source as a collision; GetOrAdd skips that check and resolves the source, hitting the rollforward branch.) Resolving the source forces
        // the add path to observe the root's pending rebase, roll it forward to completion under its own locks, then restart and bind against the settled,
        // single-head subtree.
        await repo.GetOrAddVariantAsync(fileId, "d", sourceVariantId: "b", VariantPipelines.Alias(), TestContext.CancellationToken);

        // The pending rebase has been driven to completion (no marker left); both prior survivors and the new alias resolve to the promoted data; 'a' retired.
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();

        var b = await repo.GetVariantAsync(fileId, "b");
        var c = await repo.GetVariantAsync(fileId, "c");
        var d = await repo.GetVariantAsync(fileId, "d");
        b.Path.PathExport.ShouldBe(c.Path.PathExport);
        d.Path.PathExport.ShouldBe(b.Path.PathExport);
        (await ReadAllAsync(b)).ShouldBe(originalData);
        (await ReadAllAsync(d)).ShouldBe(originalData);
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
    }

    [TestMethod]
    public async Task Crash_AfterHints_BeforeDeleteMarkers_StrayHintSwept_VariantStaysLive()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var (fileId, originalData) = await AddRealWithTwoAliasesAsync(repo);

        // Crash after the cleanup-dir hints are written but before the in-group delete markers (the commit point). This checkpoint is outside the rebase
        // try/catch, so it propagates out of the delete call. A hint alone means nothing - without a matching delete marker the variant is NOT retired.
        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.DeleteHintsWritten)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        // No delete marker committed: 'a' (and its alias dependents) are still live.
        (await ReadAllAsync(await repo.GetVariantAsync(fileId, "a"))).ShouldBe(originalData);

        // The cleaner finds a hint with no matching delete marker -> sweeps the stray hint and leaves the variant live.
        await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        (await ReadAllAsync(await repo.GetVariantAsync(fileId, "a"))).ShouldBe(originalData);
        (await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"))).ShouldBe(originalData);
        (await ReadAllAsync(await repo.GetVariantAsync(fileId, "c"))).ShouldBe(originalData);
    }

    [TestMethod]
    public async Task Crash_MidRebase_NextDeleteOfAnotherVariant_ReentrancyTakeoverFinishesPriorOp()
    {
        using var repo = CreateRepo(DeleteMode.Immediate);
        var (fileId, originalData) = await AddRealWithTwoAliasesAsync(repo);

        // An independent real variant in the same group, with no relation to 'a's subtree.
        await repo.AddVariantAsync(fileId, "x", VariantPipelines.RealData([42, 43, 44]), TestContext.CancellationToken);

        // Crash mid-rebase of 'a's subtree, leaving a pending rebase marker (and committed delete marker + stale cleanup hint for 'a').
        repo.DebugStepHook = step =>
        {
            if (step is DebugStep.RebaseMaterialized)
                throw new SimulatedCrashException();
        };

        await Should.ThrowAsync<SimulatedCrashException>(async () => await repo.DeleteVariantsAsync(fileId, "a"));
        repo.DebugStepHook = null;

        var groupDir = (await repo.GetAsync(fileId)).Path.ParentDirectory;
        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldNotBeEmpty();

        // A brand-new delete of an UNRELATED variant ('x') must, on entry, detect the pending rebase from the crashed op and roll it forward to completion
        // before planning its own retirement - the foreground reentrancy takeover. Afterwards both operations are fully settled.
        await repo.DeleteVariantsAsync(fileId, "x");

        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();

        // 'a's subtree was rolled forward: 'b'/'c' promoted to the original data; 'a' retired.
        await AssertSurvivorsResolvedAsync(repo, fileId, originalData, groupDir);

        // ...and the new delete of 'x' also completed.
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "x"));
    }

    private static async Task AssertSurvivorsResolvedAsync(FileRepo repo, FileId fileId, byte[] originalData, IAbsoluteDirectoryPath groupDir)
    {
        var b = await repo.GetVariantAsync(fileId, "b");
        var c = await repo.GetVariantAsync(fileId, "c");

        b.Extension.ShouldNotBe(FileRepoPaths.AliasMarkerExtension);
        b.Path.PathExport.ShouldBe(c.Path.PathExport);
        (await ReadAllAsync(b)).ShouldBe(originalData);
        (await ReadAllAsync(c)).ShouldBe(originalData);

        groupDir.GetChildFiles($"*{FileRepoPaths.RebaseMarkerExtension}").ShouldBeEmpty();
        await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
    }

    private async Task<(FileId FileId, byte[] OriginalData)> AddRealWithTwoAliasesAsync(FileRepo repo)
    {
        var fileId = await AddBaseFileAsync(repo);

        var pipeline = VariantPipelines.RealData()
            .WithVariant("b", VariantPipelines.Alias())
            .WithVariant("c", VariantPipelines.Alias());
        await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

        byte[] originalData = await ReadAllAsync(await repo.GetVariantAsync(fileId, "b"));
        return (fileId, originalData);
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
        var dir = _appDir.CombineDirectory("RepoRoot_VariantCrash_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

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
