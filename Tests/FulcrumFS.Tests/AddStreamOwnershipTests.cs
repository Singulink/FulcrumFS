using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace FulcrumFS;

/// <summary>
/// Pins what a transaction's add does with its source stream: with <c>leaveOpen: false</c> it closes the stream whether it succeeds or fails, however early
/// it fails, and closes it once; with <c>leaveOpen: true</c> it never closes it.
/// </summary>
[PrefixTestClass]
public sealed class AddStreamOwnershipTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();

    private static int _counter;

    public required TestContext TestContext { get; set; }

    public enum TransactionKind
    {
        Ordinary,
        Staged,
    }

    /// <summary>
    /// What makes an add fail before the processing context of its main file takes the stream over.
    /// </summary>
    public enum EarlyFailure
    {
        /// <summary>The transaction has been disposed.</summary>
        TransactionDisposed,

        /// <summary>The staged transaction has been staged.</summary>
        TransactionStaged,

        /// <summary>The token is cancelled before the add is awaited.</summary>
        TokenCancelled,

        /// <summary>The token is cancelled while the add waits for another add of the transaction to finish.</summary>
        CancelledWhileWaitingForTheTransaction,

        /// <summary>The add takes the extension from the name of a file stream that has no path.</summary>
        FileStreamWithoutAPath,

        /// <summary>The extension is not a valid extension.</summary>
        InvalidExtension,

        /// <summary>The repository has been disposed, so the add fails when it makes sure that the repository is initialized.</summary>
        RepositoryDisposed,

        /// <summary>The repository's cleanup directory is gone, which the add finds when it chooses the file ID.</summary>
        CleanupDirectoryMissing,

        /// <summary>The pipeline selector throws.</summary>
        SelectorThrows,

        /// <summary>The pipeline declares the same variant ID twice.</summary>
        DuplicateVariantIds,
    }

    [TestMethod]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.TransactionDisposed)]
    [DataRow(TransactionKind.Staged, EarlyFailure.TransactionDisposed)]
    [DataRow(TransactionKind.Staged, EarlyFailure.TransactionStaged)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.TokenCancelled)]
    [DataRow(TransactionKind.Staged, EarlyFailure.TokenCancelled)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.CancelledWhileWaitingForTheTransaction)]
    [DataRow(TransactionKind.Staged, EarlyFailure.CancelledWhileWaitingForTheTransaction)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.FileStreamWithoutAPath)]
    [DataRow(TransactionKind.Staged, EarlyFailure.FileStreamWithoutAPath)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.InvalidExtension)]
    [DataRow(TransactionKind.Staged, EarlyFailure.InvalidExtension)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.RepositoryDisposed)]
    [DataRow(TransactionKind.Staged, EarlyFailure.RepositoryDisposed)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.CleanupDirectoryMissing)]
    [DataRow(TransactionKind.Staged, EarlyFailure.CleanupDirectoryMissing)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.SelectorThrows)]
    [DataRow(TransactionKind.Staged, EarlyFailure.SelectorThrows)]
    [DataRow(TransactionKind.Ordinary, EarlyFailure.DuplicateVariantIds)]
    [DataRow(TransactionKind.Staged, EarlyFailure.DuplicateVariantIds)]
    public async Task Add_ThatFailsBeforeAContextTakesTheStreamOver_ClosesItUnlessLeftOpen(TransactionKind kind, EarlyFailure failure)
    {
        foreach (bool leaveOpen in (bool[])[false, true])
        {
            var repoDir = NewRepoDir();
            var repo = CreateRepo(repoDir);
            var txn = await BeginAsync(repo, kind);
            Stream? stream = null;
            CancellationTokenSource? cts = null;
            Action release = () => { };

            try
            {
                string extension = ".bin";
                IFileProcessingPipelineSelector pipeline = FileProcessingPipeline.Empty;
                var cancellationToken = TestContext.CancellationToken;
                Func<Task>? whileAdding = null;

                switch (failure)
                {
                    case EarlyFailure.TransactionDisposed:
                        await txn.DisposeAsync();
                        break;
                    case EarlyFailure.TransactionStaged:
                        await ((FileRepoStagedTransaction)txn).StageAsync(TestContext.CancellationToken);
                        break;
                    case EarlyFailure.TokenCancelled:
                        cancellationToken = new CancellationToken(canceled: true);
                        break;
                    case EarlyFailure.CancelledWhileWaitingForTheTransaction:
                        // Another add holds the transaction until it is released, and the add is cancelled while it waits for it.
                        var gate = new GateProcessor();
                        release = gate.Release;

                        var holder = AddAsync(txn, new MemoryStream([9]), ".bin", leaveOpen: false, gate.ToPipeline(), TestContext.CancellationToken);
                        await gate.Entered.WaitAsync(TestContext.CancellationToken);

                        cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
                        cancellationToken = cts.Token;

                        whileAdding = async () =>
                        {
                            await cts.CancelAsync();
                            release();
                            await holder;
                        };

                        break;
                    case EarlyFailure.FileStreamWithoutAPath:
                        extension = null!;
                        break;
                    case EarlyFailure.InvalidExtension:
                        extension = ".a/b";
                        break;
                    case EarlyFailure.RepositoryDisposed:
                        repo.Dispose();
                        break;
                    case EarlyFailure.CleanupDirectoryMissing:
                        repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None).Delete(recursive: true);
                        break;
                    case EarlyFailure.SelectorThrows:
                        pipeline = new ThrowingSelector();
                        break;
                    case EarlyFailure.DuplicateVariantIds:
                        pipeline = FileProcessingPipeline.Empty
                            .WithVariant("a", VariantPipelines.Alias().WithVariant("b", VariantPipelines.Alias()))
                            .WithVariant("b", VariantPipelines.Alias());
                        break;
                }

                stream = failure is EarlyFailure.FileStreamWithoutAPath ? OpenFileStreamWithoutAPath(repoDir) : new MemoryStream([1, 2, 3]);
                var add = AddAsync(txn, stream, extension, leaveOpen, pipeline, cancellationToken);

                if (whileAdding is not null)
                    await whileAdding();

                var ex = await Should.ThrowAsync<Exception>(async () => await add);
                ex.ShouldBeAssignableTo(ExpectedException(failure), $"leaveOpen: {leaveOpen}");

                stream.CanRead.ShouldBe(leaveOpen, $"leaveOpen: {leaveOpen}");
            }
            finally
            {
                release();
                stream?.Dispose();
                cts?.Dispose();

                await txn.DisposeAsync();
                repo.Dispose();
            }
        }

        static Type ExpectedException(EarlyFailure failure) => failure switch
        {
            EarlyFailure.TransactionDisposed or EarlyFailure.RepositoryDisposed => typeof(ObjectDisposedException),
            EarlyFailure.TransactionStaged or EarlyFailure.SelectorThrows => typeof(InvalidOperationException),
            EarlyFailure.TokenCancelled or EarlyFailure.CancelledWhileWaitingForTheTransaction => typeof(OperationCanceledException),
            EarlyFailure.FileStreamWithoutAPath or EarlyFailure.InvalidExtension or EarlyFailure.DuplicateVariantIds => typeof(ArgumentException),
            EarlyFailure.CleanupDirectoryMissing => typeof(IOException),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
    }

    [TestMethod]
    [DataRow(TransactionKind.Ordinary)]
    [DataRow(TransactionKind.Staged)]
    public async Task Add_ThatFailsAfterAContextTookTheStreamOver_ClosesItOnceUnlessLeftOpen(TransactionKind kind)
    {
        foreach (bool leaveOpen in (bool[])[false, true])
        {
            var repoDir = NewRepoDir();
            var repo = CreateRepo(repoDir);
            var txn = await BeginAsync(repo, kind);
            var stream = new CloseCountingStream([1, 2, 3]);

            try
            {
                var pipeline = new FailingProcessor().ToPipeline();

                await Should.ThrowAsync<FileProcessingException>(
                    async () => await AddAsync(txn, stream, ".bin", leaveOpen, pipeline, TestContext.CancellationToken));

                stream.CloseCount.ShouldBe(leaveOpen ? 0 : 1, $"leaveOpen: {leaveOpen}");
            }
            finally
            {
                await stream.DisposeAsync();
                await txn.DisposeAsync();
                repo.Dispose();
            }
        }
    }

    [TestMethod]
    [DataRow(TransactionKind.Ordinary)]
    [DataRow(TransactionKind.Staged)]
    public async Task Add_ThatSucceeds_ClosesTheStreamOnceUnlessLeftOpen(TransactionKind kind)
    {
        foreach (bool leaveOpen in (bool[])[false, true])
        {
            var repoDir = NewRepoDir();
            var repo = CreateRepo(repoDir);
            var txn = await BeginAsync(repo, kind);
            var stream = new CloseCountingStream([1, 2, 3]);

            try
            {
                var added = await AddAsync(txn, stream, ".bin", leaveOpen, FileProcessingPipeline.Empty, TestContext.CancellationToken);

                stream.CloseCount.ShouldBe(leaveOpen ? 0 : 1, $"leaveOpen: {leaveOpen}");
                (await File.ReadAllBytesAsync(added.MainFile.Path.PathExport, TestContext.CancellationToken)).ShouldBe([1, 2, 3]);
            }
            finally
            {
                await stream.DisposeAsync();
                await txn.DisposeAsync();
                repo.Dispose();
            }
        }
    }

    [TestMethod]
    [DataRow(TransactionKind.Ordinary)]
    [DataRow(TransactionKind.Staged)]
    public async Task Add_OfAFileStream_ThatSucceeds_TakesTheExtensionFromItsName_AndClosesItUnlessLeftOpen(TransactionKind kind)
    {
        foreach (bool leaveOpen in (bool[])[false, true])
        {
            var repoDir = NewRepoDir();
            var repo = CreateRepo(repoDir);
            var txn = await BeginAsync(repo, kind);

            var sourceFile = repoDir.CombineFile("source.jpg", PathOptions.None);
            await File.WriteAllBytesAsync(sourceFile.PathExport, [1, 2, 3], TestContext.CancellationToken);
            var stream = File.OpenRead(sourceFile.PathExport);

            try
            {
                var added = await AddAsync(txn, stream, extension: null, leaveOpen, FileProcessingPipeline.Empty, TestContext.CancellationToken);

                stream.CanRead.ShouldBe(leaveOpen, $"leaveOpen: {leaveOpen}");
                added.MainFile.Path.Extension.ShouldBe(".jpg");
                (await File.ReadAllBytesAsync(added.MainFile.Path.PathExport, TestContext.CancellationToken)).ShouldBe([1, 2, 3]);
            }
            finally
            {
                await stream.DisposeAsync();
                await txn.DisposeAsync();
                repo.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<IAsyncDisposable> BeginAsync(FileRepo repo, TransactionKind kind) => kind is TransactionKind.Ordinary ?
        await repo.BeginTransactionAsync() :
        await repo.BeginStagedTransactionAsync();

    /// <summary>
    /// Starts an add on either kind of transaction, through the overload that takes the extension from a file stream's name when
    /// <paramref name="extension"/> is <see langword="null"/>.
    /// </summary>
    private static Task<RepoFileGroupInfo> AddAsync(
        IAsyncDisposable txn,
        Stream stream,
        string? extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return (txn, extension) switch
        {
            (FileRepoTransaction t, null) => AwaitAsync(() => t.AddAsync((FileStream)stream, leaveOpen, pipeline, cancellationToken)),
            (FileRepoTransaction t, _) => AwaitAsync(() => t.AddAsync(stream, extension, leaveOpen, pipeline, cancellationToken)),
            (FileRepoStagedTransaction t, null) => AwaitAsync(() => t.AddAsync((FileStream)stream, leaveOpen, pipeline, cancellationToken)),
            (FileRepoStagedTransaction t, _) => AwaitAsync(() => t.AddAsync(stream, extension, leaveOpen, pipeline, cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(txn)),
        };

        // Starts the add at once, as awaiting it does, and also when the call itself throws.
        static async Task<RepoFileGroupInfo> AwaitAsync(Func<TaskWithProgress<RepoFileGroupInfo>> add) => await add();
    }

    /// <summary>
    /// Opens a file stream on a handle that does not know its path, as one on a handle that the process inherited, whose name is then not a path.
    /// </summary>
    private static FileStream OpenFileStreamWithoutAPath(IAbsoluteDirectoryPath repoDir)
    {
        var file = repoDir.CombineFile("source.bin", PathOptions.None);
        File.WriteAllBytes(file.PathExport, [1, 2, 3]);

        using var opened = File.OpenHandle(file.PathExport, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var handle = new SafeFileHandle(opened.DangerousGetHandle(), ownsHandle: true);
        opened.SetHandleAsInvalid();

        var stream = new FileStream(handle, FileAccess.Read);
        Should.Throw<ArgumentException>(() => FilePath.ParseAbsolute(stream.Name, PathOptions.None));

        return stream;
    }

    private static FileRepo CreateRepo(IAbsoluteDirectoryPath repoDir)
    {
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version2, o => o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(30));
        repo.EnsureCreated();

        return repo;
    }

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_AddStreamOwnership_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }

    /// <summary>
    /// A selector that fails to select a pipeline.
    /// </summary>
    private sealed class ThrowingSelector : IFileProcessingPipelineSelector
    {
        public FileProcessingPipeline GetPipeline(string extension) => throw new InvalidOperationException("Simulated selector failure.");
    }

    /// <summary>
    /// A processor that fails without touching its source.
    /// </summary>
    private sealed class FailingProcessor : FileProcessor
    {
        public override IReadOnlyList<string> AllowedFileExtensions => [];

        protected override Task<FileProcessingResult> ProcessAsync(FileProcessingContext context) =>
            throw new InvalidOperationException("Simulated processor failure.");
    }
}
