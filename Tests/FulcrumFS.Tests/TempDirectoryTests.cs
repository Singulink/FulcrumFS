using System.Globalization;

namespace FulcrumFS;

/// <summary>
/// Pins when a repository instance empties its temp directory: once, at its first initialization, which removes what a process that ended left behind. When
/// it initializes again, after a health check has dropped the repository lock, the directory holds the working files of its own operations, which go on
/// through the outage, so it is left as it is. Also pins the working directories that adds have in it: one of its own for each pipeline an add runs, which
/// the add removes when it finishes, whether it succeeds or fails, and one that it cannot remove stays until the next first initialization without getting
/// in the way of a later add of the same variant of the same file.
/// </summary>
[PrefixTestClass]
public sealed class TempDirectoryTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();

    // The repository's HealthCheckInterval, the shortest the options allow, and its MaxAccessWaitOrRetryTime, which no call here should come near: the call
    // that finds the volume not answering initializes the repository again on its second attempt, a second after the first, by which time the volume
    // answers again.
    private static readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _outage = TimeSpan.FromMilliseconds(250);

    private static int _counter;

    public required TestContext TestContext { get; set; }

    /// <summary>
    /// What an add that is in its pipeline has in the temp directory when the repository is initialized again.
    /// </summary>
    public enum WorkFile
    {
        /// <summary>A file that a processor has finished and closed, which the next processor reads.</summary>
        Finished,

        /// <summary>A file that a processor is still writing and has open without sharing it, as a transcode has its output for minutes.</summary>
        OpenForWriting,

        /// <summary>The copy of a source stream that the pipeline buffered, which it keeps open, sharing its deletion.</summary>
        BufferedSource,
    }

    /// <summary>
    /// What makes a variant add that stores an alias fail after its pipeline has run and before the alias is staged.
    /// </summary>
    public enum AliasStagingFailure
    {
        /// <summary>The alias marker cannot be created, while the context owns a stream that a processor opened on the source.</summary>
        MarkerNotCreated,

        /// <summary>The stream that the pipeline left as the context's source fails when it is closed.</summary>
        SourceNotReleased,
    }

    [TestMethod]
    [DataRow(WorkFile.Finished)]
    [DataRow(WorkFile.OpenForWriting)]
    [DataRow(WorkFile.BufferedSource)]
    public async Task Reinitialization_WhileAnAddIsInItsPipeline_LeavesItsWorkingFiles_AndIsNotHeldUpByThem(WorkFile workFile)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);
        var volume = new SimulatedVolume(repo, repoDir);
        FileRepoTransaction? txn = null;
        Action releaseAdd = () => { };

        try
        {
            txn = await repo.BeginTransactionAsync();

            byte[] firstPart = [1, 2, 3];
            byte[] secondPart = [4, 5];
            byte[] content = [.. firstPart, .. secondPart];

            Stream source = new MemoryStream(content);
            FileProcessingPipeline pipeline;
            Task entered;

            if (workFile is WorkFile.OpenForWriting)
            {
                var processor = new GatedRewriteProcessor(firstPart, secondPart);
                (pipeline, entered, releaseAdd) = (processor.ToPipeline(), processor.Entered, processor.Release);
            }
            else
            {
                var gate = new GateProcessor();
                (entered, releaseAdd) = (gate.Entered, gate.Release);

                if (workFile is WorkFile.Finished)
                {
                    pipeline = new FileProcessingPipeline(new RewriteProcessor(content), gate);
                }
                else
                {
                    // A source that is neither a file nor in memory is copied to a work file before the processors run.
                    source = new BufferedStream(source);
                    pipeline = gate.ToPipeline();
                }
            }

            // The add is held in its pipeline, with its working file in the temp directory.
            var add = AddAsync(txn, source, pipeline);
            await entered.WaitAsync(TestContext.CancellationToken);

            var held = TempDir(repoDir).GetChildDirectories().ShouldHaveSingleItem().GetChildFiles().ShouldHaveSingleItem();

            // The volume stops answering, and answers again a moment later. The next call finds that out from its probe, which drops the repository lock,
            // and initializes the repository again once the volume answers. It is the call of another transaction, which adds and commits a file of its own
            // while the first add is still in its pipeline.
            await volume.StopAnsweringOnceIdleAsync(answerAgainAfter: _outage);

            byte[] otherContent = [6, 7, 8, 9];
            var otherFileId = await AddAndCommitAsync(repo, otherContent);

            volume.FailedProbes.ShouldBe(1);
            volume.Initializations.ShouldBe(1);
            add.IsCompleted.ShouldBeFalse();
            held.State.ShouldBe(EntryState.Exists);

            // The add goes on, and its file is committed and readable.
            releaseAdd();
            var fileId = await add;
            await txn.CommitAsync(TestContext.CancellationToken);

            await ShouldBeStoredAsync(repo, fileId, content);
            await ShouldBeStoredAsync(repo, otherFileId, otherContent);
            CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();

            // Each add has removed its own working files.
            TempDir(repoDir).GetChildEntries().ShouldBeEmpty();
            volume.Initializations.ShouldBe(1);
        }
        finally
        {
            // The add holds its transaction until it is released, so it is released before the transaction is disposed.
            releaseAdd();
            volume.AnswerAgain();

            if (txn is not null)
                await txn.DisposeAsync();

            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task Reinitialization_CreatesTheTempDirectoryIfItIsGone()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);
        var volume = new SimulatedVolume(repo, repoDir);

        try
        {
            await volume.StopAnsweringOnceIdleAsync(answerAgainAfter: _outage);
            TempDir(repoDir).Delete();

            await AccessAsync(repo);

            volume.Initializations.ShouldBe(1);
            TempDir(repoDir).State.ShouldBe(EntryState.Exists);
        }
        finally
        {
            volume.AnswerAgain();
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task FirstInitialization_RemovesWhatAPreviousProcessLeftInTheTempDirectory(bool byOperation, bool afterAnAttemptThatFailed)
    {
        var repoDir = NewRepoDir();
        var previous = CreateRepo(repoDir);
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version2);

        try
        {
            // What a process that ends in the middle of an add leaves behind: the working directory of the add, with its files. The stray file stands for
            // anything else that is in the way.
            var workDir = TempDir(repoDir).CombineDirectory(FileId.CreateSequential().ToString(), PathOptions.None);
            var workFile = workDir.CombineFile("1.bin", PathOptions.None);
            var strayFile = TempDir(repoDir).CombineFile("stray.bin", PathOptions.None);

            workDir.Create();
            File.WriteAllBytes(workFile.PathExport, [1, 2, 3]);
            File.WriteAllBytes(strayFile.PathExport, [4]);

            if (afterAnAttemptThatFailed)
            {
                // While another instance holds the repository, an initialization fails without touching what is in the directory, which may be that
                // instance's. It is not the first initialization: the one that succeeds is.
                Should.Throw<IOException>(repo.EnsureInitialized);

                workFile.State.ShouldBe(EntryState.Exists);
                strayFile.State.ShouldBe(EntryState.Exists);
            }

            previous.Dispose();

            if (byOperation)
                await AccessAsync(repo);
            else
                repo.EnsureInitialized();

            TempDir(repoDir).State.ShouldBe(EntryState.Exists);
            TempDir(repoDir).GetChildEntries().ShouldBeEmpty();
        }
        finally
        {
            previous.Dispose();
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Working directories
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task WorkingDirectories_AreNamedAfterTheFileAndVariant_WithANumberOfTheirOwn()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);

        try
        {
            var main = new WorkFileProcessor();
            var variant = new WorkFileProcessor();
            var fileId = await AddAndCommitAsync(repo, [1, 2, 3], main.WithVariant("thumb", variant.ToPipeline()));

            // Two attempts at the same variant of the same file, the first of which fails.
            var failed = new WorkFileProcessor(fail: true);
            var succeeded = new WorkFileProcessor();

            await Should.ThrowAsync<FileProcessingException>(
                async () => await repo.AddVariantAsync(fileId, "copy", failed.ToPipeline(), TestContext.CancellationToken));

            await repo.AddVariantAsync(fileId, "copy", succeeded.ToPipeline(), TestContext.CancellationToken);

            WorkingDirectoryName(main).ShouldMatch($"^{fileId} [0-9]+$");
            WorkingDirectoryName(variant).ShouldMatch($"^{fileId} thumb [0-9]+$");
            WorkingDirectoryName(failed).ShouldMatch($"^{fileId} copy [0-9]+$");
            WorkingDirectoryName(succeeded).ShouldMatch($"^{fileId} copy [0-9]+$");
            WorkingDirectoryName(succeeded).ShouldNotBe(WorkingDirectoryName(failed));
        }
        finally
        {
            repo.Dispose();
        }

        string WorkingDirectoryName(WorkFileProcessor processor)
        {
            var dir = processor.WorkFile.ShouldNotBeNull().ParentDirectory;
            dir.ParentDirectory.ShouldBe(TempDir(repoDir));

            return dir.Name;
        }
    }

    [TestMethod]
    public async Task Adds_RemoveTheirWorkingDirectories_WhenTheySucceedAndWhenTheyFail()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);
        var processors = new List<WorkFileProcessor>();

        try
        {
            // A transactional add of a main file with a data variant and an alias, and one that fails in its last variant, once its main file, a data
            // variant and an alias are staged.
            var fileId = await AddAndCommitAsync(repo, [1, 2, 3], Pipeline().WithVariant("data", Pipeline()).WithVariant("alias", Pipeline(alias: true)));
            ShouldAllBeRemoved();

            await using (var txn = await repo.BeginTransactionAsync())
            {
                var pipeline = Pipeline()
                    .WithVariant("data", Pipeline())
                    .WithVariant("alias", Pipeline(alias: true))
                    .WithVariant("failed", Pipeline(fail: true));

                await Should.ThrowAsync<FileProcessingException>(async () => await AddAsync(txn, new MemoryStream([4, 5]), pipeline));
            }

            ShouldAllBeRemoved();

            // A variant add of a data variant with an alias of it, one that fails in its processor, and one that fails in a variant of an alias once the
            // alias is staged.
            await repo.AddVariantAsync(fileId, "v1", Pipeline().WithVariant("v1-alias", Pipeline(alias: true)), TestContext.CancellationToken);
            ShouldAllBeRemoved();

            await Should.ThrowAsync<FileProcessingException>(
                async () => await repo.AddVariantAsync(fileId, "v2", Pipeline(fail: true), TestContext.CancellationToken));

            ShouldAllBeRemoved();

            await Should.ThrowAsync<FileProcessingException>(
                async () => await repo.AddVariantAsync(fileId, "v3", Pipeline(alias: true).WithVariant("v3-child", Pipeline(fail: true)), TestContext.CancellationToken));

            ShouldAllBeRemoved();
        }
        finally
        {
            repo.Dispose();
        }

        FileProcessingPipeline Pipeline(bool alias = false, bool fail = false)
        {
            var processor = new WorkFileProcessor(fail);
            processors.Add(processor);

            return processor.ToPipeline(aliasWhenVariantSourceUnchanged: alias);
        }

        // Every pipeline of the add ran and had a working directory, and the add removed it.
        void ShouldAllBeRemoved()
        {
            foreach (var processor in processors)
                processor.WorkFile.ShouldNotBeNull().ParentDirectory.State.ShouldBe(EntryState.ParentExists);

            processors.Clear();
            TempDir(repoDir).GetChildEntries().ShouldBeEmpty();
        }
    }

    [TestMethod]
    public async Task VariantAdd_AfterAnAttemptWhoseWorkingDirectoryCouldNotBeRemoved_Succeeds()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);

        // The first attempt fails with its work file open, as an external tool's child process that has not exited still has its output open.
        var holder = new WorkFileProcessor(fail: true, getFileToHold: workFile => workFile);

        try
        {
            var fileId = await AddAndCommitAsync(repo, [1, 2, 3]);

            await Should.ThrowAsync<FileProcessingException>(
                async () => await repo.AddVariantAsync(fileId, "thumb", holder.ToPipeline(), TestContext.CancellationToken));

            var leftover = holder.HeldFile.ShouldNotBeNull();
            ShouldBeLeftBehind(leftover);

            // The next attempt writes a work file of the same name while that one is still open, and its file is stored.
            byte[] content = [4, 5, 6];
            await repo.AddVariantAsync(fileId, "thumb", new RewriteProcessor(content).ToPipeline(), TestContext.CancellationToken);
            await ShouldBeStoredAsync(repo, fileId, "thumb", content);

            // It has removed its own working directory, and the one that could not be removed is left until the repository is opened again.
            TempDir(repoDir).GetChildDirectories().ShouldHaveSingleItem().ShouldBe(leftover.ParentDirectory);
        }
        finally
        {
            holder.HeldStream?.Dispose();
            repo.Dispose();
        }

        using var next = new FileRepo(repoDir, RepoFormatVersion.Version2);
        next.EnsureInitialized();

        TempDir(repoDir).GetChildEntries().ShouldBeEmpty();
    }

    [TestMethod]
    public async Task AliasVariantAdd_AfterAnAttemptWhoseWorkingDirectoryCouldNotBeRemoved_Succeeds()
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);

        // The first attempt stages the variant's alias marker and then fails in a variant of it, while something has the marker open, as a scanner can.
        string markerName = $"thumb.{FileRepoPaths.MainFileName}.bin{FileRepoPaths.AliasMarkerExtension}";
        var recorder = new WorkFileProcessor();
        var holder = new WorkFileProcessor(fail: true, getFileToHold: _ => recorder.WorkFile!.ParentDirectory.CombineFile(markerName, PathOptions.None));

        try
        {
            var fileId = await AddAndCommitAsync(repo, [1, 2, 3]);
            var pipeline = recorder.ToPipeline(aliasWhenVariantSourceUnchanged: true).WithVariant("small", holder.ToPipeline());

            await Should.ThrowAsync<FileProcessingException>(
                async () => await repo.AddVariantAsync(fileId, "thumb", pipeline, TestContext.CancellationToken));

            var leftover = holder.HeldFile.ShouldNotBeNull();
            ShouldBeLeftBehind(leftover);

            // The next attempt stages its marker under the same name while that one is still open, and the alias is stored.
            await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);

            var main = await repo.GetAsync(fileId);
            (await repo.GetVariantAsync(fileId, "thumb")).Path.ShouldBe(main.Path);
            await ShouldBeStoredAsync(repo, fileId, "thumb", [1, 2, 3]);

            TempDir(repoDir).GetChildDirectories().ShouldHaveSingleItem().ShouldBe(leftover.ParentDirectory);
        }
        finally
        {
            holder.HeldStream?.Dispose();
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(AliasStagingFailure.MarkerNotCreated)]
    [DataRow(AliasStagingFailure.SourceNotReleased)]
    public async Task AliasVariantAdd_ThatFailsBeforeTheAliasIsStaged_LeavesNothingBehind(AliasStagingFailure failure)
    {
        var repoDir = NewRepoDir();
        var repo = CreateRepo(repoDir);
        var processor = new AliasStagingFailureProcessor(failure);

        try
        {
            var fileId = await AddAndCommitAsync(repo, [1, 2, 3]);

            await Should.ThrowAsync<IOException>(
                async () => await repo.AddVariantAsync(fileId, "thumb", processor.ToPipeline(aliasWhenVariantSourceUnchanged: true), TestContext.CancellationToken));

            // The variant's context is disposed: its working directory is removed, and the stream it had as its source, which it owned, is closed.
            TempDir(repoDir).GetChildEntries().ShouldBeEmpty();
            processor.Source.ShouldNotBeNull().CanRead.ShouldBeFalse();

            // Nothing of the variant is stored, and it can be added.
            await Should.ThrowAsync<RepoFileNotFoundException>(async () => await repo.GetVariantAsync(fileId, "thumb"));
            await repo.AddVariantAsync(fileId, "thumb", VariantPipelines.Alias(), TestContext.CancellationToken);
            await ShouldBeStoredAsync(repo, fileId, "thumb", [1, 2, 3]);
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes sure that a file in the working directory of an add that failed is still there, as the add could not remove it. On Windows the handle that the
    /// test holds on it kept the add from deleting it. Elsewhere an open file does not keep it from being deleted, so the add removed the directory, and the
    /// test puts the file back where it was, as a removal that failed would have left it.
    /// </summary>
    private static void ShouldBeLeftBehind(IAbsoluteFilePath file)
    {
        if (OperatingSystem.IsWindows())
        {
            file.State.ShouldBe(EntryState.Exists);
            return;
        }

        file.ParentDirectory.State.ShouldBe(EntryState.ParentExists);
        file.ParentDirectory.Create();
        File.WriteAllBytes(file.PathExport, []);
    }

    /// <summary>
    /// Asserts that a variant is in the repository and reads back as the specified content.
    /// </summary>
    private async Task ShouldBeStoredAsync(FileRepo repo, FileId fileId, string variantId, byte[] content)
    {
        var file = await repo.GetVariantAsync(fileId, variantId);
        (await File.ReadAllBytesAsync(file.Path.PathExport, TestContext.CancellationToken)).ShouldBe(content);
    }

    /// <summary>
    /// Asserts that a file is in the repository and reads back as the specified content.
    /// </summary>
    private async Task ShouldBeStoredAsync(FileRepo repo, FileId fileId, byte[] content)
    {
        var file = await repo.GetAsync(fileId);
        (await File.ReadAllBytesAsync(file.Path.PathExport, TestContext.CancellationToken)).ShouldBe(content);
    }

    private Task<FileId> AddAndCommitAsync(FileRepo repo, byte[] content) => AddAndCommitAsync(repo, content, new RewriteProcessor(content).ToPipeline());

    private async Task<FileId> AddAndCommitAsync(FileRepo repo, byte[] content, FileProcessingPipeline pipeline)
    {
        await using var txn = await repo.BeginTransactionAsync();
        var fileId = await AddAsync(txn, new MemoryStream(content), pipeline);
        await txn.CommitAsync(TestContext.CancellationToken);

        return fileId;
    }

    private async Task<FileId> AddAsync(FileRepoTransaction txn, Stream source, FileProcessingPipeline pipeline)
    {
        var added = await txn.AddAsync(source, ".bin", leaveOpen: false, pipeline, TestContext.CancellationToken);
        return added.FileId;
    }

    private static async Task AccessAsync(FileRepo repo)
    {
        await using (await repo.BeginTransactionAsync())
        {
        }
    }

    private static FileRepo CreateRepo(IAbsoluteDirectoryPath repoDir)
    {
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version2, o => {
            o.MaxAccessWaitOrRetryTime = _wait;
            o.HealthCheckInterval = _healthCheckInterval;
        });

        repo.EnsureCreated();
        return repo;
    }

    private static IAbsoluteDirectoryPath TempDir(IAbsoluteDirectoryPath repoDir) =>
        repoDir.CombineDirectory(FileRepoPaths.TempDirectoryName, PathOptions.None);

    private static IAbsoluteDirectoryPath CleanupDir(IAbsoluteDirectoryPath repoDir) =>
        repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_TempDirectory_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }

    /// <summary>
    /// A processor that writes a work file and records where it is, and then passes the source through unchanged or fails. Before it fails, it can open a
    /// file without sharing its deletion and leave it open for the test, which on Windows keeps the add from removing the working directory that the file is
    /// in.
    /// </summary>
    private sealed class WorkFileProcessor : FileProcessor
    {
        private readonly bool _fail;
        private readonly Func<IAbsoluteFilePath, IAbsoluteFilePath>? _getFileToHold;

        /// <summary>
        /// Initializes a new instance of the <see cref="WorkFileProcessor"/> class.
        /// </summary>
        /// <param name="fail">Whether the processor fails once it has written its work file.</param>
        /// <param name="getFileToHold">Gets the file to keep open from the processor's work file, or <see langword="null"/> to keep none open.</param>
        public WorkFileProcessor(bool fail = false, Func<IAbsoluteFilePath, IAbsoluteFilePath>? getFileToHold = null)
        {
            _fail = fail;
            _getFileToHold = getFileToHold;
        }

        public IAbsoluteFilePath? WorkFile { get; private set; }

        public IAbsoluteFilePath? HeldFile { get; private set; }

        public FileStream? HeldStream { get; private set; }

        public override IReadOnlyList<string> AllowedFileExtensions => [];

        protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
        {
            var workFile = context.GetNewWorkFile(context.Extension);
            workFile.ParentDirectory.Create();

            var stream = workFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.None);

            await using (stream.ConfigureAwait(false))
                await stream.WriteAsync(new byte[] { 0 }).ConfigureAwait(false);

            WorkFile = workFile;

            if (_getFileToHold is not null)
            {
                HeldFile = _getFileToHold(workFile);
                HeldStream = HeldFile.OpenStream(FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            if (_fail)
                throw new InvalidOperationException("Simulated processor failure.");

            var sourceFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
            return FileProcessingResult.File(sourceFile, hasChanges: false);
        }
    }

    /// <summary>
    /// A processor that makes no changes and leaves its context in a state in which staging an alias fails (see <see cref="AliasStagingFailure"/>). It
    /// records the stream that it leaves as the context's source, which the context owns.
    /// </summary>
    private sealed class AliasStagingFailureProcessor : FileProcessor
    {
        private readonly AliasStagingFailure _failure;

        public AliasStagingFailureProcessor(AliasStagingFailure failure) => _failure = failure;

        public Stream? Source { get; private set; }

        public override IReadOnlyList<string> AllowedFileExtensions => [];

        protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
        {
            if (_failure is AliasStagingFailure.SourceNotReleased)
            {
                Source = new CloseFailingStream();
                return FileProcessingResult.Stream(Source, context.Extension, hasChanges: false);
            }

            // An entry where the alias marker goes makes its creation fail, as an I/O error would.
            var workingDir = context.GetNewWorkDirectory().ParentDirectory.ShouldNotBeNull();
            workingDir.Create();

            string markerName = $"{context.VariantId}.{FileRepoPaths.MainFileName}{context.Extension}{FileRepoPaths.AliasMarkerExtension}";
            await File.WriteAllBytesAsync(workingDir.CombineFile(markerName, PathOptions.None).PathExport, [], context.CancellationToken).ConfigureAwait(false);

            Source = await context.GetSourceAsSeekableStreamAsync(preferInMemory: false, maxInMemoryCopySize: 0).ConfigureAwait(false);
            return FileProcessingResult.Stream(Source, context.Extension, hasChanges: false);
        }
    }

    /// <summary>
    /// A stream that fails the first time it is closed and is closed all the same, as a stream that writes out what it buffered when it is closed does when
    /// the write fails.
    /// </summary>
    private sealed class CloseFailingStream : MemoryStream
    {
        private bool _isClosed;

        public override ValueTask DisposeAsync()
        {
            if (_isClosed)
                return ValueTask.CompletedTask;

            _isClosed = true;
            Dispose();

            return ValueTask.FromException(new IOException("Simulated failure to close the stream."));
        }
    }
}
