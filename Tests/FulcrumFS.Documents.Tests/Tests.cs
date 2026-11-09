using System.Diagnostics;
using System.Runtime.Versioning;
using PrefixClassName.MsTest;
using Shouldly;
using Singulink.IO;

namespace FulcrumFS.Documents;

[PrefixTestClass]
public sealed class Tests
{
    public required TestContext TestContext { get; set; }

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _samplesDir = _appDir.CombineDirectory("SampleFiles");
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot");

    private static readonly FileRepo _repo = new(_repoDir, RepoFormatVersion.Latest, options => {
        options.DeleteMode = DeleteMode.Immediate;
        options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
    });

    private static readonly Dictionary<string, FileFormat> _formatsByExtension = new() {
        [".doc"] = FileFormat.Doc,
        [".docx"] = FileFormat.Docx,
        [".xls"] = FileFormat.Xls,
        [".xlsx"] = FileFormat.Xlsx,
        [".xlsm"] = FileFormat.Xlsm,
        [".ppt"] = FileFormat.Ppt,
        [".pptx"] = FileFormat.Pptx,
        [".odt"] = FileFormat.Odt,
        [".ods"] = FileFormat.Ods,
        [".odp"] = FileFormat.Odp,
        [".rtf"] = FileFormat.Rtf,
    };

    private static bool _initialized;

    [TestMethod]
    [DataRow(".doc")]
    [DataRow(".docx")]
    [DataRow(".xls")]
    [DataRow(".xlsx")]
    [DataRow(".xlsm")]
    [DataRow(".ppt")]
    [DataRow(".pptx")]
    [DataRow(".odt")]
    [DataRow(".ods")]
    [DataRow(".odp")]
    [DataRow(".rtf")]
    public async Task ConvertToPdf(string extension)
    {
        ResetRepository();

        var sourceFile = _samplesDir.CombineFile("sample" + extension);
        var validationPipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(_formatsByExtension[extension])).ToPipeline();

        FileId fileId;

        await using (var stream = sourceFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await using var txn = await _repo.BeginTransactionAsync();

            var added = await txn.AddAsync(stream, extension, leaveOpen: true, validationPipeline, TestContext.CancellationToken);
            await _repo.AddVariantAsync(
                added.FileId,
                "pdf",
                new DocumentPdfConversionProcessor(DocumentPdfConversionProcessingOptions.Standard).ToPipeline(),
                TestContext.CancellationToken);

            await txn.CommitAsync(TestContext.CancellationToken);
            fileId = added.FileId;
        }

        var pdfPath = (await _repo.GetVariantAsync(fileId, "pdf")).Path;
        pdfPath.Extension.ShouldBe(".pdf");

        await using var pdfStream = pdfPath.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var result = await FileFormat.Pdf.ValidateAsync(pdfStream, TestContext.CancellationToken);
        result.IsValid.ShouldBeTrue(result.ErrorMessage);
    }

    [TestMethod]
    public async Task UnsupportedExtension()
    {
        ResetRepository();

        var pipeline = new DocumentPdfConversionProcessor(DocumentPdfConversionProcessingOptions.Standard).ToPipeline();

        await using var stream = new MemoryStream("Plain text content."u8.ToArray());

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, ".txt", leaveOpen: true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldStartWith("Extension '.txt' is not allowed.");
    }

    [TestMethod]
    public async Task InvalidDocumentContent()
    {
        ResetRepository();

        var pipeline = new DocumentPdfConversionProcessor(DocumentPdfConversionProcessingOptions.Standard).ToPipeline();

        // Note: LibreOffice leniently imports plain text content as a text document regardless of the extension, so unloadable content requires something
        // like a corrupt ZIP archive (which the .docx import filter fails to open and no fallback filter can load either).
        await using var stream = new MemoryStream([0x50, 0x4B, 0x03, 0x04, .. new byte[256]]);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, ".docx", leaveOpen: true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldStartWith("Failed to convert the document to PDF.");
    }

    [TestMethod]
    public void CustomSourceFormatsRestrictAllowedExtensions()
    {
        var processor = new DocumentPdfConversionProcessor(new() {
            SourceFormats = [FileFormat.Docx, FileFormat.Odt],
        });

        processor.AllowedFileExtensions.ShouldBe([".docx", ".odt"]);
    }

    [TestMethod]
    public void ConfigurationOptionsOutOfRange_ReportValue()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(() => new DocumentPdfConversionConfigurationOptions { MaxConcurrentProcesses = 0 });
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldStartWith("The value is less than 1. (Parameter 'value')");

        ex = Should.Throw<ArgumentOutOfRangeException>(() => new DocumentPdfConversionConfigurationOptions { ProcessPriorityClass = (ProcessPriorityClass)42 });
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldStartWith("The value is not a defined ProcessPriorityClass value. (Parameter 'value')");

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            ex = Should.Throw<ArgumentOutOfRangeException>(SetProcessorAffinityToZero);
            ex.ParamName.ShouldBe("value");
            ex.Message.ShouldStartWith("The value is zero. (Parameter 'value')");
        }
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    private static void SetProcessorAffinityToZero() => _ = new DocumentPdfConversionConfigurationOptions { ProcessorAffinity = 0 };

    [TestMethod]
    public void NullSourceFormat()
    {
        var ex = Should.Throw<ArgumentException>(() => new DocumentPdfConversionProcessingOptions {
            SourceFormats = [FileFormat.Docx, null!],
        });

        ex.Message.ShouldStartWith("The value contains null.");
    }

    [TestMethod]
    public void EmptySourceFormats()
    {
        var ex = Should.Throw<ArgumentException>(() => new DocumentPdfConversionProcessingOptions {
            SourceFormats = [],
        });

        ex.Message.ShouldStartWith("The value is empty.");
    }

    private static void ResetRepository()
    {
        lock (_repo)
        {
            if (_initialized)
                return;

            _initialized = true;

            if (_repoDir.Exists)
                _repoDir.Delete(true);

            _repoDir.Create();
            _repo.EnsureCreated();
        }
    }
}
