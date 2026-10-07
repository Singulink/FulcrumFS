using System.Globalization;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileIdGeneratorTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();

    // An arbitrary time with sub-millisecond ticks, which the embedded timestamp drops.
    private static readonly DateTimeOffset _start = new DateTimeOffset(2026, 10, 7, 12, 34, 56, 789, TimeSpan.Zero).AddTicks(4321);
    private static readonly long _startMs = _start.ToUnixTimeMilliseconds();

    private static int _counter;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void FrozenClock_SixteenIdsPerMillisecond_ThenTimestampMovesAhead()
    {
        var generator = new FileIdGenerator(new ManualTimeProvider(_start));
        var ids = Enumerable.Range(0, 33).Select(_ => generator.CreateSequential()).ToList();

        for (int i = 0; i < 16; i++)
        {
            GetTimestampMilliseconds(ids[i]).ShouldBe(_startMs);
            GetCounter(ids[i]).ShouldBe(i);

            GetTimestampMilliseconds(ids[16 + i]).ShouldBe(_startMs + 1);
            GetCounter(ids[16 + i]).ShouldBe(i);
        }

        GetTimestampMilliseconds(ids[32]).ShouldBe(_startMs + 2);
        GetCounter(ids[32]).ShouldBe(0);

        ShouldBeStrictlyIncreasing(ids);
    }

    [TestMethod]
    public void ClockAdvance_NextIdTakesClockTimeWithCounterZero()
    {
        var clock = new ManualTimeProvider(_start);
        var generator = new FileIdGenerator(clock);
        FileId previous = null!;

        // 20 IDs leave the generator one millisecond ahead of the clock with a counter of 3.
        for (int i = 0; i < 20; i++)
            previous = generator.CreateSequential();

        GetTimestampMilliseconds(previous).ShouldBe(_startMs + 1);
        GetCounter(previous).ShouldBe(3);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        var next = generator.CreateSequential();

        GetTimestampMilliseconds(next).ShouldBe(_startMs + 10);
        GetCounter(next).ShouldBe(0);
        ShouldBeStrictlyIncreasing([previous, next]);
    }

    [TestMethod]
    public void ClockRegression_IdsKeepIncreasing()
    {
        var clock = new ManualTimeProvider(_start);
        var generator = new FileIdGenerator(clock);
        var ids = new List<FileId> { generator.CreateSequential() };

        clock.Advance(TimeSpan.FromMilliseconds(-5));
        ids.Add(generator.CreateSequential());

        ShouldBeStrictlyIncreasing(ids);

        // The generator keeps counting within its last timestamp instead of following the clock back, and moves ahead once that timestamp is used up.
        GetTimestampMilliseconds(ids[1]).ShouldBe(_startMs);
        GetCounter(ids[1]).ShouldBe(1);

        for (int i = 0; i < 15; i++)
            ids.Add(generator.CreateSequential());

        GetTimestampMilliseconds(ids[16]).ShouldBe(_startMs + 1);
        GetCounter(ids[16]).ShouldBe(0);
        ShouldBeStrictlyIncreasing(ids);
    }

    [TestMethod]
    public void ClockBeforeUnixEpoch_UsesEpoch()
    {
        var generator = new FileIdGenerator(new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(-1)));
        var first = generator.CreateSequential();
        var second = generator.CreateSequential();

        first.Timestamp.ShouldBe(DateTimeOffset.UnixEpoch);
        GetCounter(first).ShouldBe(0);
        second.Timestamp.ShouldBe(DateTimeOffset.UnixEpoch);
        GetCounter(second).ShouldBe(1);
    }

    [TestMethod]
    public void SystemClock_100000Ids_StrictlyIncreasingVersion7AndDistinct()
    {
        var generator = new FileIdGenerator();
        var ids = new FileId[100_000];

        for (int i = 0; i < ids.Length; i++)
            ids[i] = generator.CreateSequential();

        foreach (var id in ids)
        {
            GetVersion(id).ShouldBe(7);
            GetVariantBits(id).ShouldBe(0b10);
        }

        ShouldBeStrictlyIncreasing(ids);
        ids.Select(id => id.Guid).Distinct().Count().ShouldBe(ids.Length);
    }

    [TestMethod]
    public void TwoGenerators_AreIndependent()
    {
        var clock = new ManualTimeProvider(_start);
        var first = new FileIdGenerator(clock);
        var second = new FileIdGenerator(clock);

        // Interleaved, so a shared counter would run out halfway through.
        for (int i = 0; i < 16; i++)
        {
            var a = first.CreateSequential();
            var b = second.CreateSequential();

            GetTimestampMilliseconds(a).ShouldBe(_startMs);
            GetCounter(a).ShouldBe(i);
            GetTimestampMilliseconds(b).ShouldBe(_startMs);
            GetCounter(b).ShouldBe(i);
        }
    }

    [TestMethod]
    public void Timestamp_IsClockMillisecond_AndNullForSecureId()
    {
        var id = new FileIdGenerator(new ManualTimeProvider(_start)).CreateSequential();

        id.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(_startMs));
        id.Timestamp.ShouldNotBeNull().Offset.ShouldBe(TimeSpan.Zero);

        // The timestamp is written the way the BCL writes one into its own version 7 GUIDs.
        id.ToString()[..13].ShouldBe(Guid.CreateVersion7(_start).ToString("D")[..13]);

        FileId.CreateSecure().Timestamp.ShouldBeNull();
    }

    [TestMethod]
    public void Timestamp_DecodesRfc9562Example_AndIsNullBeyondDateTimeOffsetRange()
    {
        // The version 7 example in RFC 9562 appendix A.6: Unix time 1645557742000 ms, which is 2022-02-22 19:22:22 UTC.
        FileId.Parse("017f22e2-79b0-7cc3-98c4-dc0c0c07398f").Timestamp.ShouldBe(new DateTimeOffset(2022, 2, 22, 19, 22, 22, TimeSpan.Zero));

        FileId.Parse("ffffffff-ffff-7000-8000-000000000000").Timestamp.ShouldBeNull();
    }

    [TestMethod]
    public void RelativeDirectory_IsShardedByLow16BitsOfTimestamp()
    {
        var id = new FileIdGenerator(new ManualTimeProvider(_start)).CreateSequential();
        string idString = id.ToString();
        string low16 = (_startMs & 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);

        idString[9..13].ShouldBe(low16);
        id.RelativeDirectory.ShouldBe(DirectoryPath.ParseRelative($"{low16[..2]}/{low16[2..]}/{idString}", PathFormat.Universal, PathOptions.None));
    }

    [TestMethod]
    public void FileIdCreateSequential_ReturnsIncreasingVersion7Ids()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => FileId.CreateSequential()).ToList();

        foreach (var id in ids)
            GetVersion(id).ShouldBe(7);

        ShouldBeStrictlyIncreasing(ids);
    }

    [TestMethod]
    public async Task ConcurrentCallers_GetDistinctIds()
    {
        var generator = new FileIdGenerator();

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => {
                var ids = new FileId[10_000];

                for (int i = 0; i < ids.Length; i++)
                    ids[i] = generator.CreateSequential();

                return ids;
            },
            TestContext.CancellationToken));

        var results = await Task.WhenAll(tasks);
        var all = results.SelectMany(ids => ids).ToList();

        all.Select(id => id.Guid).Distinct().Count().ShouldBe(80_000);

        // The random bits alone would make the IDs distinct; the lock is what hands each timestamp and counter pair out once.
        all.Select(id => (GetTimestampMilliseconds(id), GetCounter(id))).Distinct().Count().ShouldBe(80_000);

        foreach (var ids in results)
            ShouldBeStrictlyIncreasing(ids);
    }

    [TestMethod]
    public async Task SequentialRepo_AddsIncreasingVersion7Ids()
    {
        var repoDir = NewRepoDir();
        var repo = new FileRepo(repoDir, RepoFormatVersion.Latest, o => {
            o.FileIdMode = FileIdMode.Sequential;
            o.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });
        repo.EnsureCreated();

        try
        {
            var before = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var ids = new List<FileId>();

            await using (var txn = await repo.BeginTransactionAsync())
            {
                for (int i = 0; i < 3; i++)
                {
                    await using var source = new MemoryStream([1, 2, 3]);
                    var added = await txn.AddAsync(source, ".bin", leaveOpen: true, FileProcessingPipeline.Empty, TestContext.CancellationToken);
                    ids.Add(added.FileId);
                }

                await txn.CommitAsync(TestContext.CancellationToken);
            }

            var after = DateTimeOffset.UtcNow;

            foreach (var id in ids)
            {
                GetVersion(id).ShouldBe(7);
                id.Timestamp.ShouldNotBeNull().ShouldBeInRange(before, after);
            }

            ShouldBeStrictlyIncreasing(ids);
        }
        finally
        {
            repo.Dispose();
        }
    }

    // Asserts that each ID is greater than the one before it both by ordinal comparison of the D strings and by Guid.CompareTo.
    private static void ShouldBeStrictlyIncreasing(IReadOnlyList<FileId> ids)
    {
        for (int i = 1; i < ids.Count; i++)
        {
            ids[i].ToString().ShouldBeGreaterThan(ids[i - 1].ToString(), StringComparer.Ordinal);
            ids[i].Guid.ShouldBeGreaterThan(ids[i - 1].Guid);
        }
    }

    // The field helpers below read the ID's bytes in RFC 9562 order.
    private static byte[] GetBytes(FileId id) => id.Guid.ToByteArray(bigEndian: true);

    private static long GetTimestampMilliseconds(FileId id)
    {
        byte[] b = GetBytes(id);
        return ((long)b[0] << 40) | ((long)b[1] << 32) | ((long)b[2] << 24) | ((long)b[3] << 16) | ((long)b[4] << 8) | b[5];
    }

    private static int GetVersion(FileId id) => GetBytes(id)[6] >> 4;

    private static int GetCounter(FileId id) => GetBytes(id)[6] & 0x0F;

    private static int GetVariantBits(FileId id) => GetBytes(id)[8] >> 6;

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_FileIdGenerator_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
