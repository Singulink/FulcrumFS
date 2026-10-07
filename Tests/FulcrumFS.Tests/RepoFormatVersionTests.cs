using System.Globalization;
using System.Security.Cryptography;
using FulcrumFS.Utilities;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class RepoFormatVersionTests
{
    private const string VersionOneCreated = "2025-03-04T05:06:07.0891011Z";
    private const string VersionOneContent = $"version=1\ncreated={VersionOneCreated}\n";
    private const string UpgradedContent = $"version=2\nread-compat-version=1\nclean-compat-version=2\ncreated={VersionOneCreated}\n";

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static int _counter;

    public required TestContext TestContext { get; set; }

    // ---------------------------------------------------------------------------------------------------------------
    // The option
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void MinimumFormatVersionOption_RejectsUndefinedValues_AndIsFrozen()
    {
        var options = new FileRepoOptions(NewRepoDir(), RepoFormatVersion.Version1);
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Version1);

        Should.Throw<ArgumentOutOfRangeException>(() => options.MinimumFormatVersion = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MinimumFormatVersion = (RepoFormatVersion)(-2));
        Should.Throw<ArgumentOutOfRangeException>(() => options.MinimumFormatVersion = (RepoFormatVersion)(RepoInfoFile.SupportedVersion + 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new FileRepoOptions(NewRepoDir(), 0));
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Version1);

        // Latest orders above every real version.
        options.MinimumFormatVersion = RepoFormatVersion.Latest;
        Enum.GetValues<RepoFormatVersion>().Max().ShouldBe(RepoFormatVersion.Latest);

        options.MinimumFormatVersion = RepoFormatVersion.Version2;
        using var repo = new FileRepo(options);

        Should.Throw<InvalidOperationException>(() => options.MinimumFormatVersion = RepoFormatVersion.Version1);
        repo.Options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Version2);
    }

    [TestMethod]
    public void MinimumFormatVersionOption_NeverSet_RepositoryConstructionThrows_NamingTheOption()
    {
        // The property is required in code, but options bound from configuration are created without it, as here.
        var options = Activator.CreateInstance<FileRepoOptions>();
        options.BaseDirectory = NewRepoDir();
        ((int)options.MinimumFormatVersion).ShouldBe(0);

        // No fallback: the message names the option, by the name a configuration section has to use, and the choices.
        const string message =
            "FileRepoOptions.MinimumFormatVersion must be set to Version1, Version2 or Latest; Version1 keeps repositories compatible with FulcrumFS 1.0.x.";

        var ex = Should.Throw<ArgumentException>(() => new FileRepo(options));
        ex.ParamName.ShouldBe("options");
        ex.Message.ShouldStartWith(message);

        ex = Should.Throw<ArgumentException>(() => new FileRepo(Microsoft.Extensions.Options.Options.Create(options)));
        ex.ParamName.ShouldBe("options");
        ex.Message.ShouldStartWith(message);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Creating repositories
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void NewRepository_VersionOne_InfoFileIsWrittenAsFulcrumFS10xWroteIt()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version1);

        try
        {
            var before = DateTime.UtcNow;
            repo.EnsureCreated();
            var after = DateTime.UtcNow;

            // FulcrumFS 1.0.x wrote the version and the creation time, without the compat keys it did not know.
            string[] lines = ReadInfo(repoDir).Split('\n');
            lines.Length.ShouldBe(3);
            lines[0].ShouldBe("version=1");
            lines[1].ShouldStartWith("created=");
            lines[2].ShouldBeEmpty();

            var created = DateTime.Parse(lines[1]["created=".Length..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            created.Kind.ShouldBe(DateTimeKind.Utc);
            created.ShouldBeInRange(before, after);

            RepoInfoFile.Read(InfoFile(repoDir)).ShouldBe(new RepoInfoFile.Versions(FullAccess: 1, ReadCompat: 1, CleanCompat: 1));
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version1);

            repo.EnsureInitialized();
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version1);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version2)]
    [DataRow(RepoFormatVersion.Latest)]
    public void NewRepository_VersionTwoOrLatest_InfoFileDeclaresVersionTwo_WithCompatVersionsAndCreationTime(RepoFormatVersion formatVersion)
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, formatVersion);

        try
        {
            var before = DateTime.UtcNow;
            repo.EnsureCreated();
            var after = DateTime.UtcNow;

            string[] lines = ReadInfo(repoDir).Split('\n');
            lines.Length.ShouldBe(5);
            lines[0].ShouldBe("version=2");
            lines[1].ShouldBe("read-compat-version=1");
            lines[2].ShouldBe("clean-compat-version=2");
            lines[3].ShouldStartWith("created=");
            lines[4].ShouldBeEmpty();

            var created = DateTime.Parse(lines[3]["created=".Length..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            created.Kind.ShouldBe(DateTimeKind.Utc);
            created.ShouldBeInRange(before, after);

            RepoInfoFile.Read(InfoFile(repoDir)).ShouldBe(new RepoInfoFile.Versions(FullAccess: 2, ReadCompat: 1, CleanCompat: 2));
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version2);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public void NewRepository_EnsureCreated_OpensIt_HoldingTheLock_WithItsFormatVersionKnown()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version1);

        try
        {
            repo.EnsureCreated();

            // One call creates the repository and initializes the instance as EnsureInitialized does: the lock is held and the temp directory, which only
            // initialization creates, is ready.
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version1);
            IsHeld(LockFile(repoDir)).ShouldBeTrue();
            TempDir(repoDir).State.ShouldBe(EntryState.Exists);

            // Both methods can be called again on the open instance.
            repo.EnsureCreated();
            repo.EnsureInitialized();
            IsHeld(LockFile(repoDir)).ShouldBeTrue();
        }
        finally
        {
            repo.Dispose();
        }

        LockFile(repoDir).State.ShouldNotBe(EntryState.Exists);
    }

    [TestMethod]
    public void EnsureCreated_WhileAnotherInstanceHoldsTheRepository_ThrowsIOExceptionAtOnce_AsEnsureInitializedDoes()
    {
        var repoDir = NewRepoDir();
        var first = new FileRepo(repoDir, RepoFormatVersion.Version2);

        // A long wait, so that a retry like the one operations make would show as a TimeoutException after a minute instead of the IOException.
        var second = new FileRepo(repoDir, RepoFormatVersion.Version2, o => o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60));

        try
        {
            first.EnsureCreated();

            // Neither method waits for the lock: both throw the sharing violation itself, and the second instance has not opened the repository.
            Should.Throw<IOException>(second.EnsureCreated).GetType().ShouldBe(typeof(IOException));
            Should.Throw<IOException>(second.EnsureInitialized).GetType().ShouldBe(typeof(IOException));
            Should.Throw<InvalidOperationException>(() => second.FormatVersion);

            first.FormatVersion.ShouldBe(RepoFormatVersion.Version2);
            IsHeld(LockFile(repoDir)).ShouldBeTrue();

            // Once the first instance lets go, the second one opens the repository.
            first.Dispose();
            second.EnsureCreated();

            second.FormatVersion.ShouldBe(RepoFormatVersion.Version2);
            IsHeld(LockFile(repoDir)).ShouldBeTrue();
        }
        finally
        {
            second.Dispose();
            first.Dispose();
        }
    }

    [TestMethod]
    public void FormatVersion_BeforeTheRepositoryIsOpened_Throws()
    {
        using var repo = new FileRepo(NewRepoDir(), RepoFormatVersion.Version1);
        Should.Throw<InvalidOperationException>(() => repo.FormatVersion);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Existing repositories
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task VersionOneRepository_VersionOneOption_IsNeverChanged()
    {
        var repoDir = NewVersionOneRepoDir();
        byte[] hash = HashInfo(repoDir);
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version1);

        try
        {
            repo.EnsureCreated();
            HashInfo(repoDir).ShouldBe(hash);

            repo.EnsureInitialized();
            HashInfo(repoDir).ShouldBe(hash);

            await AddSampleFileAsync(repo);
            HashInfo(repoDir).ShouldBe(hash);
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version1);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    [DataRow(RepoFormatVersion.Version2)]
    [DataRow(RepoFormatVersion.Latest)]
    public void VersionOneRepository_VersionTwoOrLatest_IsUpgradedAtInitialization_UnderTheLock_KeepingItsCreationTime(RepoFormatVersion formatVersion)
    {
        var repoDir = NewVersionOneRepoDir();
        var tempFile = UpgradeTempFile(repoDir);
        var repo = new FileRepo(repoDir, formatVersion);

        try
        {
            bool? lockHeldAtFlush = null;

            DurableIO.Observer = path =>
            {
                if (path == tempFile.PathExport)
                    lockHeldAtFlush = IsHeld(LockFile(repoDir));
            };

            repo.EnsureInitialized();

            // The upgraded content was flushed while this instance held the repository lock.
            lockHeldAtFlush.ShouldBe(true);
            ReadInfo(repoDir).ShouldBe(UpgradedContent);
            tempFile.State.ShouldNotBe(EntryState.Exists);
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version2);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    [DoNotParallelize] // DurableIO.Observer is process-wide.
    [DataRow(RepoFormatVersion.Version2)]
    [DataRow(RepoFormatVersion.Latest)]
    public void VersionOneRepository_VersionTwoOrLatest_IsUpgradedByEnsureCreated_UnderTheLock_KeepingItsCreationTime(RepoFormatVersion formatVersion)
    {
        var repoDir = NewVersionOneRepoDir();
        var tempFile = UpgradeTempFile(repoDir);
        var repo = new FileRepo(repoDir, formatVersion);

        try
        {
            bool? lockHeldAtFlush = null;

            DurableIO.Observer = path =>
            {
                if (path == tempFile.PathExport)
                    lockHeldAtFlush = IsHeld(LockFile(repoDir));
            };

            // EnsureCreated finds the repository and initializes the instance as EnsureInitialized does, which makes the upgrade.
            repo.EnsureCreated();

            // The upgraded content was flushed while this instance held the repository lock.
            lockHeldAtFlush.ShouldBe(true);
            ReadInfo(repoDir).ShouldBe(UpgradedContent);
            tempFile.State.ShouldNotBe(EntryState.Exists);
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version2);
        }
        finally
        {
            DurableIO.Observer = null;
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version2)]
    [DataRow(RepoFormatVersion.Latest)]
    public async Task VersionOneRepository_VersionTwoOrLatest_IsUpgradedByTheFirstOperation(RepoFormatVersion formatVersion)
    {
        var repoDir = NewVersionOneRepoDir();
        var repo = new FileRepo(repoDir, formatVersion);

        try
        {
            // Neither EnsureCreated nor EnsureInitialized is called: the first operation initializes the instance.
            Should.Throw<InvalidOperationException>(() => repo.FormatVersion);
            ReadInfo(repoDir).ShouldBe(VersionOneContent);

            await using (await repo.BeginTransactionAsync()) { }

            ReadInfo(repoDir).ShouldBe(UpgradedContent);
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version2);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public void VersionOneRepository_HeldByAnotherInstance_IsNotUpgradedUntilThisInstanceGetsTheLock()
    {
        var repoDir = NewVersionOneRepoDir();
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version2);

        try
        {
            // Stands in for an instance of an earlier version that has the repository open: the format must not change underneath it.
            using (OpenRepoLock(repoDir))
            {
                Should.Throw<IOException>(repo.EnsureCreated);
                Should.Throw<IOException>(repo.EnsureInitialized);
                ReadInfo(repoDir).ShouldBe(VersionOneContent);
            }

            repo.EnsureInitialized();
            ReadInfo(repoDir).ShouldBe(UpgradedContent);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public void VersionOneRepository_TempFileLeftByAnInterruptedUpgrade_IsReplaced()
    {
        var repoDir = NewVersionOneRepoDir();
        File.WriteAllText(UpgradeTempFile(repoDir).PathExport, "version=2\nread-comp");
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version2);

        try
        {
            repo.EnsureInitialized();

            ReadInfo(repoDir).ShouldBe(UpgradedContent);
            UpgradeTempFile(repoDir).State.ShouldNotBe(EntryState.Exists);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    public async Task VersionTwoRepository_VersionOneOption_KeepsFullAccessAndItsVersion_AndStagesWithAJournal()
    {
        var repoDir = NewRepoDir();

        using (var creator = new FileRepo(repoDir, RepoFormatVersion.Version2))
            creator.EnsureCreated();

        byte[] hash = HashInfo(repoDir);
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version1);

        try
        {
            repo.EnsureCreated();
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version2);

            var fileId = await AddSampleFileAsync(repo);

            // The staged transaction goes by the version the repository has, not by the option.
            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                await txn.StageAsync(TestContext.CancellationToken);

                CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().Extension.ShouldBe(FileRepoPaths.StagedJournalExtension);
                IndeterminateMarker(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            }

            HashInfo(repoDir).ShouldBe(hash);
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version2);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(DeleteMode.Immediate)]
    [DataRow(DeleteMode.DeferredUntilClean)]
    public async Task VersionOneRepositoryWrittenByFulcrumFS10x_StagedTransaction_UsesPerFileMarkers(DeleteMode deleteMode)
    {
        var repoDir = NewVersionOneRepoDir();
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version1, o => o.DeleteMode = deleteMode);

        try
        {
            var fileId = await AddSampleFileAsync(repo);

            await using (var txn = await repo.BeginStagedTransactionAsync())
            {
                await txn.DeleteAsync(fileId, TestContext.CancellationToken);
                var stage = await txn.StageAsync(TestContext.CancellationToken);

                // The deletion is recorded as an ordinary transaction records it: the file's indeterminate marker, held until the commit.
                CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(IndeterminateMarker(repoDir, fileId));
                IsHeld(IndeterminateMarker(repoDir, fileId)).ShouldBeTrue();

                await stage.CommitAsync(TestContext.CancellationToken);
            }

            if (deleteMode is DeleteMode.Immediate)
            {
                CleanupDir(repoDir).GetChildFiles().ShouldBeEmpty();
                FileDir(repoDir, fileId).State.ShouldNotBe(EntryState.Exists);
            }
            else
            {
                // A FulcrumFS 1.0.x cleaner knows this delete marker.
                CleanupDir(repoDir).GetChildFiles().ShouldHaveSingleItem().ShouldBe(DeleteMarker(repoDir, fileId));
                FileDir(repoDir, fileId).State.ShouldBe(EntryState.Exists);
            }

            ReadInfo(repoDir).ShouldBe(VersionOneContent);
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Newer repositories
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("version=3\n")]
    [DataRow("version=3\nread-compat-version=1\nclean-compat-version=2\n")]
    public void NewerFullAccessVersion_IsRejected_AndLeftUnchanged(string content)
    {
        var repoDir = NewRepoDir();

        using (var creator = new FileRepo(repoDir, RepoFormatVersion.Version1))
            creator.EnsureCreated();

        WriteInfo(repoDir, content);
        var repo = new FileRepo(repoDir, RepoFormatVersion.Version2);

        try
        {
            Should.Throw<NotSupportedException>(repo.EnsureCreated);
            Should.Throw<NotSupportedException>(repo.EnsureInitialized);

            ReadInfo(repoDir).ShouldBe(content);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow("version=3\nread-compat-version=1\nclean-compat-version=3\n")]
    [DataRow("version=3\nread-compat-version=2\n")]
    public async Task NewerRepository_ReadCompatSupported_IsOpenedReadOnly(string content)
    {
        var repoDir = NewRepoDir();
        FileId fileId;

        using (var creator = new FileRepo(repoDir, RepoFormatVersion.Version1))
        {
            creator.EnsureCreated();
            fileId = await AddSampleFileAsync(creator);
        }

        WriteInfo(repoDir, content);
        var repo = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true);

        try
        {
            repo.EnsureCreated();
            repo.FormatVersion.ShouldBe((RepoFormatVersion)3);

            await using (var stream = await repo.OpenAsync(fileId, FileAccessPattern.Sequential))
                stream.Length.ShouldBe(new FileInfo(_sampleDir.CombineFile("sample.jpg").PathExport).Length);

            ReadInfo(repoDir).ShouldBe(content);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow("version=3\n")]
    [DataRow("version=3\nread-compat-version=3\nclean-compat-version=2\n")]
    public void NewerRepository_ReadCompatUnsupported_IsRejectedReadOnly(string content)
    {
        var repoDir = NewRepoDir();

        using (var creator = new FileRepo(repoDir, RepoFormatVersion.Version1))
            creator.EnsureCreated();

        WriteInfo(repoDir, content);
        var repo = new FileRepo(repoDir, RepoFormatVersion.Latest, o => o.ReadOnly = true);

        try
        {
            Should.Throw<NotSupportedException>(repo.EnsureCreated);
            Should.Throw<NotSupportedException>(repo.EnsureInitialized);
            ReadInfo(repoDir).ShouldBe(content);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [TestMethod]
    [DataRow(RepoFormatVersion.Version2)]
    [DataRow(RepoFormatVersion.Latest)]
    public async Task VersionOneRepository_ReadOnlyWithNewerOption_IsNotUpgraded(RepoFormatVersion formatVersion)
    {
        var repoDir = NewVersionOneRepoDir();
        byte[] hash = HashInfo(repoDir);

        FileId fileId;

        using (var writer = new FileRepo(repoDir, RepoFormatVersion.Version1))
            fileId = await AddSampleFileAsync(writer);

        var repo = new FileRepo(repoDir, formatVersion, o => o.ReadOnly = true);

        try
        {
            // EnsureCreated opens a read-only instance as EnsureInitialized does: without the lock and without upgrading.
            repo.EnsureCreated();
            HashInfo(repoDir).ShouldBe(hash);
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version1);
            LockFile(repoDir).State.ShouldNotBe(EntryState.Exists);

            repo.EnsureInitialized();
            (await repo.GetAsync(fileId)).FileId.ShouldBe(fileId);

            HashInfo(repoDir).ShouldBe(hash);
            repo.FormatVersion.ShouldBe(RepoFormatVersion.Version1);
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The cleaner
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(VersionOneContent)]
    [DataRow("version=2\nread-compat-version=1\nclean-compat-version=2\n")]
    [DataRow("version=3\nclean-compat-version=2\n")]
    public async Task Cleaner_CleansRepositoryItCanClean_WithoutChangingItsInfoFile(string content)
    {
        var (repoDir, fileDir) = await NewRepoWithPendingDeletionAsync(content);
        byte[] hash = HashInfo(repoDir);

        await new FileRepoCleaner(repoDir).CleanAsync(TimeSpan.Zero, TestContext.CancellationToken);

        fileDir.State.ShouldNotBe(EntryState.Exists);
        HashInfo(repoDir).ShouldBe(hash);
    }

    [TestMethod]
    [DataRow("version=2\nread-compat-version=1\nclean-compat-version=3\n")]
    [DataRow("version=3\n")]
    public async Task Cleaner_RejectsRepositoryThatNeedsANewerCleaner_WithoutCleaning(string content)
    {
        var (repoDir, fileDir) = await NewRepoWithPendingDeletionAsync(content);

        await Should.ThrowAsync<NotSupportedException>(async () => await new FileRepoCleaner(repoDir).CleanAsync(TimeSpan.Zero, TestContext.CancellationToken));

        fileDir.State.ShouldBe(EntryState.Exists);
        ReadInfo(repoDir).ShouldBe(content);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a repository with one file whose deletion has committed and waits for the cleaner, then replaces its info file with the specified content.
    /// Returns the base directory and the file's group directory.
    /// </summary>
    private async Task<(IAbsoluteDirectoryPath RepoDir, IAbsoluteDirectoryPath FileDir)> NewRepoWithPendingDeletionAsync(string infoContent)
    {
        var repoDir = NewRepoDir();
        FileId fileId;

        using (var repo = new FileRepo(repoDir, RepoFormatVersion.Version1, o => o.DeleteMode = DeleteMode.DeferredUntilClean))
        {
            repo.EnsureCreated();
            fileId = await AddSampleFileAsync(repo);

            await using var txn = await repo.BeginTransactionAsync();
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        WriteInfo(repoDir, infoContent);

        var fileDir = FileDir(repoDir, fileId);
        fileDir.State.ShouldBe(EntryState.Exists);

        return (repoDir, fileDir);
    }

    private async Task<FileId> AddSampleFileAsync(FileRepo repo)
    {
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();

        var fileId = (await txn.AddAsync(source, ".jpg", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        return fileId;
    }

    /// <summary>
    /// Creates a repository and replaces its info file with one written by FulcrumFS 1.0.x.
    /// </summary>
    private static IAbsoluteDirectoryPath NewVersionOneRepoDir()
    {
        var repoDir = NewRepoDir();

        using (var creator = new FileRepo(repoDir, RepoFormatVersion.Version1))
            creator.EnsureCreated();

        WriteInfo(repoDir, VersionOneContent);
        return repoDir;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the file exists and is held open, as the repository lock is by an initialized instance and an indeterminate marker is
    /// by its transaction.
    /// </summary>
    private static bool IsHeld(IAbsoluteFilePath file)
    {
        try
        {
            using var probe = file.OpenStream(FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static FileStream OpenRepoLock(IAbsoluteDirectoryPath repoDir) =>
        LockFile(repoDir).OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    private static IAbsoluteFilePath LockFile(IAbsoluteDirectoryPath repoDir) => repoDir.CombineFile(FileRepoPaths.RepoLockFileName, PathOptions.None);

    private static IAbsoluteFilePath InfoFile(IAbsoluteDirectoryPath repoDir) => repoDir.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None);

    private static IAbsoluteDirectoryPath TempDir(IAbsoluteDirectoryPath repoDir) =>
        repoDir.CombineDirectory(FileRepoPaths.TempDirectoryName, PathOptions.None);

    private static IAbsoluteFilePath UpgradeTempFile(IAbsoluteDirectoryPath repoDir) =>
        repoDir.CombineFile(FileRepoPaths.InfoFileName + ".tmp", PathOptions.None);

    private static IAbsoluteDirectoryPath CleanupDir(IAbsoluteDirectoryPath repoDir) =>
        repoDir.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);

    private static IAbsoluteDirectoryPath FileDir(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        repoDir.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None).Combine(fileId.RelativeDirectory);

    private static IAbsoluteFilePath IndeterminateMarker(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        CleanupDir(repoDir).CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);

    private static IAbsoluteFilePath DeleteMarker(IAbsoluteDirectoryPath repoDir, FileId fileId) =>
        CleanupDir(repoDir).CombineFile(fileId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);

    private static string ReadInfo(IAbsoluteDirectoryPath repoDir) => File.ReadAllText(InfoFile(repoDir).PathExport);

    private static byte[] HashInfo(IAbsoluteDirectoryPath repoDir) => SHA256.HashData(File.ReadAllBytes(InfoFile(repoDir).PathExport));

    private static void WriteInfo(IAbsoluteDirectoryPath repoDir, string content) => File.WriteAllText(InfoFile(repoDir).PathExport, content);

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_RepoFormatVersion_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
