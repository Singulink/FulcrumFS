namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileIdTests
{
    // A version 7 ID with hex letters in every group, so that uppercasing any group changes it.
    private const string LowercaseId = "0192a3b4-c5d6-7e8f-9abc-def012345678";

    [TestMethod]
    public void EqualityOperators_AcceptNullOperands()
    {
        // Frameworks compare values generically (EF Core compares every reference-type property with null through the operator
        // when it takes original values), so the operators must take null without throwing.

        FileId? none = null;
        var id = FileId.Parse(LowercaseId);

        (none == null).ShouldBeTrue();
        (none != null).ShouldBeFalse();
        (id == null).ShouldBeFalse();
        (none == id).ShouldBeFalse();
        (id != null).ShouldBeTrue();
        (id == FileId.Parse(LowercaseId)).ShouldBeTrue();
        id.Equals(null).ShouldBeFalse();
    }

    [TestMethod]
    public void TryParseUnsafe_LowercaseD_IsAccepted()
    {
        FileId.TryParseUnsafe(LowercaseId, out var fileId).ShouldBeTrue();

        fileId.ShouldBe(FileId.Parse(LowercaseId));
        fileId.ToString().ShouldBe(LowercaseId);
    }

    [TestMethod]
    [DataRow("0192A3B4-C5D6-7E8F-9ABC-DEF012345678")]
    [DataRow("0192a3b4-c5d6-7e8f-9abc-dEf012345678")]
    [DataRow("{0192a3b4-c5d6-7e8f-9abc-def012345678}")]
    [DataRow("(0192a3b4-c5d6-7e8f-9abc-def012345678)")]
    [DataRow("0192a3b4c5d67e8f9abcdef012345678")]
    [DataRow(" 0192a3b4-c5d6-7e8f-9abc-def012345678")]
    [DataRow("0192a3b4-c5d6-7e8f-9abc-def012345678 ")]
    [DataRow("0192a3b4-c5d6-1e8f-9abc-def012345678")] // A version 1 GUID.
    [DataRow("")]
    public void TryParseUnsafe_AnyOtherFormatOrCasing_IsRejected(string s)
    {
        FileId.TryParseUnsafe(s, out var fileId).ShouldBeFalse();
        fileId.ShouldBeNull();
    }

    [TestMethod]
    public void TryParseUnsafe_Null_IsRejected()
    {
        FileId.TryParseUnsafe(null, out var fileId).ShouldBeFalse();
        fileId.ShouldBeNull();
    }

    [TestMethod]
    public void TryParse_Uppercase_IsAccepted()
    {
        // The public parser accepts any casing, unlike the one used for repository names.
        FileId.TryParse(LowercaseId.ToUpperInvariant(), out var fileId).ShouldBeTrue();
        fileId.ToString().ShouldBe(LowercaseId);
    }

    [TestMethod]
    [DataRow(LowercaseId, "70192a3b4c5d6e8f9abcdef012345678")]
    [DataRow("f47ac10b-58cc-4372-a567-0e02b2c3d479", "4f47ac10b58cc372a5670e02b2c3d479")] // A version 4 ID.
    public void OrderedBytes_KnownId_MatchesFixedVector(string id, string expectedHex)
    {
        // Fixed vectors, so that the stored format never drifts: the version nibble first, then the timestamp, then the rest of the nibbles in place.
        byte[] expected = Convert.FromHexString(expectedHex);
        var fileId = FileId.Parse(id);

        fileId.ToOrderedBytes().ShouldBe(expected);
        FileId.FromOrderedBytes(expected).ShouldBe(fileId);
    }

    [TestMethod]
    public void OrderedBytes_RandomVersion4AndVersion7Ids_RoundTrip()
    {
        var random = new Random(1);
        var ids = Enumerable.Range(0, 1000).Select(_ => FileId.CreateSecure())
            .Concat(Enumerable.Range(0, 1000).Select(_ => RandomVersion7(random)))
            .ToList();

        Span<byte> buffer = stackalloc byte[16];

        foreach (var id in ids)
        {
            byte[] bytes = id.ToOrderedBytes();
            FileId.FromOrderedBytes(bytes).Guid.ShouldBe(id.Guid);

            id.ToOrderedBytes(buffer);
            buffer.ToArray().ShouldBe(bytes);
        }
    }

    [TestMethod]
    public void OrderedBytes_EveryVersion7_SortsAfterEveryVersion4()
    {
        var random = new Random(2);

        // Version 7 IDs with random timestamps over the whole 48-bit range, against version 4 IDs whose first bytes are uniform as well.
        for (int i = 0; i < 1000; i++)
            CompareOrdinal(RandomVersion7(random).ToOrderedBytes(), FileId.CreateSecure().ToOrderedBytes()).ShouldBeGreaterThan(0);

        // The extremes: the smallest version 7 ID (timestamp 0) against the largest version 4 ID (first byte 0xFF), which sort the other way in RFC 9562 byte
        // order.
        var smallestVersion7 = FileId.Parse("00000000-0000-7000-8000-000000000000");
        var largestVersion4 = FileId.Parse("ffffffff-ffff-4fff-bfff-ffffffffffff");

        CompareOrdinal(smallestVersion7.ToOrderedBytes(), largestVersion4.ToOrderedBytes()).ShouldBeGreaterThan(0);
        CompareOrdinal(smallestVersion7.Guid.ToByteArray(bigEndian: true), largestVersion4.Guid.ToByteArray(bigEndian: true)).ShouldBeLessThan(0);
    }

    [TestMethod]
    public void OrderedBytes_IdsFromOneGenerator_StrictlyIncreasing()
    {
        // The clock starts 40 ms before a multiple of 2^16 ms, so that the timestamp carries across the bytes of both encodings during the run.
        long boundaryMs = ((new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() >> 16) + 1) << 16;
        var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(boundaryMs - 40));
        var generator = new FileIdGenerator(clock);
        var random = new Random(3);
        var ids = new List<FileId>(10_000);
        int counterOverflows = 0;
        int aheadOfClock = 0;

        while (ids.Count < 10_000)
        {
            // Bursts of up to 40 IDs within one clock millisecond run the counter through all 16 values and move the timestamp ahead of the clock, and the
            // clock then advances by 0 to 3 ms, so it sometimes stays behind the generator's timestamp.
            int burst = random.Next(1, 41);

            for (int i = 0; i < burst && ids.Count < 10_000; i++)
            {
                var id = generator.CreateSequential();

                if ((id.Guid.ToByteArray(bigEndian: true)[6] & 0x0F) is 15)
                    counterOverflows++;

                if (id.Timestamp > clock.UtcNow)
                    aheadOfClock++;

                ids.Add(id);
            }

            clock.Advance(TimeSpan.FromMilliseconds(random.Next(0, 4)));
        }

        counterOverflows.ShouldBeGreaterThan(0);
        aheadOfClock.ShouldBeGreaterThan(0);
        ids[0].Timestamp.ShouldNotBeNull().ToUnixTimeMilliseconds().ShouldBeLessThan(boundaryMs);
        ids[^1].Timestamp.ShouldNotBeNull().ToUnixTimeMilliseconds().ShouldBeGreaterThan(boundaryMs);

        for (int i = 1; i < ids.Count; i++)
            CompareOrdinal(ids[i].ToOrderedBytes(), ids[i - 1].ToOrderedBytes()).ShouldBeGreaterThan(0);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(15)]
    [DataRow(17)]
    public void FromOrderedBytes_WrongLength_Throws(int length)
    {
        Should.Throw<ArgumentException>(() => FileId.FromOrderedBytes(new byte[length]));
    }

    [TestMethod]
    public void FromOrderedBytes_InvalidVersion_Throws()
    {
        // The version nibble is the high nibble of the first byte: 1 is a version 1 GUID, which is not a file ID.
        byte[] bytes = FileId.Parse(LowercaseId).ToOrderedBytes();
        bytes[0] = (byte)(0x10 | (bytes[0] & 0x0F));

        Should.Throw<ArgumentException>(() => FileId.FromOrderedBytes(bytes));
    }

    [TestMethod]
    public void ToOrderedBytes_DestinationTooShort_Throws()
    {
        var id = FileId.Parse(LowercaseId);

        Should.Throw<ArgumentException>(() => id.ToOrderedBytes(new byte[15]));
    }

    [TestMethod]
    public void ToOrderedBytes_LongerDestination_WritesFirst16Bytes()
    {
        var id = FileId.Parse(LowercaseId);
        byte[] destination = Enumerable.Repeat((byte)0xEE, 20).ToArray();

        id.ToOrderedBytes(destination);

        destination[..16].ShouldBe(id.ToOrderedBytes());
        destination[16..].ShouldAllBe(b => b == 0xEE);
    }

    // A version 7 ID with a random timestamp over the whole 48-bit range and random counter and random bits.
    private static FileId RandomVersion7(Random random)
    {
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));

        return new FileId(new Guid(bytes, bigEndian: true));
    }

    // Compares the bytes as unsigned values, first byte first, as databases compare binary columns.
    private static int CompareOrdinal(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);
}
