using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FulcrumFS;

/// <summary>
/// Checks that the local types implement the repository interfaces and that the explicit implementations hand out the same objects as the public members they
/// stand in for. The assignments from the local types to the interfaces are the compile-time half of the check.
/// </summary>
[PrefixTestClass]
public sealed class FileRepoInterfaceImplementationTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteFilePath _sampleFile = _appDir.CombineDirectory("SampleFiles").CombineFile("sample.jpg");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task Group_ExplicitMembers_ReturnTheSameObjectsAsTheProperties()
    {
        using var repo = CreateRepo();
        var fileId = await AddSampleFileAsync(repo);
        await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);

        RepoFileGroupInfo group = await repo.GetGroupAsync(fileId);
        IRepoFileGroup groupView = group;

        groupView.FileId.ShouldBe(group.FileId);
        groupView.MainFile.ShouldBeSameAs(group.MainFile);
        groupView.VariantFiles.ShouldBeSameAs(group.VariantFiles);
        groupView.DanglingAliases.ShouldBeSameAs(group.DanglingAliases);

        RepoFileInfo main = group.MainFile;
        IRepoFile mainView = main;

        mainView.FileId.ShouldBe(main.FileId);
        mainView.VariantId.ShouldBe(main.VariantId);
        mainView.Extension.ShouldBe(main.Extension);
    }

    [TestMethod]
    public async Task Fetches_ThroughTheInterface_ReturnTheLocalTypes()
    {
        using var repo = CreateRepo();
        IFileRepo repoView = repo;

        var fileId = await AddSampleFileAsync(repo);
        await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);

        var main = await repo.GetAsync(fileId);
        var thumb = await repo.GetVariantAsync(fileId, "thumb");

        ShouldDescribeTheSameFile((await repoView.GetAsync(fileId)).ShouldBeOfType<RepoFileInfo>(), main);
        ShouldDescribeTheSameFile((await repoView.GetVariantAsync(fileId, "thumb")).ShouldBeOfType<RepoFileInfo>(), thumb);

        var group = (await repoView.GetGroupAsync(fileId)).ShouldBeOfType<RepoFileGroupInfo>();
        ShouldDescribeTheSameFile(group.MainFile, main);
        ShouldDescribeTheSameFile(group.VariantFiles.ShouldHaveSingleItem(), thumb);
        group.DanglingAliases.ShouldBeEmpty();

        await using (var stream = await repoView.OpenAsync(fileId, FileAccessPattern.Sequential))
            stream.ShouldBeOfType<FileStream>().Name.ShouldBe(main.Path.PathExport);

        await using (var stream = await repoView.OpenVariantAsync(fileId, "thumb", FileAccessPattern.Sequential))
            stream.ShouldBeOfType<FileStream>().Name.ShouldBe(thumb.Path.PathExport);

        var added = await repoView.AddVariantAsync(fileId, "small", VariantPipelines.RealData([4]), TestContext.CancellationToken);
        var addedSmall = added.ShouldBeAssignableTo<IReadOnlyList<RepoFileInfo>>().ShouldHaveSingleItem();
        ShouldDescribeTheSameFile(addedSmall, await repo.GetVariantAsync(fileId, "small"));
    }

    [TestMethod]
    public async Task OpenReadAsync_ReturnsTheStreamOfOpen_AndReportsFailuresThroughTheTask()
    {
        using var repo = CreateRepo();
        var fileId = await AddSampleFileAsync(repo);

        RepoFileInfo info = await repo.GetAsync(fileId);
        IRepoFile file = info;

        await using (var stream = await file.OpenReadAsync(FileAccessPattern.Sequential, TestContext.CancellationToken))
        {
            var fileStream = stream.ShouldBeOfType<FileStream>();
            fileStream.Name.ShouldBe(info.Path.PathExport);
            fileStream.CanSeek.ShouldBeTrue();
            fileStream.CanWrite.ShouldBeFalse();

            // Opened with the sharing of Open, the file can be deleted while the stream is open.
            info.Path.Delete();
        }

        // Opening never throws from the call itself: a cancellation or a failure to open completes the returned task instead.
        using (var cts = new CancellationTokenSource())
        {
            await cts.CancelAsync();
            file.OpenReadAsync(FileAccessPattern.Sequential, cts.Token).IsCanceled.ShouldBeTrue();
        }

        var failed = file.OpenReadAsync(FileAccessPattern.Sequential, TestContext.CancellationToken);
        failed.IsFaulted.ShouldBeTrue();
        failed.Exception!.InnerException.ShouldBeAssignableTo<IOException>();
    }

    [TestMethod]
    [DataRow(FileAccessPattern.Sequential)]
    [DataRow(FileAccessPattern.Random)]
    public async Task Open_EveryWay_OpensTheFileWithTheOptionsOfTheAccessPattern(FileAccessPattern accessPattern)
    {
        using var repo = CreateRepo();
        IFileRepo repoView = repo;

        var fileId = await AddSampleFileAsync(repo);
        await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.RealData([1, 2, 3]), TestContext.CancellationToken);

        byte[] sample = File.ReadAllBytes(_sampleFile.PathExport);
        var main = await repo.GetAsync(fileId);
        var thumb = await repo.GetVariantAsync(fileId, "thumb");

        await using (var stream = main.Open(accessPattern))
            await ShouldBeOpenedForAsync(stream, accessPattern, sample);

        await using (var stream = await ((IRepoFile)main).OpenReadAsync(accessPattern, TestContext.CancellationToken))
            await ShouldBeOpenedForAsync(stream, accessPattern, sample);

        await using (var stream = await repo.OpenAsync(fileId, accessPattern))
            await ShouldBeOpenedForAsync(stream, accessPattern, sample);

        await using (var stream = await repoView.OpenAsync(fileId, accessPattern))
            await ShouldBeOpenedForAsync(stream, accessPattern, sample);

        await using (var stream = await repo.OpenVariantAsync(fileId, "thumb", accessPattern))
            await ShouldBeOpenedForAsync(stream, accessPattern, [1, 2, 3]);

        await using (var stream = await repoView.OpenVariantAsync(fileId, "thumb", accessPattern))
            await ShouldBeOpenedForAsync(stream, accessPattern, [1, 2, 3]);

        await using (var stream = await ((IRepoFile)thumb).OpenReadAsync(accessPattern, TestContext.CancellationToken))
            await ShouldBeOpenedForAsync(stream, accessPattern, [1, 2, 3]);
    }

    [TestMethod]
    public async Task Length_IsReadOnce_SoItIsStillKnownAfterTheFileIsRemoved_WhileAFreshInstanceThrows()
    {
        using var repo = CreateRepo();
        var fileId = await AddSampleFileAsync(repo);

        // Two fetches of the same file: one reads the size before the file is removed, the other only after.
        var read = await repo.GetAsync(fileId);
        var fresh = await repo.GetAsync(fileId);

        read.Length.ShouldBe(_sampleFile.Length);

        await using (var stream = await ((IRepoFile)read).OpenReadAsync(FileAccessPattern.Random, TestContext.CancellationToken))
            stream.Length.ShouldBe(read.Length);

        read.Path.Delete();

        // The size is kept once read; Path is the live view of the file on disk.
        read.Length.ShouldBe(_sampleFile.Length);
        Should.Throw<Exception>(() => read.Path.Length).ShouldBeAssignableTo<IOException>();
        Should.Throw<Exception>(() => fresh.Length).ShouldBeAssignableTo<IOException>();
    }

    [TestMethod]
    public async Task GetLengthAsync_ReadsTheSizeOnce_SoItIsStillKnownAfterTheFileIsRemoved()
    {
        using var repo = CreateRepo();
        var fileId = await AddSampleFileAsync(repo);

        // Two fetches of the same file: one asks for its size before the file is removed, the other only after.
        IRepoFile cached = await repo.GetAsync(fileId);
        RepoFileInfo fresh = await repo.GetAsync(fileId);

        (await cached.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(_sampleFile.Length);

        fresh.Path.Delete();

        // The second call answers from the instance without touching the file system, which no longer has the file; so does the local Length.
        (await cached.GetLengthAsync(TestContext.CancellationToken)).ShouldBe(_sampleFile.Length);
        ((RepoFileInfo)cached).Length.ShouldBe(_sampleFile.Length);

        // The instance that never read the size has to read it now and fails. Like opening, the failure completes the returned task instead of being thrown.
        var failed = ((IRepoFile)fresh).GetLengthAsync(TestContext.CancellationToken).AsTask();
        failed.IsFaulted.ShouldBeTrue();
        failed.Exception!.InnerException.ShouldBeAssignableTo<IOException>();
        Should.Throw<Exception>(() => fresh.Length).ShouldBeAssignableTo<IOException>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        cached.GetLengthAsync(cts.Token).AsTask().IsCanceled.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Length_OfTheFilesATransactionalAddReturns_IsKnownWithoutTheFileSystem()
    {
        using var repo = CreateRepo();

        // A real variant with an alias of its own ('small'), and an alias of the main file ('same'). Each alias reports the data file it resolves to.
        var pipeline = FileProcessingPipeline.Empty
            .WithVariant("thumb", VariantPipelines.RealData([1, 2, 3]).WithVariant("small", VariantPipelines.Alias()))
            .WithVariant("same", VariantPipelines.Alias());

        RepoFileGroupInfo added;

        await using (var source = File.OpenRead(_sampleFile.PathExport))
        {
            await using var txn = await repo.BeginTransactionAsync();
            added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var main = added.MainFile;
        var (thumb, small, same) = (added.VariantFiles[0], added.VariantFiles[1], added.VariantFiles[2]);

        added.VariantFiles.Select(v => v.VariantId).ShouldBe(["thumb", "small", "same"]);
        small.Path.ShouldBe(thumb.Path);
        same.Path.ShouldBe(main.Path);

        main.Path.Delete();
        thumb.Path.Delete();

        // The add took the sizes from the files it stored, so the instances answer although the file system no longer has the files.
        Should.Throw<Exception>(() => main.Path.Length).ShouldBeAssignableTo<IOException>();
        Should.Throw<Exception>(() => thumb.Path.Length).ShouldBeAssignableTo<IOException>();

        main.Length.ShouldBe(_sampleFile.Length);
        thumb.Length.ShouldBe(3);
        small.Length.ShouldBe(3);
        same.Length.ShouldBe(_sampleFile.Length);
    }

    [TestMethod]
    public async Task Open_WithAnUndefinedAccessPattern_Throws()
    {
        using var repo = CreateRepo();
        var fileId = await AddSampleFileAsync(repo);
        var info = await repo.GetAsync(fileId);
        var undefined = (FileAccessPattern)2;

        Should.Throw<ArgumentOutOfRangeException>(() => info.Open(undefined)).ParamName.ShouldBe("accessPattern");
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.OpenAsync(fileId, undefined));

        var failed = ((IRepoFile)info).OpenReadAsync(undefined, TestContext.CancellationToken);
        failed.IsFaulted.ShouldBeTrue();
        failed.Exception!.InnerException.ShouldBeOfType<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task Transactions_ExplicitMembers_ReturnTheSameObjects()
    {
        using var repo = CreateRepo();
        IFileRepo repoView = repo;
        var fileId = await AddSampleFileAsync(repo);

        await using (var txn = await repo.BeginTransactionAsync())
        {
            IFileRepoTransaction txnView = txn;
            txnView.Repository.ShouldBeSameAs(txn.Repository);

            await using var source = File.OpenRead(_sampleFile.PathExport);
            var added = await txnView.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
            added.ShouldBeOfType<RepoFileGroupInfo>().MainFile.Path.State.ShouldBe(EntryState.Exists);
        }

        await using (var txn = await repo.BeginStagedTransactionAsync())
        {
            IFileRepoStagedTransaction txnView = txn;
            txnView.Repository.ShouldBeSameAs(txn.Repository);

            await txnView.DeleteAsync(fileId, TestContext.CancellationToken);
            IFileRepoStage stage = (await txnView.StageAsync(TestContext.CancellationToken)).ShouldBeOfType<FileRepoStage>();
            await stage.CommitAsync(TestContext.CancellationToken);
        }

        await using (var txn = await repoView.BeginTransactionAsync())
            txn.ShouldBeOfType<FileRepoTransaction>().Repository.ShouldBeSameAs(repo);

        await using (var txn = await repoView.BeginStagedTransactionAsync())
            txn.ShouldBeOfType<FileRepoStagedTransaction>().Repository.ShouldBeSameAs(repo);
    }

    [TestMethod]
    public async Task TransactionCompletionFailed_SubscribedThroughTheInterface_ReceivesWhatTheRepositoryRaises()
    {
        using var repo = CreateRepo();
        IFileRepo repoView = repo;
        var failures = new List<RepoTransactionCompletionFailureInfo>();

        RepoTransactionCompletionFailedHandler handler = failure =>
        {
            lock (failures)
                failures.Add(failure);

            return Task.CompletedTask;
        };

        repoView.TransactionCompletionFailed += handler;

        // Commit and rollback report their failures through this method; IFileRepoConformanceTests provokes a real one. Raising it directly here also shows that
        // removing the handler through the interface takes effect.
        var exception = new IOException("Commit failed.");
        await repo.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Commit, exception);

        var reported = failures.ShouldHaveSingleItem();
        reported.Operation.ShouldBe(RepoTransactionCompletionOperation.Commit);
        reported.Exception.ShouldBeSameAs(exception);

        repoView.TransactionCompletionFailed -= handler;
        await repo.OnTxnCompletionErrorAsync(RepoTransactionCompletionOperation.Rollback, exception);
        failures.ShouldHaveSingleItem();
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleFile.PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
    }

    // The info types have reference equality, so a fetch through the interface is checked against the public fetch by what both instances describe.
    private static void ShouldDescribeTheSameFile(RepoFileInfo actual, RepoFileInfo expected)
    {
        actual.FileId.ShouldBe(expected.FileId);
        actual.VariantId.ShouldBe(expected.VariantId);
        actual.Path.ShouldBe(expected.Path);
    }

    private static FileRepo CreateRepo()
    {
        var repo = new FileRepo(NewRepoDir(), RepoFormatVersion.Latest, o => o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60));
        repo.EnsureCreated();
        return repo;
    }

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory(
            "RepoRoot_FileRepoInterfaceImplementation_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }

    // Only Windows makes the option observable, and only one of the two: it reports FileOptions.SequentialScan in the mode of the handle but not
    // FileOptions.RandomAccess, so a stream opened for random access is recognized by the missing sequential flag. Linux and macOS turn both options into
    // posix_fadvise advice, which cannot be read back, so there the check is that the stream is asynchronous, seekable and reads the content.
    private async Task ShouldBeOpenedForAsync(Stream stream, FileAccessPattern accessPattern, byte[] content)
    {
        var fileStream = stream.ShouldBeOfType<FileStream>();
        fileStream.IsAsync.ShouldBeTrue();
        fileStream.CanSeek.ShouldBeTrue();

        if (OperatingSystem.IsWindows())
            WindowsFileMode.IsSequentialOnly(fileStream.SafeFileHandle).ShouldBe(accessPattern is FileAccessPattern.Sequential);

        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, TestContext.CancellationToken);
        buffer.ToArray().ShouldBe(content);
    }

    private static class WindowsFileMode
    {
        // The FileModeInformation class of NtQueryInformationFile, and the FILE_SEQUENTIAL_ONLY flag that FileOptions.SequentialScan sets in the mode.
        private const int FileModeInformation = 16;
        private const uint FileSequentialOnly = 0x4;

        public static bool IsSequentialOnly(SafeFileHandle handle)
        {
            NtQueryInformationFile(handle, out _, out uint mode, sizeof(uint), FileModeInformation).ShouldBe(0);
            return (mode & FileSequentialOnly) is not 0;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            out uint fileInformation,
            uint length,
            int fileInformationClass);

        // IO_STATUS_BLOCK holds two pointer-sized values, which the check does not need.
        [StructLayout(LayoutKind.Sequential, Size = 16)]
        private struct IoStatusBlock;
    }
}
