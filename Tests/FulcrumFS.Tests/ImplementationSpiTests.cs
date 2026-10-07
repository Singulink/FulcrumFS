using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using FulcrumFS.Utilities;

namespace FulcrumFS;

/// <summary>
/// Tests for the members of <c>Singulink.FulcrumFS</c> that exist for <see cref="IFileRepo"/> implementations: they are public, hidden from editors, validate
/// their arguments and keep the contract their documentation states.
/// </summary>
[PrefixTestClass]
public sealed class ImplementationSpiTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // --- Visibility ---

    [TestMethod]
    public void SharedPackage_GrantsInternalsOnlyToItsTests()
    {
        // The local implementation must build against the public surface alone, like any other implementation.
        var friends = typeof(IFileRepo).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName);
        friends.ShouldBe(["FulcrumFS.Tests"]);
    }

    [TestMethod]
    public void SpiMembers_ArePublicAndHiddenFromEditors()
    {
        // The default binding flags of these lookups only find public members, so a member that is not public is null here.
        MemberInfo?[] members =
        [
            typeof(FileProcessingContext).GetConstructor(
                [typeof(FileId), typeof(string), typeof(IAbsoluteDirectoryPath), typeof(IAbsoluteFilePath), typeof(CancellationToken)]),
            typeof(FileProcessingContext).GetConstructor(
                [typeof(FileId), typeof(string), typeof(IAbsoluteDirectoryPath), typeof(Stream), typeof(string), typeof(bool), typeof(CancellationToken)]),
            typeof(FileProcessingContext).GetMethod(nameof(FileProcessingContext.ReleaseSourceAsync)),
            typeof(FileProcessingPipeline).GetMethod(nameof(FileProcessingPipeline.ExecuteAsync)),
            typeof(FileProcessingPipeline).GetProperty(nameof(FileProcessingPipeline.AllVariants)),
            typeof(TaskWithProgress<int>).GetNestedType("TaskFactory"),
            typeof(TaskWithProgress<int>).GetConstructor([typeof(CancellationToken), typeof(TaskWithProgress<int>.TaskFactory)]),
            typeof(TaskWithProgress<int>).GetMethod(nameof(TaskWithProgress<int>.Upcast)),
            typeof(RepoFileEntry).GetConstructor([typeof(FileId), typeof(DateTime), typeof(bool), typeof(bool)]),
            typeof(DanglingAliasInfo).GetConstructor([typeof(string), typeof(string), typeof(string)]),
            typeof(RepoCorruptionInfo).GetConstructor([typeof(RepoCorruptionKind), typeof(FileId), typeof(string), typeof(string), typeof(Exception)]),
            typeof(RepoTransactionCompletionFailureInfo).GetConstructor([typeof(RepoTransactionCompletionOperation), typeof(Exception)]),
            typeof(IOExtensions).GetMethod(nameof(IOExtensions.CopyToAsync), [typeof(IAbsoluteFilePath), typeof(IAbsoluteFilePath), typeof(CancellationToken)]),
            typeof(IOExtensions).GetMethod(
                nameof(IOExtensions.CopyToAsync), [typeof(IAbsoluteFilePath), typeof(IAbsoluteFilePath), typeof(bool), typeof(CancellationToken)]),
            typeof(IOExtensions).GetMethod(nameof(IOExtensions.TryDelete), [typeof(IAbsoluteFilePath), typeof(Exception).MakeByRefType()]),
            typeof(IOExtensions).GetMethod(nameof(IOExtensions.TryDelete), [typeof(IAbsoluteDirectoryPath), typeof(bool), typeof(Exception).MakeByRefType()]),
        ];

        for (int i = 0; i < members.Length; i++)
        {
            var member = members[i].ShouldNotBeNull($"SPI member {i} is not public.");
            member.GetCustomAttribute<EditorBrowsableAttribute>().ShouldNotBeNull($"{member} has no EditorBrowsable attribute.")
                .State.ShouldBe(EditorBrowsableState.Never);
        }

        // IOExtensions has nothing but SPI members, so it is hidden as a whole.
        typeof(IOExtensions).IsPublic.ShouldBeTrue();
        typeof(IOExtensions).GetCustomAttribute<EditorBrowsableAttribute>().ShouldNotBeNull().State.ShouldBe(EditorBrowsableState.Never);

        // The other types that hold SPI members stay first-class API; only the members are hidden.
        Type[] types =
        [
            typeof(FileProcessingContext), typeof(FileProcessingPipeline), typeof(TaskWithProgress<>), typeof(RepoFileEntry), typeof(DanglingAliasInfo),
            typeof(RepoCorruptionInfo), typeof(RepoTransactionCompletionFailureInfo),
        ];

        foreach (var type in types)
            type.GetCustomAttribute<EditorBrowsableAttribute>().ShouldBeNull($"{type} is hidden from editors.");
    }

    // --- Argument validation ---

    [TestMethod]
    public async Task FileProcessingContext_VariantIdMustBeNullOrNormalized()
    {
        var dir = NewTestDir();
        var sourceFile = dir.CombineFile("source.bin", PathOptions.None);
        var fileId = FileId.CreateSecure();

        foreach (string invalid in (string[])["Thumb", string.Empty, "a.b", "$main"])
        {
            Should.Throw<ArgumentException>(() => new FileProcessingContext(fileId, invalid, dir.CombineDirectory("work"), sourceFile, default))
                .ParamName.ShouldBe("variantId");

            Should.Throw<ArgumentException>(() => new FileProcessingContext(fileId, invalid, dir.CombineDirectory("work"), new MemoryStream(), ".bin", false, default))
                .ParamName.ShouldBe("variantId");
        }

        await using (var main = new FileProcessingContext(fileId, null, dir.CombineDirectory("work1"), sourceFile, default))
            main.VariantId.ShouldBeNull();

        await using (var variant = new FileProcessingContext(fileId, "thumb", dir.CombineDirectory("work2"), new MemoryStream(), ".BIN", false, default))
        {
            variant.VariantId.ShouldBe("thumb");
            variant.Extension.ShouldBe(".bin");
        }
    }

    [TestMethod]
    public void RepoFileEntry_CreationTimeMustBeUtc()
    {
        var fileId = FileId.CreateSecure();

        Should.Throw<ArgumentException>(() => new RepoFileEntry(fileId, DateTime.Now, false, false)).ParamName.ShouldBe("creationTimeUtc");
        Should.Throw<ArgumentException>(() => new RepoFileEntry(fileId, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), false, false))
            .ParamName.ShouldBe("creationTimeUtc");

        var creationTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var entry = new RepoFileEntry(fileId, creationTime, isPendingDeletion: true, isIndeterminate: false);

        entry.FileId.ShouldBe(fileId);
        entry.CreationTimeUtc.ShouldBe(creationTime);
        entry.IsPendingDeletion.ShouldBeTrue();
        entry.IsIndeterminate.ShouldBeFalse();
    }

    [TestMethod]
    public void DanglingAliasInfo_IdsAndExtensionMustBeNormalized()
    {
        foreach (string invalid in (string[])["Thumb", string.Empty, "a.b", "$main"])
        {
            Should.Throw<ArgumentException>(() => new DanglingAliasInfo(invalid, null, ".jpg")).ParamName.ShouldBe("variantId");
            Should.Throw<ArgumentException>(() => new DanglingAliasInfo("thumb", invalid, ".jpg")).ParamName.ShouldBe("sourceVariantId");
        }

        foreach (string invalid in (string[])["jpg", ".JPG"])
            Should.Throw<ArgumentException>(() => new DanglingAliasInfo("thumb", null, invalid)).ParamName.ShouldBe("sourceExtension");

        var fromMain = new DanglingAliasInfo("thumb", null, string.Empty);
        fromMain.VariantId.ShouldBe("thumb");
        fromMain.SourceVariantId.ShouldBeNull();
        fromMain.SourceExtension.ShouldBe(string.Empty);

        var fromVariant = new DanglingAliasInfo("small", "large", ".jpg");
        fromVariant.SourceVariantId.ShouldBe("large");
        fromVariant.SourceExtension.ShouldBe(".jpg");
    }

    [TestMethod]
    public void EventPayloads_UndefinedEnumValue_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RepoCorruptionInfo((RepoCorruptionKind)999, FileId.CreateSecure(), null, "message"));
        Should.Throw<ArgumentOutOfRangeException>(() => new RepoTransactionCompletionFailureInfo((RepoTransactionCompletionOperation)999, new IOException()));

        var exception = new IOException();
        var failure = new RepoTransactionCompletionFailureInfo(RepoTransactionCompletionOperation.Rollback, exception);
        failure.Operation.ShouldBe(RepoTransactionCompletionOperation.Rollback);
        failure.Exception.ShouldBeSameAs(exception);

        var corruption = new RepoCorruptionInfo(RepoCorruptionKind.MalformedAlias, FileId.CreateSecure(), "Not Normalized", "message", exception);
        corruption.Kind.ShouldBe(RepoCorruptionKind.MalformedAlias);
        corruption.VariantId.ShouldBe("Not Normalized");
        corruption.Exception.ShouldBeSameAs(exception);
    }

    // --- Contract ---

    [TestMethod]
    public async Task ExecuteAsync_LeavesResultAsContextSource_DisposeDeletesWorkingDirectory()
    {
        var workDir = NewTestDir().CombineDirectory("work");
        byte[] content = [9, 8, 7];
        var pipeline = new FileProcessingPipeline(new RewriteProcessor(content));

        var context = new FileProcessingContext(FileId.CreateSecure(), null, workDir, new MemoryStream([1, 2, 3]), ".bin", false, TestContext.CancellationToken);
        IAbsoluteFilePath result;

        await using (context)
        {
            await pipeline.ExecuteAsync(context, progressCallback: null, knownRepoFileSource: false, isMainPipeline: true);

            context.HasChanges.ShouldBeTrue();
            result = await context.GetSourceAsFileAsync();
            result.ParentDirectory.ShouldBe(workDir);
            (await File.ReadAllBytesAsync(result.PathExport, TestContext.CancellationToken)).ShouldBe(content);
        }

        result.State.ShouldBe(EntryState.ParentDoesNotExist);
    }

    [TestMethod]
    public async Task ExecuteAsync_UnchangedSource_ThrowsOnlyWhenThePropertyForThePipelineKindIsSet()
    {
        var dir = NewTestDir();
        var throwForMain = new FileProcessingPipeline(new NoChangeCountingProcessor()) { ThrowWhenMainSourceUnchanged = true };
        var aliasForVariant = new FileProcessingPipeline(new NoChangeCountingProcessor()) { AliasWhenVariantSourceUnchanged = true };

        await RunAsync(throwForMain, isMainPipeline: true).ShouldThrowAsync<FileSourceUnchangedException>();
        await RunAsync(throwForMain, isMainPipeline: false);

        await RunAsync(aliasForVariant, isMainPipeline: false).ShouldThrowAsync<FileSourceUnchangedException>();
        await RunAsync(aliasForVariant, isMainPipeline: true);

        async Task RunAsync(FileProcessingPipeline pipeline, bool isMainPipeline)
        {
            var workDir = dir.CombineDirectory("work" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));
            string? variantId = isMainPipeline ? null : "variant";
            var context = new FileProcessingContext(FileId.CreateSecure(), variantId, workDir, new MemoryStream([1, 2, 3]), ".bin", false, default);

            await using (context)
                await pipeline.ExecuteAsync(context, progressCallback: null, knownRepoFileSource: false, isMainPipeline);
        }
    }

    [TestMethod]
    public async Task ReleaseSourceAsync_DisposesOwnedSourceAndKeepsWorkingDirectoryUntilDispose()
    {
        var dir = NewTestDir();

        foreach (bool leaveOpen in (bool[])[false, true])
        {
            var workDir = dir.CombineDirectory(leaveOpen ? "leave-open" : "owned");
            var source = new MemoryStream([1, 2, 3]);
            var context = new FileProcessingContext(FileId.CreateSecure(), null, workDir, source, ".bin", leaveOpen, TestContext.CancellationToken);

            var workFile = context.GetNewWorkFile(".bin");
            workFile.ParentDirectory.Create();
            await File.WriteAllBytesAsync(workFile.PathExport, [4, 5, 6], TestContext.CancellationToken);

            await context.ReleaseSourceAsync();

            source.CanRead.ShouldBe(leaveOpen);
            workFile.State.ShouldBe(EntryState.Exists);
            Should.Throw<ObjectDisposedException>(() => context.GetNewWorkFile(".bin"));

            await context.DisposeAsync();
            workDir.State.ShouldBe(EntryState.ParentExists);
        }
    }

    [TestMethod]
    public async Task IOExtensions_CopyToAsync_CreatesNewFileUnlessOverwriting()
    {
        var dir = NewTestDir();
        var source = dir.CombineFile("source.bin", PathOptions.None);
        var destination = dir.CombineFile("destination.bin", PathOptions.None);
        await File.WriteAllBytesAsync(source.PathExport, [1, 2, 3], TestContext.CancellationToken);

        // The overload without an overwrite argument is unambiguous and never replaces an existing file.
        await source.CopyToAsync(destination);
        (await File.ReadAllBytesAsync(destination.PathExport, TestContext.CancellationToken)).ShouldBe([1, 2, 3]);

        await File.WriteAllBytesAsync(source.PathExport, [4, 5], TestContext.CancellationToken);
        await Should.ThrowAsync<IOException>(() => source.CopyToAsync(destination, TestContext.CancellationToken));
        await Should.ThrowAsync<IOException>(() => source.CopyToAsync(destination, overwrite: false, TestContext.CancellationToken));

        await source.CopyToAsync(destination, overwrite: true, TestContext.CancellationToken);
        (await File.ReadAllBytesAsync(destination.PathExport, TestContext.CancellationToken)).ShouldBe([4, 5]);
    }

    [TestMethod]
    public async Task IOExtensions_TryDelete_ReturnsIOExceptionInsteadOfThrowing()
    {
        var dir = NewTestDir();
        var subDir = dir.CombineDirectory("sub");
        subDir.Create();
        var file = subDir.CombineFile("file.bin", PathOptions.None);
        await File.WriteAllBytesAsync(file.PathExport, [1], TestContext.CancellationToken);

        subDir.TryDelete(recursive: false, out var exception).ShouldBeFalse();
        exception.ShouldBeAssignableTo<IOException>();
        subDir.State.ShouldBe(EntryState.Exists);

        file.TryDelete(out exception).ShouldBeTrue();
        exception.ShouldBeNull();
        file.State.ShouldBe(EntryState.ParentExists);

        await File.WriteAllBytesAsync(file.PathExport, [1], TestContext.CancellationToken);
        subDir.TryDelete(recursive: true, out exception).ShouldBeTrue();
        exception.ShouldBeNull();
        subDir.State.ShouldBe(EntryState.ParentExists);
    }

    private static IAbsoluteDirectoryPath NewTestDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_ImplementationSpi_" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }

    [TestMethod]
    public async Task IsTempWorkingFile_IsTrueForWorkFiles_AndFalseOutsideTheWorkingDirectory()
    {
        var workingDir = DirectoryPath.GetTemp().CombineDirectory("fulcrumfs-spi-" + Guid.NewGuid().ToString("N"), PathOptions.None);
        var sibling = DirectoryPath.GetTemp().CombineDirectory(workingDir.Name + "2", PathOptions.None);

        await using var source = new MemoryStream([1, 2, 3]);
        var context = new FileProcessingContext(FileId.CreateSecure(), null, workingDir, source, ".bin", leaveOpen: true, TestContext.CancellationToken);

        await using (context)
        {
            var workFile = context.GetNewWorkFile(".bin");
            context.IsTempWorkingFile(workFile).ShouldBeTrue();
            context.IsTempWorkingFile(workingDir.CombineDirectory("nested", PathOptions.None).CombineFile("x.bin", PathOptions.None)).ShouldBeTrue();
            context.IsTempWorkingFile(sibling.CombineFile("x.bin", PathOptions.None)).ShouldBeFalse();
            context.IsTempWorkingFile(DirectoryPath.GetTemp().CombineFile("x.bin", PathOptions.None)).ShouldBeFalse();
        }
    }
}
