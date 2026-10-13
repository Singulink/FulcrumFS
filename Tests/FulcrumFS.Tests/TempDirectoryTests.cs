using System.Globalization;

namespace FulcrumFS;

/// <summary>
/// Pins when a repository instance empties its temp directory: once, at its first initialization, which removes what a process that ended left behind. When
/// it initializes again, after a health check has dropped the repository lock, the directory holds the working files of its own operations, which go on
/// through the outage, so it is left as it is.
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
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts that a file is in the repository and reads back as the specified content.
    /// </summary>
    private async Task ShouldBeStoredAsync(FileRepo repo, FileId fileId, byte[] content)
    {
        var file = await repo.GetAsync(fileId);
        (await File.ReadAllBytesAsync(file.Path.PathExport, TestContext.CancellationToken)).ShouldBe(content);
    }

    private async Task<FileId> AddAndCommitAsync(FileRepo repo, byte[] content)
    {
        await using var txn = await repo.BeginTransactionAsync();
        var fileId = await AddAsync(txn, new MemoryStream(content), new RewriteProcessor(content).ToPipeline());
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
}
