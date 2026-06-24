using System.IO.Compression;

namespace FulcrumFS;

/// <summary>
/// Tests for every built-in <see cref="FileFormat"/> singleton. Each test feeds the real sample file to <see cref="FileFormat.ValidateAsync"/> and asserts
/// success. Negative tests feed mismatched content and assert failure with a non-empty error message. ZIP-family negative cases (missing required entries,
/// wrong mimetype) are constructed at runtime via <see cref="ZipArchive"/> since they cannot be expressed as real Office/OpenDocument files.
/// </summary>
[PrefixTestClass]
public sealed class FileFormatTests
{
    private static readonly IAbsoluteDirectoryPath _sampleDir = DirectoryPath.GetAppBase().CombineDirectory("SampleFiles");

    public required TestContext TestContext { get; set; }

    public static IEnumerable<object[]> RealSamples =>
    [
        [FileFormat.Jpeg, "sample.jpg"],
        [FileFormat.Png, "sample.png"],
        [FileFormat.Gif, "sample.gif"],
        [FileFormat.WebP, "sample.webp"],
        [FileFormat.Bmp, "sample.bmp"],
        [FileFormat.Tiff, "sample.tif"],
        [FileFormat.Heic, "sample.heic"],
        [FileFormat.Heif, "sample.heif"],
        [FileFormat.Avif, "sample.avif"],
        [FileFormat.Mp4, "sample.mp4"],
        [FileFormat.Mov, "sample.mov"],
        [FileFormat.M4a, "sample.m4a"],
        [FileFormat.Tgp, "sample.3gp"],
        [FileFormat.Tg2, "sample.3g2"],
        [FileFormat.Mj2, "sample.mj2"],
        [FileFormat.Mp4Loose, "sample.mp4"],
        [FileFormat.Mp4Loose, "sample.mov"],
        [FileFormat.Mp4Loose, "sample.m4a"],
        [FileFormat.Mp4Loose, "sample.3gp"],
        [FileFormat.Mp4Loose, "sample.3g2"],
        [FileFormat.Mp4Loose, "sample.mj2"],
        [FileFormat.Mkv, "sample.mkv"],
        [FileFormat.WebM, "sample.webm"],
        [FileFormat.Ts, "sample.ts"],
        [FileFormat.M2ts, "sample.m2ts"],
        [FileFormat.Avi, "sample.avi"],
        [FileFormat.Mpeg, "sample.mpeg"],
        [FileFormat.Wav, "sample.wav"],
        [FileFormat.Mp3, "sample.mp3"],
        [FileFormat.Flac, "sample.flac"],
        [FileFormat.Ogg, "sample.ogg"],
        [FileFormat.Pdf, "sample.pdf"],
        [FileFormat.Rtf, "sample.rtf"],
        [FileFormat.Doc, "sample.doc"],
        [FileFormat.Xls, "sample.xls"],
        [FileFormat.Ppt, "sample.ppt"],
        [FileFormat.Zip, "sample.zip"],
        [FileFormat.Docx, "sample.docx"],
        [FileFormat.Xlsx, "sample.xlsx"],
        [FileFormat.Pptx, "sample.pptx"],
        [FileFormat.Odt, "sample.odt"],
        [FileFormat.Ods, "sample.ods"],
        [FileFormat.Odp, "sample.odp"],
        [FileFormat.Epub, "sample.epub"],
    ];

    [TestMethod]
    [DynamicData(nameof(RealSamples))]
    public async Task BuiltInType_ValidSample_Succeeds(FileFormat type, string fileName)
    {
        var path = _sampleDir.CombineFile(fileName);

        await using var stream = File.OpenRead(path.PathExport);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);

        result.IsValid.ShouldBeTrue($"Expected {type.Name} to accept {fileName} but got: {result.ErrorMessage}");
        result.ErrorMessage.ShouldBeNull();
    }

    [TestMethod]
    [DynamicData(nameof(RealSamples))]
    public async Task BuiltInType_GarbageBytes_Fails(FileFormat type, string fileName)
    {
        _ = fileName;

        // Use 8 KiB of zero bytes - won't match any real magic/structure.
        await using var stream = new MemoryStream(new byte[8 * 1024]);
        var result = await type.ValidateAsync(stream, TestContext.CancellationToken);

        result.IsValid.ShouldBeFalse($"Expected {type.Name} to reject garbage bytes.");
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task Jpeg_PngBytes_Fails()
    {
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.png").PathExport);
        var result = await FileFormat.Jpeg.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Mp4_MovBytes_Fails()
    {
        // The mp4 brand set does not include 'qt  ', and the mov sample uses major_brand 'qt  '.
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.mov").PathExport);
        var result = await FileFormat.Mp4.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Mp4Loose_TsBytes_Fails()
    {
        // The mp4 loose format does not check the brand, so it should accept the ts sample.
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.ts").PathExport);
        var result = await FileFormat.Mp4Loose.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Ts_M2tsBytes_Fails()
    {
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.m2ts").PathExport);
        var result = await FileFormat.Ts.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task M2ts_TsBytes_Fails()
    {
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.ts").PathExport);
        var result = await FileFormat.M2ts.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Doc_XlsBytes_Fails()
    {
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.xls").PathExport);
        var result = await FileFormat.Doc.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Xls");
    }

    [TestMethod]
    public async Task Xls_DocBytes_Fails()
    {
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.doc").PathExport);
        var result = await FileFormat.Xls.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Doc");
    }

    [TestMethod]
    public async Task Ppt_DocBytes_Fails()
    {
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.doc").PathExport);
        var result = await FileFormat.Ppt.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Doc");
    }

    [TestMethod]
    public async Task FileFormat_NameAndExtensions_AreNonEmpty()
    {
        // Sanity check every built-in type has a name and at least one extension and ToString returns the name.
        FileFormat[] all =
        [
            FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.WebP, FileFormat.Bmp, FileFormat.Tiff,
            FileFormat.Heic, FileFormat.Heif, FileFormat.Avif,
            FileFormat.Mp4Loose, FileFormat.Mp4, FileFormat.Mov, FileFormat.M4a, FileFormat.Tgp, FileFormat.Tg2, FileFormat.Mj2,
            FileFormat.Mkv, FileFormat.WebM,
            FileFormat.Ts, FileFormat.M2ts,
            FileFormat.Avi, FileFormat.Mpeg, FileFormat.Wav, FileFormat.Mp3, FileFormat.Flac, FileFormat.Ogg,
            FileFormat.Pdf, FileFormat.Rtf, FileFormat.Doc, FileFormat.Xls, FileFormat.Ppt,
            FileFormat.Zip, FileFormat.Docx, FileFormat.Xlsx, FileFormat.Pptx,
            FileFormat.Odt, FileFormat.Ods, FileFormat.Odp, FileFormat.Epub,
        ];

        foreach (FileFormat type in all)
        {
            type.Name.ShouldNotBeNullOrEmpty();
            type.Extensions.ShouldNotBeEmpty();
            type.PrimaryExtension.ShouldBe(type.Extensions[0]);
            type.ToString().ShouldBe(type.Name);

            foreach (string ext in type.Extensions)
                ext.ShouldStartWith(".", customMessage: $"Extension '{ext}' on {type.Name} must include leading dot.");
        }
    }

    #region ZIP-family

    // Positive cases for the ZIP-family formats are covered by BuiltInType_ValidSample_Succeeds via the real samples in SampleFiles. The negative cases
    // below (missing required entries, wrong mimetype) must be synthesized because a real Office/EPUB file with a missing required entry or wrong mimetype
    // isn't something Office or an EPUB authoring tool produces.

    [TestMethod]
    public async Task Zip_NonZipBytes_Fails()
    {
        await using var stream = new MemoryStream(new byte[64]);
        var result = await FileFormat.Zip.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Docx_MissingContentTypes_Fails()
    {
        await using var zip = CreateZip(("word/document.xml", "<doc/>"u8.ToArray()));
        var result = await FileFormat.Docx.ValidateAsync(zip, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("[Content_Types].xml");
    }

    [TestMethod]
    public async Task Docx_MissingDocumentPart_Fails()
    {
        await using var zip = CreateZip(("[Content_Types].xml", "<types/>"u8.ToArray()));
        var result = await FileFormat.Docx.ValidateAsync(zip, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("word/document.xml");
    }

    [TestMethod]
    public async Task Odt_MissingMimetype_Fails()
    {
        await using var zip = CreateZip(("content.xml", "<x/>"u8.ToArray()));
        var result = await FileFormat.Odt.ValidateAsync(zip, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("mimetype");
    }

    [TestMethod]
    public async Task Odt_WrongMimetype_Fails()
    {
        await using var zip = CreateZip(("mimetype", "application/epub+zip"u8.ToArray()));
        var result = await FileFormat.Odt.ValidateAsync(zip, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("application/epub+zip");
    }

    private static MemoryStream CreateZip(params (string Name, byte[] Content)[] entries)
    {
        var ms = new MemoryStream();

        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using Stream s = entry.Open();
                s.Write(content);
            }
        }

        ms.Position = 0;
        return ms;
    }

    #endregion
}
