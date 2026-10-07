namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileIdTests
{
    // A version 7 ID with hex letters in every group, so that uppercasing any group changes it.
    private const string LowercaseId = "0192a3b4-c5d6-7e8f-9abc-def012345678";

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
}
