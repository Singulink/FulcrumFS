using System.Globalization;
using System.Security.Cryptography;

namespace FulcrumFS;

/// <summary>
/// Uses a repository only through <see cref="IFileRepo"/> and the interfaces it returns, to prove that they are sufficient for an application. The local
/// <see cref="FileRepo"/> appears only where a repository is created and disposed, the tests of the two events damage the repository through the file
/// system to provoke what the events report, and the tests of sizes remove files from storage to show where a size comes from (the one about fetches asks
/// the local type, since the interface leaves that to the implementation).
/// </summary>
[PrefixTestClass]
public sealed class IFileRepoConformanceTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteFilePath _sampleFile = _appDir.CombineDirectory("SampleFiles").CombineFile("sample.jpg");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task Add_ThenReadBackThroughOpenReadAsyncAndOpenAsync()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            byte[] sample = File.ReadAllBytes(_sampleFile.PathExport);
            IRepoFileGroup fromStream;
            IRepoFileGroup fromFileStream;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                txn.Repository.ShouldBeSameAs(repo);

                await using (var source = new MemoryStream(sample))
                    fromStream = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);

                // The FileStream overload takes the extension from the file name.
                await using (var source = File.OpenRead(_sampleFile.PathExport))
                    fromFileStream = await txn.AddAsync(source, leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            fromStream.FileId.ShouldNotBe(fromFileStream.FileId);

            foreach (var added in new[] { fromStream, fromFileStream })
            {
                added.MainFile.FileId.ShouldBe(added.FileId);
                added.MainFile.VariantId.ShouldBeNull();
                added.MainFile.Extension.ShouldBe(".jpg");
                (await ReadAllAsync(added.MainFile)).ShouldBe(sample);
                added.VariantFiles.ShouldBeEmpty();
                added.DanglingAliases.ShouldBeEmpty();

                var file = await repo.GetAsync(added.FileId);
                file.FileId.ShouldBe(added.FileId);
                file.VariantId.ShouldBeNull();
                file.Extension.ShouldBe(".jpg");

                // The length of the opened stream is the size of the file.
                await using (var stream = await file.OpenReadAsync(FileAccessPattern.Sequential, TestContext.CancellationToken))
                {
                    stream.CanRead.ShouldBeTrue();
                    stream.CanWrite.ShouldBeFalse();
                    stream.Length.ShouldBe(sample.Length);
                    (await ReadAllAsync(stream)).ShouldBe(sample);
                }

                await using (var stream = await repo.OpenAsync(added.FileId, FileAccessPattern.Sequential))
                {
                    stream.CanWrite.ShouldBeFalse();
                    stream.Length.ShouldBe(sample.Length);
                    (await ReadAllAsync(stream)).ShouldBe(sample);
                }
            }

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(FileId.CreateSecure()));
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.OpenAsync(FileId.CreateSecure(), FileAccessPattern.Sequential));
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    [DataRow(FileAccessPattern.Sequential)]
    [DataRow(FileAccessPattern.Random)]
    public async Task Open_WithEitherAccessPattern_ReturnsASeekableStream(FileAccessPattern accessPattern)
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            // Larger than a stream's read buffer, so that the seeks land outside what the previous read buffered.
            byte[] mainContent = RandomNumberGenerator.GetBytes(20_000);
            byte[] variantContent = RandomNumberGenerator.GetBytes(10_000);
            FileId fileId;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await using (var source = new MemoryStream(mainContent))
                    fileId = (await txn.AddAsync(source, ".bin", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.RealData(variantContent), TestContext.CancellationToken);

            var main = await repo.GetAsync(fileId);
            var variant = await repo.GetVariantAsync(fileId, "thumb");

            await using (var stream = await main.OpenReadAsync(accessPattern, TestContext.CancellationToken))
                await ShouldBeSeekableAsync(stream, mainContent);

            await using (var stream = await repo.OpenAsync(fileId, accessPattern))
                await ShouldBeSeekableAsync(stream, mainContent);

            await using (var stream = await variant.OpenReadAsync(accessPattern, TestContext.CancellationToken))
                await ShouldBeSeekableAsync(stream, variantContent);

            await using (var stream = await repo.OpenVariantAsync(fileId, "thumb", accessPattern))
                await ShouldBeSeekableAsync(stream, variantContent);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task GetLength_IsTheSizeOfTheContent_ForTheMainFileAndItsVariants()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            byte[] mainContent = RandomNumberGenerator.GetBytes(20_000);
            byte[] variantContent = RandomNumberGenerator.GetBytes(10_000);
            IRepoFileGroup added;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await using (var source = new MemoryStream(mainContent))
                    added = await txn.AddAsync(source, ".bin", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            var fileId = added.FileId;
            var addedThumb = await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.RealData(variantContent), TestContext.CancellationToken);

            // 'small' is an alias of 'thumb', so its size is the size of the content it shares.
            var addedSmall = await repo.AddVariantAsync(fileId, "small", "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);

            // Every way the interface hands out a file reports its size: the results of the adds, the fetches and the members of the group.
            (await added.MainFile.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(mainContent.Length);
            (await addedThumb.ShouldHaveSingleItem().GetLengthAsync(TestContext.CancellationToken)).ShouldBe(variantContent.Length);
            (await addedSmall.ShouldHaveSingleItem().GetLengthAsync(TestContext.CancellationToken)).ShouldBe(variantContent.Length);

            var main = await repo.GetAsync(fileId);
            var thumb = await repo.GetVariantAsync(fileId, "thumb");
            (await main.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(mainContent.Length);
            (await thumb.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(variantContent.Length);
            (await (await repo.GetVariantAsync(fileId, "small")).GetLengthAsync(TestContext.CancellationToken)).ShouldBe(variantContent.Length);

            var group = await repo.GetGroupAsync(fileId);
            (await group.MainFile.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(mainContent.Length);
            group.VariantFiles.Count.ShouldBe(2);

            foreach (var variant in group.VariantFiles)
                (await variant.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(variantContent.Length);

            // Asking again gives the same answer, and it agrees with the length of a stream opened over the file.
            (await main.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(mainContent.Length);

            await using (var stream = await thumb.OpenReadAsync(FileAccessPattern.Random, TestContext.CancellationToken))
                stream.Length.ShouldBe(await thumb.GetLengthAsync(TestContext.CancellationToken));
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task GetLength_OfTheFilesATransactionalAddReturns_IsKnownWithoutStorage()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            byte[] mainContent = RandomNumberGenerator.GetBytes(20_000);
            byte[] thumbContent = RandomNumberGenerator.GetBytes(10_000);

            // A declared variant with an alias of its own ('small'), and an alias of the main file ('same').
            var pipeline = FileProcessingPipeline.Empty
                .WithVariant("thumb", VariantPipelines.RealData(thumbContent).WithVariant("small", VariantPipelines.Alias()))
                .WithVariant("same", VariantPipelines.Alias());

            IRepoFileGroup added;
            IRepoFileGroup addedStaged;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await using (var source = new MemoryStream(mainContent))
                    added = await txn.AddAsync(source, ".bin", leaveOpen: true, pipeline, TestContext.CancellationToken);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await using (var source = File.OpenRead(_sampleFile.PathExport))
                    addedStaged = await txn.AddAsync(source, leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);

                await txn.StageAndCommitAsync(TestContext.CancellationToken);
            }

            added.VariantFiles.Select(v => v.VariantId).ShouldBe(["thumb", "small", "same"]);
            addedStaged.VariantFiles.ShouldBeEmpty();

            RemoveFromStorage(repoDir, added.FileId);
            RemoveFromStorage(repoDir, addedStaged.FileId);

            await ShouldKnowTheSizeAsync(added.MainFile, mainContent.Length);
            await ShouldKnowTheSizeAsync(added.VariantFiles[0], thumbContent.Length);
            await ShouldKnowTheSizeAsync(added.VariantFiles[1], thumbContent.Length);
            await ShouldKnowTheSizeAsync(added.VariantFiles[2], mainContent.Length);
            await ShouldKnowTheSizeAsync(addedStaged.MainFile, _sampleFile.Length);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task GetLength_OfTheVariantsAVariantAddStores_IsKnownWithoutStorage()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            byte[] mainContent = RandomNumberGenerator.GetBytes(20_000);
            byte[] aContent = RandomNumberGenerator.GetBytes(10_000);
            byte[] cContent = RandomNumberGenerator.GetBytes(5_000);
            byte[] dContent = RandomNumberGenerator.GetBytes(3_000);
            FileId fileId;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await using (var source = new MemoryStream(mainContent))
                    fileId = (await txn.AddAsync(source, ".bin", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            // Every variant an add stored, with the size of its content. An alias has the size of the data it resolves to, whether the same add stored that
            // data ('a-copy') or it was stored before ('same' of the main file, and 'b' and 'd-copy' of 'a').
            var stored = new List<(IRepoFile File, long Size)>();

            var withCopy = VariantPipelines.RealData(aContent).WithVariant("a-copy", VariantPipelines.Alias());
            var a = await repo.AddVariantAsync(fileId, "a", withCopy, TestContext.CancellationToken);
            a.Select(v => v.VariantId).ShouldBe(["a", "a-copy"]);
            stored.Add((a[0], aContent.Length));
            stored.Add((a[1], aContent.Length));

            var same = await repo.AddVariantAsync(fileId, "same", VariantPipelines.Alias(), TestContext.CancellationToken);
            stored.Add((same.ShouldHaveSingleItem(), mainContent.Length));

            var b = await repo.AddVariantAsync(fileId, "b", "a", VariantPipelines.Alias(), TestContext.CancellationToken);
            stored.Add((b.ShouldHaveSingleItem(), aContent.Length));

            var c = await repo.TryAddVariantAsync(fileId, "c", VariantPipelines.RealData(cContent), TestContext.CancellationToken);
            stored.Add((c.ShouldNotBeNull().ShouldHaveSingleItem(), cContent.Length));

            // 'a' exists, so get-or-add returns it as it is and adds only the variants declared under it. The existing one may read its size on demand.
            var tree = VariantPipelines.RealData([9]).WithVariant("d", VariantPipelines.RealData(dContent)).WithVariant("d-copy", VariantPipelines.Alias());
            var d = await repo.GetOrAddVariantAsync(fileId, "a", tree, TestContext.CancellationToken);
            d.Select(v => v.VariantId).ShouldBe(["a", "d", "d-copy"]);
            stored.Add((d[1], dContent.Length));
            stored.Add((d[2], aContent.Length));

            RemoveFromStorage(repoDir, fileId);

            foreach (var (file, size) in stored)
                await ShouldKnowTheSizeAsync(file, size);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task GetLength_OfAFetchedFile_IsNotReadByTheFetch()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);

            var main = await repo.GetAsync(fileId);
            var thumb = await repo.GetVariantAsync(fileId, "thumb");
            var group = await repo.GetGroupAsync(fileId);
            IRepoFile[] fetched = [main, thumb, group.MainFile, group.VariantFiles.ShouldHaveSingleItem()];

            RemoveFromStorage(repoDir, fileId);

            // The interface lets an implementation know the size of a fetched file (from the metadata a remote fetch returned, for example), so the local type
            // is asked instead: none of its fetches read a size, so with the files gone none of them can produce one.
            foreach (var file in fetched)
                Should.Throw<Exception>(() => ((RepoFileInfo)file).Length).ShouldBeAssignableTo<IOException>();
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task GetGroup_ListsTheMainFileAndItsVariant()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            // The progress of the variant's pipeline arrives through the interface's task before its result.
            var addTask = repo.AddVariantAsync(fileId, "Thumb", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);
            var progress = new List<ProgressValue>();

            await foreach (var value in addTask.WithCancellation(TestContext.CancellationToken))
                progress.Add(value);

            var added = await addTask;

            progress.ShouldBe([new("thumb", nameof(RewriteProcessor), 0.0), new("thumb", nameof(RewriteProcessor), 1.0)]);
            var addedVariant = added.ShouldHaveSingleItem();
            addedVariant.FileId.ShouldBe(fileId);
            addedVariant.VariantId.ShouldBe("thumb");

            var group = await repo.GetGroupAsync(fileId);
            group.FileId.ShouldBe(fileId);
            group.MainFile.FileId.ShouldBe(fileId);
            group.MainFile.VariantId.ShouldBeNull();
            group.MainFile.Extension.ShouldBe(".jpg");
            group.DanglingAliases.ShouldBeEmpty();

            var listed = group.VariantFiles.ShouldHaveSingleItem();
            listed.FileId.ShouldBe(fileId);
            listed.VariantId.ShouldBe("thumb");
            listed.Extension.ShouldBe(".jpg");
            (await ReadAllAsync(listed)).ShouldBe([1, 2, 3]);

            var fetched = await repo.GetVariantAsync(fileId, "thumb");
            fetched.FileId.ShouldBe(fileId);
            fetched.VariantId.ShouldBe("thumb");
            (await ReadAllAsync(fetched)).ShouldBe([1, 2, 3]);

            // Variant IDs are case-insensitive.
            await using (var stream = await repo.OpenVariantAsync(fileId, "THUMB", FileAccessPattern.Sequential))
                (await ReadAllAsync(stream)).ShouldBe([1, 2, 3]);

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "missing"));
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.OpenVariantAsync(fileId, "missing", FileAccessPattern.Sequential));
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task GetOrAddVariant_AddsOnce_ThenReturnsTheExistingVariant()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            var first = await repo.GetOrAddVariantAsync(fileId, "thumb", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);
            first.ShouldHaveSingleItem().VariantId.ShouldBe("thumb");
            (await ReadAllAsync(first[0])).ShouldBe([1, 2, 3]);

            // The variant exists now, so the different pipeline passed this time does not run and the stored content is returned.
            var second = await repo.GetOrAddVariantAsync(fileId, "thumb", VariantPipelines.RealData([9, 9]), TestContext.CancellationToken);
            second.ShouldHaveSingleItem().VariantId.ShouldBe("thumb");
            (await ReadAllAsync(second[0])).ShouldBe([1, 2, 3]);

            // From a source variant: 'small' is an alias of 'thumb' and shares its content.
            var fromSource = await repo.GetOrAddVariantAsync(fileId, "small", "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);
            fromSource.ShouldHaveSingleItem().VariantId.ShouldBe("small");
            (await ReadAllAsync(fromSource[0])).ShouldBe([1, 2, 3]);

            (await repo.GetGroupAsync(fileId)).VariantFiles.Select(v => v.VariantId).ShouldBe(["small", "thumb"], ignoreOrder: true);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task TryAddAddAndDeleteVariants()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            var a = await repo.TryAddVariantAsync(fileId, "a", VariantPipelines.RealData([1]), TestContext.CancellationToken);
            a.ShouldNotBeNull().ShouldHaveSingleItem().VariantId.ShouldBe("a");

            // TryAdd returns null for an existing variant, and Add throws.
            (await repo.TryAddVariantAsync(fileId, "a", VariantPipelines.RealData([2]), TestContext.CancellationToken)).ShouldBeNull();
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await repo.AddVariantAsync(fileId, "a", VariantPipelines.RealData([2]), TestContext.CancellationToken));

            var b = await repo.TryAddVariantAsync(fileId, "b", "a", VariantPipelines.RealData([3]), TestContext.CancellationToken);
            b.ShouldNotBeNull().ShouldHaveSingleItem().VariantId.ShouldBe("b");

            var c = await repo.AddVariantAsync(fileId, "c", "b", VariantPipelines.Alias(), TestContext.CancellationToken);
            c.ShouldHaveSingleItem().VariantId.ShouldBe("c");
            (await ReadAllAsync(c[0])).ShouldBe([3]);

            // 'c' depends on 'b' but is not listed, so it survives the retirement of 'b' with its content, and only the listed variants are retired.
            (await repo.DeleteVariantsAsync(fileId, "a", "b")).ShouldBe(["a", "b"]);

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "a"));
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "b"));
            (await ReadAllAsync(await repo.GetVariantAsync(fileId, "c"))).ShouldBe([3]);
            (await repo.GetGroupAsync(fileId)).VariantFiles.ShouldHaveSingleItem().VariantId.ShouldBe("c");

            // Retiring again, or retiring a variant that never existed, does nothing.
            (await repo.DeleteVariantsAsync(fileId, "a", "never")).ShouldBeEmpty();
            (await ReadAllAsync(await repo.GetVariantAsync(fileId, "c"))).ShouldBe([3]);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task Transaction_DeleteAndTryDelete_TakeEffectOnCommit()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate);

        try
        {
            var kept = await AddSampleFileAsync(repo);
            var deleted = await AddSampleFileAsync(repo);
            var tryDeleted = await AddSampleFileAsync(repo);
            var missing = FileId.CreateSecure();

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await txn.DeleteAsync(deleted, TestContext.CancellationToken);
                (await txn.TryDeleteAsync(tryDeleted, TestContext.CancellationToken)).ShouldBeTrue();
                (await txn.TryDeleteAsync(missing, TestContext.CancellationToken)).ShouldBeFalse();
                await Should.ThrowAsync<RepoFileNotFoundException>(() => txn.DeleteAsync(missing, TestContext.CancellationToken));

                // Nothing is deleted before the transaction commits.
                (await repo.GetAsync(deleted)).FileId.ShouldBe(deleted);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(deleted));
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(tryDeleted));
            (await repo.GetAsync(kept)).FileId.ShouldBe(kept);

            // Replaying a deletion that already happened is harmless with TryDeleteAsync and throws with DeleteAsync.
            await using (var txn = await repo.BeginTransactionAsync())
            {
                (await txn.TryDeleteAsync(deleted, TestContext.CancellationToken)).ShouldBeFalse();
                await Should.ThrowAsync<RepoFileNotFoundException>(() => txn.DeleteAsync(tryDeleted, TestContext.CancellationToken));
                await txn.CommitAsync(TestContext.CancellationToken);
            }
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task Transaction_RollbackOrDispose_DiscardsAddsAndKeepsDeletedFiles()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate);

        try
        {
            var existing = await AddSampleFileAsync(repo);
            FileId rolledBack;
            FileId disposed;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                rolledBack = await AddSampleFileAsync(txn);
                await txn.DeleteAsync(existing, TestContext.CancellationToken);
                await txn.RollbackAsync(TestContext.CancellationToken);
            }

            await using (var txn = await repo.BeginTransactionAsync())
            {
                disposed = await AddSampleFileAsync(txn);
                await txn.DeleteAsync(existing, TestContext.CancellationToken);
            }

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(rolledBack));
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(disposed));
            (await repo.GetAsync(existing)).FileId.ShouldBe(existing);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task Transaction_CommitAfterDispose_ThrowsObjectDisposed_AndCommitsNothing()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate);

        try
        {
            var existing = await AddSampleFileAsync(repo);

            var txn = await repo.BeginTransactionAsync();
            var added = await AddSampleFileAsync(txn);
            await txn.DeleteAsync(existing, TestContext.CancellationToken);
            await txn.DisposeAsync();

            // Disposal rolled the transaction back. A commit that returned normally would say that the added file is in the repository.
            await Should.ThrowAsync<ObjectDisposedException>(() => txn.CommitAsync(TestContext.CancellationToken));

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(added));
            (await repo.GetAsync(existing)).FileId.ShouldBe(existing);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StagedTransaction_StageCommitAndClean(RepoFormatVersion formatVersion)
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean, formatVersion: formatVersion);

        try
        {
            var fileIds = new[] { await AddSampleFileAsync(repo), await AddSampleFileAsync(repo), await AddSampleFileAsync(repo) };
            FileId added;

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                txn.Repository.ShouldBeSameAs(repo);

                await using (var source = File.OpenRead(_sampleFile.PathExport))
                    added = (await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;

                await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken);
                (await txn.TryDeleteAsync(fileIds[1], TestContext.CancellationToken)).ShouldBeTrue();
                (await txn.TryDeleteAsync(FileId.CreateSecure(), TestContext.CancellationToken)).ShouldBeFalse();

                var stage = await txn.StageAsync(TestContext.CancellationToken);

                // Nothing can be deleted by a transaction once it is staged.
                await Should.ThrowAsync<InvalidOperationException>(() => txn.DeleteAsync(fileIds[2], TestContext.CancellationToken));

                await stage.CommitAsync(TestContext.CancellationToken);
            }

            // In a deferred delete mode the deleted files stay readable until a cleanup pass removes them.
            (await repo.GetAsync(fileIds[0])).FileId.ShouldBe(fileIds[0]);

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileIds[0]));
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileIds[1]));
            (await repo.GetAsync(fileIds[2])).FileId.ShouldBe(fileIds[2]);
            (await repo.GetAsync(added)).FileId.ShouldBe(added);

            // StageAndCommitAsync performs both steps in one call.
            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await txn.DeleteAsync(fileIds[2], TestContext.CancellationToken);
                await txn.StageAndCommitAsync(TestContext.CancellationToken);
            }

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileIds[2]));
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StagedTransaction_RollbackAfterStaging_DiscardsAddsAndDeletions(RepoFormatVersion formatVersion)
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate, formatVersion: formatVersion);

        try
        {
            var existing = await AddSampleFileAsync(repo);
            FileId added;

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await using (var source = File.OpenRead(_sampleFile.PathExport))
                    added = (await txn.AddAsync(source, leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;

                await txn.DeleteAsync(existing, TestContext.CancellationToken);
                await txn.StageAsync(TestContext.CancellationToken);
                await txn.RollbackAsync(TestContext.CancellationToken);
            }

            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(added));
            (await repo.GetAsync(existing)).FileId.ShouldBe(existing);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task Commit_GivenACancelledToken_CommitsEverythingOrThrowsHavingCommittedNothing()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate);

        try
        {
            var existing = await AddSampleFileAsync(repo);
            FileId added;
            bool wasCancelled = false;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                added = await AddSampleFileAsync(txn);
                await txn.DeleteAsync(existing, TestContext.CancellationToken);

                try
                {
                    await txn.CommitAsync(new CancellationToken(canceled: true));
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                }
            }

            // An implementation may honor the token or not, but only before it commits anything. A commit that was cancelled has committed nothing and left
            // the transaction open, so disposing it rolled all of it back; one that was not is committed in full.
            if (wasCancelled)
            {
                await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(added));
                (await repo.GetAsync(existing)).FileId.ShouldBe(existing);
            }
            else
            {
                (await repo.GetAsync(added)).FileId.ShouldBe(added);
                await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(existing));
            }
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version1)]
    [DataRow(RepoFormatVersion.Version2)]
    public async Task StagedTransaction_GivenACancelledToken_StagesAndCommitsOrThrowsHavingDoneNeither(RepoFormatVersion formatVersion)
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate, formatVersion: formatVersion);

        try
        {
            var existing = await AddSampleFileAsync(repo);
            var cancelled = new CancellationToken(canceled: true);
            FileId added;

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await using (var source = File.OpenRead(_sampleFile.PathExport))
                    added = (await txn.AddAsync(source, leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;

                await txn.DeleteAsync(existing, TestContext.CancellationToken);

                // A call that was cancelled did nothing, so the transaction can still be staged, and the stage still committed.
                IFileRepoStage stage;

                try
                {
                    stage = await txn.StageAsync(cancelled);
                }
                catch (OperationCanceledException)
                {
                    stage = await txn.StageAsync(TestContext.CancellationToken);
                }

                try
                {
                    await stage.CommitAsync(cancelled);
                }
                catch (OperationCanceledException)
                {
                    await stage.CommitAsync(TestContext.CancellationToken);
                }
            }

            (await repo.GetAsync(added)).FileId.ShouldBe(added);
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(existing));
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    [DataRow(false, RepoFormatVersion.Version1, DeleteMode.Immediate, false)]
    [DataRow(false, RepoFormatVersion.Version1, DeleteMode.Immediate, true)]
    [DataRow(false, RepoFormatVersion.Version2, DeleteMode.DeferredUntilClean, false)]
    [DataRow(false, RepoFormatVersion.Version2, DeleteMode.DeferredUntilClean, true)]
    [DataRow(true, RepoFormatVersion.Version1, DeleteMode.Immediate, false)]
    [DataRow(true, RepoFormatVersion.Version1, DeleteMode.Immediate, true)]
    [DataRow(true, RepoFormatVersion.Version1, DeleteMode.DeferredUntilClean, false)]
    [DataRow(true, RepoFormatVersion.Version1, DeleteMode.DeferredUntilClean, true)]
    [DataRow(true, RepoFormatVersion.Version2, DeleteMode.Immediate, false)]
    [DataRow(true, RepoFormatVersion.Version2, DeleteMode.Immediate, true)]
    [DataRow(true, RepoFormatVersion.Version2, DeleteMode.DeferredUntilClean, false)]
    [DataRow(true, RepoFormatVersion.Version2, DeleteMode.DeferredUntilClean, true)]
    public async Task Delete_OfAFileAnotherTransactionIsAdding_IsRefusedOrWins(
        bool staged, RepoFormatVersion formatVersion, DeleteMode deleteMode, bool addCommits)
    {
        var repo = CreateRepo(NewRepoDir(), deleteMode, formatVersion: formatVersion);

        try
        {
            await using var adding = await repo.BeginTransactionAsync();
            var fileId = await AddSampleFileAsync(adding);

            // The add has returned, so the application may have committed a row for the file that another of its transactions now deletes.
            bool isDeleted = staged ? await DeleteInStagedTransactionAsync(fileId) : await DeleteInTransactionAsync(fileId);

            // The adding transaction finishes afterwards, without an error either way.
            if (addCommits)
                await adding.CommitAsync(TestContext.CancellationToken);
            else
                await adding.RollbackAsync(TestContext.CancellationToken);

            await repo.CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

            // A deletion that committed stands whatever the add did afterwards, and one that was refused recorded nothing, so the file is what its add
            // made of it.
            if (isDeleted || !addCommits)
                await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(fileId));
            else
                (await repo.GetAsync(fileId)).FileId.ShouldBe(fileId);
        }
        finally
        {
            DisposeRepo(repo);
        }

        // An implementation that cannot let the deletion win refuses it with an IOException, which is not the exception for a file that is missing. It
        // must do so before the commit, which never throws for this.
        async Task<bool> DeleteInTransactionAsync(FileId fileId)
        {
            await using var txn = await repo.BeginTransactionAsync();

            try
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                return false;
            }

            await txn.CommitAsync(TestContext.CancellationToken);
            return true;
        }

        // A staged transaction must refuse it by the time it is staged, while the application can still roll back its database.
        async Task<bool> DeleteInStagedTransactionAsync(FileId fileId)
        {
            await using var txn = await repo.BeginStagedTransactionAsync();
            IFileRepoStage stage;

            try
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                stage = await txn.StageAsync(TestContext.CancellationToken);
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                return false;
            }

            await stage.CommitAsync(TestContext.CancellationToken);
            return true;
        }
    }

    [TestMethod]
    public async Task Clean_WhileATransactionIsOpen_LeavesItsFilesAlone()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.Immediate);

        try
        {
            var existing = await AddSampleFileAsync(repo);
            FileId added;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                added = await AddSampleFileAsync(txn);
                await txn.DeleteAsync(existing, TestContext.CancellationToken);

                // The callback would have every file it is asked about deleted, but it is not asked about the files of a transaction that can still
                // commit or roll back.
                var asked = new List<FileId>();

                await repo.CleanAsync(
                    TimeSpan.Zero,
                    id =>
                    {
                        asked.Add(id);
                        return IndeterminateResolution.Delete;
                    },
                    TestContext.CancellationToken);

                asked.ShouldBeEmpty();
                (await repo.GetAsync(added)).FileId.ShouldBe(added);
                (await repo.GetAsync(existing)).FileId.ShouldBe(existing);

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            (await repo.GetAsync(added)).FileId.ShouldBe(added);
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(existing));
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task EnumerateFiles_ReportsEveryFileGroup()
    {
        var repo = CreateRepo(NewRepoDir(), DeleteMode.DeferredUntilClean);

        try
        {
            var fileIds = new[] { await AddSampleFileAsync(repo), await AddSampleFileAsync(repo), await AddSampleFileAsync(repo) };

            await using (var txn = await repo.BeginTransactionAsync())
            {
                await txn.DeleteAsync(fileIds[0], TestContext.CancellationToken);
                await txn.CommitAsync(TestContext.CancellationToken);
            }

            var entries = new List<RepoFileEntry>();

            await foreach (var entry in repo.EnumerateFilesAsync(TestContext.CancellationToken))
                entries.Add(entry);

            entries.Select(e => e.FileId).ShouldBe(fileIds, ignoreOrder: true);
            entries.Single(e => e.FileId == fileIds[0]).IsPendingDeletion.ShouldBeTrue();
            entries.Where(e => e.FileId != fileIds[0]).ShouldAllBe(e => !e.IsPendingDeletion && !e.IsIndeterminate);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate, FileIdMode.Secure)]
    [DataRow(DeleteMode.DeferredUntilClean, FileIdMode.Sequential)]
    [DataRow(DeleteMode.DeferredFilesOnly, FileIdMode.Secure)]
    public async Task DeleteModeAndFileIdMode_ReflectTheOptions(DeleteMode deleteMode, FileIdMode fileIdMode)
    {
        var repo = CreateRepo(NewRepoDir(), deleteMode, fileIdMode);

        try
        {
            repo.DeleteMode.ShouldBe(deleteMode);
            repo.FileIdMode.ShouldBe(fileIdMode);

            // The IDs the repository generates follow the mode it reports.
            var fileId = await AddSampleFileAsync(repo);
            fileId.Guid.Version.ShouldBe(fileIdMode is FileIdMode.Sequential ? 7 : 4);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task TransactionCompletionFailed_IsRaisedThroughTheInterface()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var failures = new List<RepoTransactionCompletionFailureInfo>();

        repo.TransactionCompletionFailed += failure =>
        {
            lock (failures)
                failures.Add(failure);

            return Task.CompletedTask;
        };

        try
        {
            var fileId = await AddSampleFileAsync(repo);
            failures.ShouldBeEmpty();

            await using var txn = await repo.BeginTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);

            // A directory in place of the file's delete marker makes the commit unable to write it. The commit does not throw; it reports the failure.
            var blocker = repoDir
                .CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                .CombineDirectory(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);

            blocker.Create();

            try
            {
                await txn.CommitAsync(TestContext.CancellationToken);
            }
            finally
            {
                blocker.Delete();
            }

            var reported = failures.ShouldHaveSingleItem();
            reported.Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
            reported.Exception.ShouldNotBeNull();
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task TransactionCompletionFailed_AHandlerThatThrows_MakesTheCommitThrow_OnceItHasCommittedWhatItCould()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var handlerException = new InvalidOperationException("Thrown by the handler.");

        repo.TransactionCompletionFailed += _ => Task.FromException(handlerException);

        try
        {
            var blocked = await AddSampleFileAsync(repo);
            FileId added;

            await using (var txn = await repo.BeginTransactionAsync())
            {
                added = await AddSampleFileAsync(txn);
                await txn.DeleteAsync(blocked, TestContext.CancellationToken);

                // A directory in place of the file's delete marker makes the commit unable to record the deletion, as in the test above.
                var blocker = repoDir
                    .CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None)
                    .CombineDirectory(blocked + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);

                blocker.Create();

                try
                {
                    var thrown = await Should.ThrowAsync<InvalidOperationException>(() => txn.CommitAsync(TestContext.CancellationToken));
                    thrown.ShouldBeSameAs(handlerException);
                }
                finally
                {
                    blocker.Delete();
                }
            }

            // The exception does not mean that the transaction was not committed. The transaction has been disposed since, and nothing was rolled back:
            // the add is committed, and the file whose deletion failed is still readable.
            (await repo.GetAsync(added)).FileId.ShouldBe(added);
            (await repo.GetAsync(blocked)).FileId.ShouldBe(blocked);

            // That file, and only that one, is left for a cleanup pass to resolve through the callback.
            var asked = new List<FileId>();

            await repo.CleanAsync(
                TimeSpan.Zero,
                id =>
                {
                    asked.Add(id);
                    return IndeterminateResolution.Delete;
                },
                TestContext.CancellationToken);

            asked.ShouldBe([blocked]);
            (await repo.GetAsync(added)).FileId.ShouldBe(added);
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetAsync(blocked));
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    [TestMethod]
    public async Task CorruptionDetected_IsRaisedThroughTheInterface()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir, DeleteMode.DeferredUntilClean);
        var detected = new List<RepoCorruptionInfo>();

        repo.CorruptionDetected += info =>
        {
            lock (detected)
                detected.Add(info);

            return Task.CompletedTask;
        };

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            // 'b' is an alias of 'a'; losing the data file of 'a' from storage leaves 'b' dangling.
            var pipeline = VariantPipelines.RealData().WithVariant("b", VariantPipelines.Alias());
            await repo.AddVariantAsync(fileId, "a", pipeline, TestContext.CancellationToken);

            repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory)
                .GetChildFiles("a.*").ShouldHaveSingleItem().Delete();

            detected.ShouldBeEmpty();

            var group = await repo.GetGroupAsync(fileId);
            group.VariantFiles.ShouldBeEmpty();
            group.DanglingAliases.ShouldHaveSingleItem().VariantId.ShouldBe("b");

            var info = detected.ShouldHaveSingleItem();
            info.Kind.ShouldBe(RepoCorruptionKind.DanglingAlias);
            info.FileId.ShouldBe(fileId);
            info.VariantId.ShouldBe("b");

            await Should.ThrowAsync<DanglingAliasException>(async () => await repo.GetVariantAsync(fileId, "b"));
            detected.Count.ShouldBe(2);
        }
        finally
        {
            DisposeRepo(repo);
        }
    }

    private async Task<FileId> AddSampleFileAsync(IFileRepo repo)
    {
        await using var txn = await repo.BeginTransactionAsync();
        var fileId = await AddSampleFileAsync(txn);
        await txn.CommitAsync(TestContext.CancellationToken);
        return fileId;
    }

    private async Task<FileId> AddSampleFileAsync(IFileRepoTransaction txn)
    {
        await using var source = File.OpenRead(_sampleFile.PathExport);
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        return added.FileId;
    }

    private async Task<byte[]> ReadAllAsync(IRepoFile file)
    {
        await using var stream = await file.OpenReadAsync(FileAccessPattern.Sequential, TestContext.CancellationToken);
        return await ReadAllAsync(stream);
    }

    private async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.CancellationToken);
        return buffer.ToArray();
    }

    // Checks that a file an add returned knows its size: asking completes before it is awaited, and the caller has removed the file from storage first.
    private async Task ShouldKnowTheSizeAsync(IRepoFile file, long size)
    {
        var length = file.GetLengthAsync(TestContext.CancellationToken);
        length.IsCompletedSuccessfully.ShouldBeTrue();
        (await length).ShouldBe(size);
    }

    // Removes a file group from storage behind the repository's back, so that a size known afterwards cannot have been read from storage.
    private static void RemoveFromStorage(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory).Delete(recursive: true);

    // Checks the guarantees of a stream opened over a stored file, reading it only asynchronously, as the contract requires of callers.
    private async Task ShouldBeSeekableAsync(Stream stream, byte[] content)
    {
        stream.CanRead.ShouldBeTrue();
        stream.CanSeek.ShouldBeTrue();
        stream.CanWrite.ShouldBeFalse();
        stream.Position.ShouldBe(0);
        stream.Length.ShouldBe(content.Length);

        int middle = content.Length / 2;
        stream.Seek(middle, SeekOrigin.Begin).ShouldBe(middle);
        (await ReadAllAsync(stream)).ShouldBe(content[middle..]);

        stream.Position = 0;
        byte[] start = new byte[middle];
        await stream.ReadExactlyAsync(start, TestContext.CancellationToken);
        start.ShouldBe(content[..middle]);

        stream.Seek(-100, SeekOrigin.End).ShouldBe(content.Length - 100);
        (await ReadAllAsync(stream)).ShouldBe(content[^100..]);
    }

    // The only places that know the implementation: creating the repository and disposing it, which applications do in their composition root. The format
    // version decides how the local implementation records the deletions of staged transactions, which must not change their behavior.

    private static IFileRepo CreateRepo(
        IAbsoluteDirectoryPath repoDir,
        DeleteMode deleteMode,
        FileIdMode fileIdMode = FileIdMode.Secure,
        RepoFormatVersion formatVersion = RepoFormatVersion.Latest)
    {
        var repo = new FileRepo(repoDir, formatVersion, o => {
            o.DeleteMode = deleteMode;
            o.FileIdMode = fileIdMode;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });

        repo.EnsureCreated();
        return repo;
    }

    private static void DisposeRepo(IFileRepo repo) => ((IDisposable)repo).Dispose();

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_IFileRepoConformance_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
