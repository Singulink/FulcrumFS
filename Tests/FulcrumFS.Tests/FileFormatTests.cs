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
        [FileFormat.Xlsm, "sample.xlsm"],
        [FileFormat.Pptx, "sample.pptx"],
        [FileFormat.Odt, "sample.odt"],
        [FileFormat.Ods, "sample.ods"],
        [FileFormat.Odp, "sample.odp"],
        [FileFormat.Epub, "sample.epub"],
        [FileFormat.Step, "sample.step"],
        [FileFormat.Step, "sample.stp"],
        [FileFormat.SolidWorksPart, "sample.sldprt"],
        [FileFormat.SolidWorksAssembly, "sample.sldasm"],
        [FileFormat.SolidWorksDrawing, "sample.slddrw"],
        [FileFormat.EDrawingsAssembly, "sample.easm"],
        [FileFormat.Dxf, "sample.dxf"],
        [FileFormat.Dwg, "sample.dwg"],
        [FileFormat.Iges, "sample.igs"],
        [FileFormat.Gerber, "sample.gbr"],
        [FileFormat.GerberJob, "sample.gbrjob"],
        [FileFormat.ExcellonDrill, "sample.drl"],
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
        // The mp4 loose format does not check the brand, but it should still not accept the ts sample.
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
            FileFormat.Step, FileFormat.SolidWorksPart, FileFormat.SolidWorksAssembly, FileFormat.EDrawingsAssembly,
            FileFormat.SolidWorksDrawing, FileFormat.Dxf, FileFormat.Dwg, FileFormat.Iges,
            FileFormat.Gerber, FileFormat.GerberJob, FileFormat.ExcellonDrill,
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

    #region CAD

    // Positive cases for the CAD formats are covered by BuiltInType_ValidSample_Succeeds via the STEPcode project samples (STEP) and real SolidWorks /
    // eDrawings files (see the SampleFiles readme for provenance). Modern SolidWorks documents use a proprietary container with nibble-swapped entry names,
    // legacy ones use OLE Compound Documents (both containers are accepted), and modern eDrawings documents are ZIP archives with an 'eModel' entry.

    [TestMethod]
    public async Task Step_LeadingWhitespace_Succeeds()
    {
        await using var stream = new MemoryStream("\r\n  ISO-10303-21;\nHEADER;"u8.ToArray());
        var result = await FileFormat.Step.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Step_Utf8Bom_Succeeds()
    {
        await using var stream = new MemoryStream([0xEF, 0xBB, 0xBF, .. "ISO-10303-21;\nHEADER;"u8]);
        var result = await FileFormat.Step.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Step_PlainText_Fails()
    {
        await using var stream = new MemoryStream("This is just a text file, not a STEP model."u8.ToArray());
        var result = await FileFormat.Step.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("STEP");
    }

    [TestMethod]
    public async Task SolidWorksPart_DocBytes_Fails()
    {
        // A Word document is a valid OLE container, so this exercises the known-OLE-type rejection on the legacy container path.
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.doc").PathExport);
        var result = await FileFormat.SolidWorksPart.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Doc");
    }

    [TestMethod]
    public async Task SolidWorksAssembly_XlsBytes_Fails()
    {
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.xls").PathExport);
        var result = await FileFormat.SolidWorksAssembly.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Xls");
    }

    [TestMethod]
    public async Task EDrawingsAssembly_ZipBytes_Fails()
    {
        // A plain ZIP archive is a valid container but lacks the required 'eModel' entry.
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.zip").PathExport);
        var result = await FileFormat.EDrawingsAssembly.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("eModel");
    }

    [TestMethod]
    public async Task EDrawingsAssembly_HsfBytes_Succeeds()
    {
        // Legacy eDrawings documents (and SolidWorks Simulation analysis exports) are raw HOOPS Stream Format files, which open with a version comment.
        await using var stream = new MemoryStream([.. ";; HSF V19.10 \n"u8, 0x49, 0x0C, 0x90, 0x03, 0x00, 0x42, 0x00]);
        var result = await FileFormat.EDrawingsAssembly.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task EDrawingsAssembly_UnknownBytes_Fails()
    {
        await using var stream = new MemoryStream("This is neither a HOOPS Stream Format file nor a ZIP archive."u8.ToArray());
        var result = await FileFormat.EDrawingsAssembly.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("HOOPS");
    }

    // 'Contents' in ASCII with the high/low nibbles of each byte swapped (the modern SolidWorks container's entry-name encoding).
    private static readonly byte[] NibbleSwappedContents = [0x34, 0xF6, 0xE6, 0x47, 0x56, 0xE6, 0x47, 0x37];

    [TestMethod]
    public async Task SolidWorksPart_ContentsEntryBehindLargePreview_Succeeds()
    {
        // Real documents can carry tens of KB of preview images before the first 'Contents' entry, so the search must not stop after the first few KB.
        byte[] bytes = [0x84, 0xB6, 0x22, 0x08, 0x00, 0x00, 0x00, 0x04, .. new byte[300_000], .. NibbleSwappedContents, .. new byte[64]];
        await using var stream = new MemoryStream(bytes);
        var result = await FileFormat.SolidWorksPart.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SolidWorksPart_ContentsEntryAcrossReadBoundary_Succeeds()
    {
        // The 'Contents' name straddles the 64 KB read boundary of the search, which carries the tail of each read over.
        byte[] bytes = [0x84, 0xB6, 0x22, 0x08, 0x00, 0x00, 0x00, 0x04, .. new byte[65536 - 8 - 4], .. NibbleSwappedContents, .. new byte[1000]];
        await using var stream = new MemoryStream(bytes);
        var result = await FileFormat.SolidWorksPart.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SolidWorksPart_ModernPrefixWithoutContentsEntry_Fails()
    {
        byte[] bytes = [0x84, 0xB6, 0x22, 0x08, 0x00, 0x00, 0x00, 0x04, .. new byte[300_000]];
        await using var stream = new MemoryStream(bytes);
        var result = await FileFormat.SolidWorksPart.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Contents");
    }

    [TestMethod]
    public async Task SolidWorksPart_SldAsmBytes_Succeeds()
    {
        // Part and assembly documents cannot be reliably distinguished from each other at the container level, so either accepts the other's content.
        await using var stream = File.OpenRead(_sampleDir.CombineFile("sample.sldasm").PathExport);
        var result = await FileFormat.SolidWorksPart.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Dxf_BinarySentinel_Succeeds()
    {
        await using var stream = new MemoryStream([.. "AutoCAD Binary DXF\r\n"u8, 0x1A, 0x00]);
        var result = await FileFormat.Dxf.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Dxf_CommentThenSection_Succeeds()
    {
        await using var stream = new MemoryStream("999\nA comment\n  0\nSECTION\n  2\nHEADER\n"u8.ToArray());
        var result = await FileFormat.Dxf.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Dxf_PlainText_Fails()
    {
        await using var stream = new MemoryStream("This is just a text file, not a DXF drawing."u8.ToArray());
        var result = await FileFormat.Dxf.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("DXF");
    }

    [TestMethod]
    public async Task Dwg_TruncatedSignature_Fails()
    {
        await using var stream = new MemoryStream("AC1"u8.ToArray());
        var result = await FileFormat.Dwg.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("DWG");
    }

    [TestMethod]
    public async Task SolidWorksPart_GarbageBytes_Fails()
    {
        await using var stream = new MemoryStream(new byte[8 * 1024]);
        var result = await FileFormat.SolidWorksPart.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    #endregion

    #region PCB

    // Positive cases for Gerber and Excellon are covered by BuiltInType_ValidSample_Succeeds via the gerbv project
    // samples; the Gerber job sample is a hand-built minimal spec-conformant file (see the SampleFiles readme).

    [TestMethod]
    public async Task Gerber_MissingHeaderCommands_Fails()
    {
        await using var stream = new MemoryStream("G04 A comment*\nX0Y0D02*\nM02*\n"u8.ToArray());
        var result = await FileFormat.Gerber.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Gerber");
    }

    [TestMethod]
    public async Task GerberJob_JsonWithoutHeader_Fails()
    {
        await using var stream = new MemoryStream("""{ "GeneralSpecs": { "LayerNumber": 2 } }"""u8.ToArray());
        var result = await FileFormat.GerberJob.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Gerber job");
    }

    [TestMethod]
    public async Task Excellon_LeadingComments_Succeeds()
    {
        await using var stream = new MemoryStream("; generated by test\n\nM48\nINCH,TZ\nT1C0.028\n%\nM30\n"u8.ToArray());
        var result = await FileFormat.ExcellonDrill.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Excellon_MissingHeader_Fails()
    {
        await using var stream = new MemoryStream("T1C0.028\nX1Y1\nM30\n"u8.ToArray());
        var result = await FileFormat.ExcellonDrill.ValidateAsync(stream, TestContext.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("M48");
    }

    #endregion

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
