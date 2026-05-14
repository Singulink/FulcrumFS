using System.Text;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FactoryFileFormatTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AnyContent_AcceptsAnything()
    {
        var type = FileFormat.AnyContent(".log");
        type.Name.ShouldBe("AnyContent");
        type.Extensions.ShouldBe([".log"]);

        await using var stream = new MemoryStream([0x00, 0xFF, 0x42]);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task AnyContent_EmptyExtensions_Throws()
    {
        Should.Throw<ArgumentException>(() => FileFormat.AnyContent());
    }

    [TestMethod]
    public async Task TextAscii_PureAscii_Succeeds()
    {
        var type = FileFormat.TextAscii(".txt");
        type.Name.ShouldBe("TextAscii");

        await using var stream = new MemoryStream("Hello, World!\r\nLine 2\n"u8.ToArray());
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TextAscii_HighByte_Fails()
    {
        var type = FileFormat.TextAscii(".txt");
        await using var stream = new MemoryStream([0x48, 0x65, 0xFF, 0x6C, 0x6F]);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("ASCII");
    }

    [TestMethod]
    public async Task TextUnicode_Utf8NoBom_Succeeds()
    {
        var type = FileFormat.TextUnicode(".txt");
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, cafÃ©! æ—¥æœ¬èªž"));
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TextUnicode_InvalidUtf8_Fails()
    {
        var type = FileFormat.TextUnicode(".txt");

        // Invalid UTF-8: lone continuation byte.
        await using var stream = new MemoryStream([0x48, 0x65, 0x80, 0x6C, 0x6F]);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task TextUnicode_TruncatedUtf8AtEnd_Fails()
    {
        var type = FileFormat.TextUnicode(".txt");

        // "ab" followed by the first byte of a 3-byte UTF-8 sequence (0xE6) and the first continuation byte (0x97) but missing the final continuation byte.
        await using var stream = new MemoryStream([0x61, 0x62, 0xE6, 0x97]);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("UTF-8");
    }

    [TestMethod]
    public async Task TextUnicode_Utf8WithBom_Succeeds()
    {
        var type = FileFormat.TextUnicode(".txt");
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "Hello"u8];
        await using var stream = new MemoryStream(bytes);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TextUnicode_Utf16LeBom_Succeeds()
    {
        var type = FileFormat.TextUnicode(".txt");
        byte[] payload = Encoding.Unicode.GetBytes("Hello, cafÃ©!");
        byte[] bytes = [0xFF, 0xFE, .. payload];
        await using var stream = new MemoryStream(bytes);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TextUnicode_Utf16BeBom_Succeeds()
    {
        var type = FileFormat.TextUnicode(".txt");
        byte[] payload = Encoding.BigEndianUnicode.GetBytes("Hello, cafÃ©!");
        byte[] bytes = [0xFE, 0xFF, .. payload];
        await using var stream = new MemoryStream(bytes);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TextUnicode_Utf32LeBom_Succeeds()
    {
        var type = FileFormat.TextUnicode(".txt");
        var utf32 = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
        byte[] payload = utf32.GetBytes("Hello");
        byte[] bytes = [0xFF, 0xFE, 0x00, 0x00, .. payload];
        await using var stream = new MemoryStream(bytes);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TextUnicode_DisallowedEncoding_Fails()
    {
        var type = FileFormat.TextUnicode(UnicodeEncodings.Utf16Le | UnicodeEncodings.Utf16Be, ".txt");

        // No-BOM file should fail (UTF-8 isn't allowed) even if content would be valid UTF-8.
        await using var stream = new MemoryStream("Hello"u8.ToArray());
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("BOM");
    }

    [TestMethod]
    public async Task TextUnicode_BomNotInAllowedSet_Fails()
    {
        var type = FileFormat.TextUnicode(UnicodeEncodings.Utf8, ".txt");

        byte[] payload = Encoding.Unicode.GetBytes("Hi");
        byte[] bytes = [0xFF, 0xFE, .. payload];
        await using var stream = new MemoryStream(bytes);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("UTF-16 LE");
    }

    [TestMethod]
    public void TextUnicode_NoEncodings_Throws()
    {
        Should.Throw<ArgumentException>(() => FileFormat.TextUnicode(UnicodeEncodings.None, ".txt"));
    }

    [TestMethod]
    public async Task TextEncoding_ValidLatin1_Succeeds()
    {
        var type = FileFormat.TextEncoding(Encoding.Latin1, ".txt");
        type.Name.ShouldContain("iso-8859-1");

        await using var stream = new MemoryStream([0x48, 0x65, 0xFF, 0x6C, 0x6F]); // Valid Latin-1.
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TextEncoding_InvalidAscii_Fails()
    {
        var type = FileFormat.TextEncoding(Encoding.ASCII, ".txt");
        await using var stream = new MemoryStream([0x48, 0xFF, 0x6C]); // 0xFF not valid ASCII.
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public void TextEncoding_NullEncoding_Throws()
    {
        Should.Throw<ArgumentNullException>(() => FileFormat.TextEncoding(null!, ".txt"));
    }

    [TestMethod]
    public void Factories_NormalizeExtensions()
    {
        var type = FileFormat.AnyContent(".LOG", ".Txt");

        // Extensions should be lowercased + dot-prefixed (FileExtension.Normalize behavior).
        type.Extensions.ShouldAllBe(e => e.StartsWith('.') && e == e.ToLowerInvariant());
    }
}
