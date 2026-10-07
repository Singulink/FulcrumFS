using System.Globalization;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoReadOnlyTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteFilePath _sampleFile = _appDir.CombineDirectory("SampleFiles").CombineFile("sample.jpg");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task Reader_NextToAWriterHoldingTheRepository_ReadsAndEnumerates_AndRefusesEveryWrite()
    {
        var repoDir = NewRepoDir();
        var writer = CreateWriter(repoDir, DeleteMode.DeferredUntilClean);
        FileRepo? reader = null;

        try
        {
            writer.EnsureInitialized();
            var fileId = await AddSampleFileAsync(writer);
            await writer.AddVariantAsync(fileId, "thumb", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);
            await writer.AddVariantAsync(fileId, "same", VariantPipelines.Alias(), TestContext.CancellationToken);

            reader = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true);
            IFileRepo readerView = reader;

            // The writer holds the repository lock, which a read-only instance does not need.
            reader.EnsureCreated();
            reader.EnsureInitialized();
            reader.IsReadOnly.ShouldBeTrue();
            readerView.IsReadOnly.ShouldBeTrue();
            ((IFileRepo)writer).IsReadOnly.ShouldBeFalse();
            reader.FormatVersion.ShouldBe(writer.FormatVersion);

            byte[] sample = File.ReadAllBytes(_sampleFile.PathExport);

            (await reader.GetAsync(fileId)).Path.ShouldBe((await writer.GetAsync(fileId)).Path);

            await using (var stream = await reader.OpenAsync(fileId, FileAccessPattern.Sequential))
                (await ReadAllAsync(stream)).ShouldBe(sample);

            var group = await reader.GetGroupAsync(fileId);
            group.VariantFiles.Select(v => v.VariantId).ShouldBe(["thumb", "same"], ignoreOrder: true);

            (await reader.GetVariantAsync(fileId, "same")).Path.ShouldBe(group.MainFile.Path);

            await using (var stream = await reader.OpenVariantAsync(fileId, "thumb", FileAccessPattern.Sequential))
                (await ReadAllAsync(stream)).ShouldBe([1, 2, 3]);

            var entry = (await CollectAsync(reader)).ShouldHaveSingleItem();
            entry.FileId.ShouldBe(fileId);
            entry.IsPendingDeletion.ShouldBeFalse();

            // An existing variant is returned; one that would have to be added is refused.
            var existing = await reader.GetOrAddVariantAsync(fileId, "thumb", VariantPipelines.RealData([9]), TestContext.CancellationToken);
            existing.ShouldHaveSingleItem().Path.ShouldBe((await reader.GetVariantAsync(fileId, "thumb")).Path);

            string[] before = Snapshot(repoDir);

            await Should.ThrowAsync<InvalidOperationException>(async () => await reader.BeginTransactionAsync());
            await Should.ThrowAsync<InvalidOperationException>(async () => await reader.BeginStagedTransactionAsync());
            await Should.ThrowAsync<InvalidOperationException>(async () => await readerView.BeginTransactionAsync());
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await reader.AddVariantAsync(fileId, "new", VariantPipelines.RealData([4]), TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await reader.TryAddVariantAsync(fileId, "new", VariantPipelines.RealData([4]), TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await reader.GetOrAddVariantAsync(fileId, "new", VariantPipelines.RealData([4]), TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () => await reader.DeleteVariantsAsync(fileId, "thumb"));
            await Should.ThrowAsync<InvalidOperationException>(async () => await reader.DeleteVariantsAsync(fileId));
            await Should.ThrowAsync<InvalidOperationException>(async () => await reader.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await reader.CleanAsync(TimeSpan.Zero, _ => IndeterminateResolution.Keep, TestContext.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await reader.CleanAsync(TimeSpan.Zero, _ => Task.FromResult(IndeterminateResolution.Keep), TestContext.CancellationToken));

            // Nothing was written by any of it.
            Snapshot(repoDir).ShouldBe(before);
            (await reader.GetGroupAsync(fileId)).VariantFiles.Count.ShouldBe(2);

            // The writer still holds the repository and keeps working.
            await using (var txn = await writer.BeginTransactionAsync())
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            (await CollectAsync(reader)).ShouldHaveSingleItem().IsPendingDeletion.ShouldBeTrue();
        }
        finally
        {
            reader?.Dispose();
            writer.Dispose();
        }
    }

    [TestMethod]
    public async Task Reader_StreamOpenWhileTheWriterDeletesTheFile_ReadsToTheEnd()
    {
        var repoDir = NewRepoDir();
        var writer = CreateWriter(repoDir, DeleteMode.Immediate);
        var reader = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true);

        try
        {
            var fileId = await AddSampleFileAsync(writer);
            byte[] sample = File.ReadAllBytes(_sampleFile.PathExport);

            await using (var stream = await reader.OpenAsync(fileId, FileAccessPattern.Sequential))
            {
                byte[] start = new byte[100];
                await stream.ReadExactlyAsync(start, TestContext.CancellationToken);

                await using (var txn = await writer.BeginTransactionAsync())
                {
                    await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                    await txn.CommitAsync(TestContext.CancellationToken);
                }

                // The deletion removed the file from disk while the reader still had it open (the stream shares deletion).
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);

                byte[] rest = await ReadAllAsync(stream);
                start.Concat(rest).ShouldBe(sample);
            }

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await reader.OpenAsync(fileId, FileAccessPattern.Sequential));
        }
        finally
        {
            reader.Dispose();
            writer.Dispose();
        }
    }

    [TestMethod]
    public void Reader_NonExistentOrEmptyDirectory_EnsureCreatedThrowsDirectoryNotFound_AsEnsureInitializedDoes_AndCreatesNothing()
    {
        var repoDir = NewRepoDir();
        repoDir.Delete();

        using (var reader = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true))
        {
            Should.Throw<DirectoryNotFoundException>(reader.EnsureCreated);
            repoDir.State.ShouldNotBe(EntryState.Exists);

            Should.Throw<DirectoryNotFoundException>(reader.EnsureInitialized);
            repoDir.State.ShouldNotBe(EntryState.Exists);
        }

        repoDir.Create();

        using (var reader = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true))
        {
            // The message does not send the caller back to EnsureCreated on this instance, which cannot create the repository.
            Should.Throw<DirectoryNotFoundException>(reader.EnsureCreated).Message.ShouldContain("cannot be created by an instance opened read-only");
            repoDir.GetChildEntries().ShouldBeEmpty();

            Should.Throw<DirectoryNotFoundException>(reader.EnsureInitialized);
            repoDir.GetChildEntries().ShouldBeEmpty();
        }
    }

    [TestMethod]
    public async Task Reader_TakesNoLock_LeavesTheTempDirectoryAlone_AndRunsNoHealthChecks()
    {
        var repoDir = NewRepoDir();
        FileId fileId;

        using (var writer = CreateWriter(repoDir, DeleteMode.DeferredUntilClean))
            fileId = await AddSampleFileAsync(writer);

        // Initializing a writer wipes the temp directory; a reader must not.
        var tempFile = repoDir.CombineDirectory(FileRepoPaths.TempDirectoryName, PathOptions.None).CombineFile("work.bin", PathOptions.None);
        File.WriteAllBytes(tempFile.PathExport, [1]);

        var lockFile = repoDir.CombineFile(FileRepoPaths.RepoLockFileName, PathOptions.None);
        lockFile.State.ShouldNotBe(EntryState.Exists);

        var reader = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true);

        try
        {
            // EnsureCreated opens an existing repository read-only, as EnsureInitialized does.
            reader.EnsureCreated();
            lockFile.State.ShouldNotBe(EntryState.Exists);
            tempFile.State.ShouldBe(EntryState.Exists);

            reader.EnsureInitialized();
            (await reader.GetAsync(fileId)).FileId.ShouldBe(fileId);
            (await CollectAsync(reader)).ShouldHaveSingleItem();

            lockFile.State.ShouldNotBe(EntryState.Exists);
            tempFile.State.ShouldBe(EntryState.Exists);
            reader.HealthChecksActive.ShouldBeFalse();

            // A writer can still take the repository while the reader is open.
            using var writer = CreateWriter(repoDir, DeleteMode.DeferredUntilClean);
            writer.EnsureInitialized();
            tempFile.State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            reader.Dispose();
        }
    }

    [TestMethod]
    public async Task Reader_AfterDispose_ThrowsObjectDisposed()
    {
        var repoDir = NewRepoDir();
        FileId fileId;

        using (var writer = CreateWriter(repoDir, DeleteMode.DeferredUntilClean))
            fileId = await AddSampleFileAsync(writer);

        var reader = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true);
        (await reader.GetAsync(fileId)).FileId.ShouldBe(fileId);
        reader.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await reader.GetAsync(fileId));
        Should.Throw<ObjectDisposedException>(reader.EnsureInitialized);
    }

    private static string[] Snapshot(IAbsoluteDirectoryPath repoDir) =>
        [.. Directory.GetFileSystemEntries(repoDir.PathExport, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private async Task<List<RepoFileEntry>> CollectAsync(FileRepo repo)
    {
        var entries = new List<RepoFileEntry>();

        await foreach (var entry in repo.EnumerateFilesAsync(TestContext.CancellationToken))
            entries.Add(entry);

        return entries;
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleFile.PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    private async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.CancellationToken);
        return buffer.ToArray();
    }

    private static FileRepo CreateWriter(IAbsoluteDirectoryPath repoDir, DeleteMode deleteMode)
    {
        var repo = new FileRepo(repoDir, RepoFormatVersion.Latest, o => {
            o.DeleteMode = deleteMode;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });

        repo.EnsureCreated();
        return repo;
    }

    private static IAbsoluteDirectoryPath FileDir(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_FileRepoReadOnly_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
