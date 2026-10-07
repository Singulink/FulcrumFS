using System.Globalization;
using System.Text;
using FulcrumFS.Utilities;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class DeleteJournalTests
{
    private const string Header = "FULCRUMFS DELETE JOURNAL 1\n";
    private const int IdLineLength = 37;

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();

    private static int _counter;

    [TestMethod]
    public void Crc32_MatchesStandardCheckValue()
    {
        Crc32.Compute("123456789"u8).ShouldBe(0xCBF43926u);
        Crc32.Compute([]).ShouldBe(0u);
    }

    [TestMethod]
    public void FormatJournal_WritesHeaderIdLinesAndEndLine()
    {
        FileId[] fileIds = [FileId.CreateSequential(), FileId.CreateSecure()];

        byte[] content = RepoFileSystem.FormatJournal(fileIds);

        content.ShouldBe(BuildJournal(fileIds.Select(id => id.ToString())));
        Encoding.ASCII.GetString(content).ShouldStartWith(Header + fileIds[0] + "\n" + fileIds[1] + "\nEND 2 ");
    }

    [TestMethod]
    public void TryReadJournal_WellFormed_ReturnsIdsInOrder()
    {
        FileId[] fileIds = [FileId.CreateSequential(), FileId.CreateSecure(), FileId.CreateSequential()];
        var journal = WriteJournal(BuildJournal(fileIds.Select(id => id.ToString())));

        RepoFileSystem.TryReadJournal(journal, out var read).ShouldBeTrue();
        read.ShouldBe(fileIds);
    }

    [TestMethod]
    [DataRow("empty file")]
    [DataRow("header only")]
    [DataRow("wrong header")]
    [DataRow("no IDs")]
    [DataRow("truncated last line")]
    [DataRow("missing END line")]
    [DataRow("wrong count")]
    [DataRow("wrong CRC")]
    [DataRow("uppercase CRC")]
    [DataRow("changed character")]
    [DataRow("uppercase ID")]
    [DataRow("version 1 ID")]
    [DataRow("CRLF line endings")]
    [DataRow("trailing bytes")]
    [DataRow("zeroed block")]
    public void TryReadJournal_TornOrDamaged_ReturnsFalse(string damage)
    {
        string[] ids;

        // The checksum must contain a hex letter for the uppercase CRC case to change anything.
        do
        {
            ids = [.. Enumerable.Range(0, 4).Select(_ => FileId.CreateSecure().ToString())];
        }
        while (!ComputeCrc(ids).ToString("x8", CultureInfo.InvariantCulture).Any(char.IsAsciiLetter));

        byte[] valid = BuildJournal(ids);

        // The undamaged journal is accepted, so a rejection below is caused by the damage alone.
        RepoFileSystem.TryReadJournal(WriteJournal(valid), out _).ShouldBeTrue();

        byte[] content = damage switch
        {
            "empty file" => [],
            "header only" => Encoding.ASCII.GetBytes(Header),
            "wrong header" => [.. Encoding.ASCII.GetBytes("FULCRUMFS DELETE JOURNAL 2\n"), .. valid.AsSpan(Header.Length)],
            "no IDs" => BuildJournal([]),
            "truncated last line" => valid[..^3],
            "missing END line" => valid[..(Header.Length + (IdLineLength * ids.Length))],
            "wrong count" => BuildJournal(ids, count: ids.Length + 1),
            "wrong CRC" => BuildJournal(ids, crc: ComputeCrc(ids) ^ 1),
            "uppercase CRC" => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(valid).Replace(
                $" {ComputeCrc(ids):x8}\n", $" {ComputeCrc(ids):X8}\n", StringComparison.Ordinal)),
            "changed character" => ChangeHexDigit(valid, Header.Length + 35),
            "uppercase ID" => BuildJournal([ids[0], UpperCaseHexLetters(ids[1]), ids[2], ids[3]]),
            "version 1 ID" => BuildJournal([ids[0], "a1b2c3d4-e5f6-1789-8abc-def012345678", ids[2], ids[3]]),
            "CRLF line endings" => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(valid).Replace("\n", "\r\n", StringComparison.Ordinal)),
            "trailing bytes" => [.. valid, (byte)'\n'],
            "zeroed block" => ZeroBlock(valid, Header.Length + IdLineLength + 10, IdLineLength),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };

        content.SequenceEqual(valid).ShouldBeFalse();

        RepoFileSystem.TryReadJournal(WriteJournal(content), out var fileIds).ShouldBeFalse();
        fileIds.ShouldBeNull();
    }

    /// <summary>
    /// Builds journal content independently of the library's formatter, with an optional wrong count or checksum. The checksum is computed over the ID lines
    /// as given, so a malformed ID line is the only damage unless a wrong count or checksum is passed.
    /// </summary>
    private static byte[] BuildJournal(IEnumerable<string> ids, int? count = null, uint? crc = null)
    {
        string[] idArray = [.. ids];
        string endLine = string.Create(CultureInfo.InvariantCulture, $"END {count ?? idArray.Length} {crc ?? ComputeCrc(idArray):x8}\n");

        return Encoding.ASCII.GetBytes(Header + string.Concat(idArray.Select(id => id + "\n")) + endLine);
    }

    private static uint ComputeCrc(IEnumerable<string> ids) => Crc32.Compute(Encoding.ASCII.GetBytes(string.Concat(ids.Select(id => id + "\n"))));

    private static string UpperCaseHexLetters(string id)
    {
        // Make sure the ID has a letter to uppercase.
        string withLetter = id[..^1] + "a";
        return withLetter.ToUpperInvariant();
    }

    private static byte[] ChangeHexDigit(byte[] content, int index)
    {
        // The last character of the first ID changes to another hex digit, so the line still parses as a file ID and only the checksum can catch it.
        byte[] changed = [.. content];
        changed[index] = changed[index] is (byte)'0' ? (byte)'1' : (byte)'0';
        return changed;
    }

    private static byte[] ZeroBlock(byte[] content, int start, int length)
    {
        byte[] zeroed = [.. content];
        zeroed.AsSpan(start, length).Clear();
        return zeroed;
    }

    private static IAbsoluteFilePath WriteJournal(byte[] content)
    {
        var dir = _appDir.CombineDirectory(
            "DeleteJournal_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture), PathOptions.None);

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();

        var journal = dir.CombineFile(Guid.CreateVersion7().ToString("D") + FileRepoPaths.CommittedJournalExtension, PathOptions.None);
        File.WriteAllBytes(journal.PathExport, content);
        return journal;
    }
}
