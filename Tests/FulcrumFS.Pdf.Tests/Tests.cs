using PrefixClassName.MsTest;
using Shouldly;
using Singulink.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FulcrumFS.Pdf;

[PrefixTestClass]
public sealed class Tests
{
    public required TestContext TestContext { get; set; }

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot");

    private static readonly FileRepo _repo = new(_repoDir, options => {
        options.DeleteMode = DeleteMode.Immediate;
        options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
    });

    private static readonly FileProcessingPipeline _pdfValidationPipeline =
        new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Pdf)).ToPipeline();

    private static bool _initialized;

    [TestMethod]
    public async Task ExtractLandscapePage()
    {
        ResetRepository();

        var fileId = await AddPdfAsync(TestPdf.Create(400, 200), "thumbnail", new PdfImageExtractionProcessor(new() {
            MaxPixelSize = 800,
        }));

        var thumbnailPath = (await _repo.GetVariantAsync(fileId, "thumbnail")).Path;
        thumbnailPath.Extension.ShouldBe(".png");

        using var image = Image.Load<Rgba32>(thumbnailPath.PathExport);
        image.Width.ShouldBe(800);
        image.Height.ShouldBe(400);

        // Corners are white background; the center is covered by the gray rectangle drawn by TestPdf:
        image[2, 2].ShouldBe(new Rgba32(255, 255, 255));
        image[400, 200].R.ShouldBeInRange<byte>(120, 135);
    }

    [TestMethod]
    public async Task ExtractPortraitPage()
    {
        ResetRepository();

        var fileId = await AddPdfAsync(TestPdf.Create(200, 400), "thumbnail", new PdfImageExtractionProcessor(new() {
            MaxPixelSize = 800,
        }));

        using var image = Image.Load((await _repo.GetVariantAsync(fileId, "thumbnail")).Path.PathExport);
        image.Width.ShouldBe(400);
        image.Height.ShouldBe(800);
    }

    [TestMethod]
    public async Task ExtractSecondPage()
    {
        ResetRepository();

        var fileId = await AddPdfAsync(TestPdf.Create(300, 300, pageCount: 2), "page2", new PdfImageExtractionProcessor(new() {
            PageNumber = 2,
            MaxPixelSize = 600,
        }));

        using var image = Image.Load((await _repo.GetVariantAsync(fileId, "page2")).Path.PathExport);
        image.Width.ShouldBe(600);
        image.Height.ShouldBe(600);
    }

    [TestMethod]
    public async Task BackgroundColor()
    {
        ResetRepository();

        var fileId = await AddPdfAsync(TestPdf.Create(400, 200), "thumbnail", new PdfImageExtractionProcessor(new() {
            MaxPixelSize = 400,
            BackgroundColor = PdfBackgroundColor.FromRgb(255, 0, 0),
        }));

        using var image = Image.Load<Rgba32>((await _repo.GetVariantAsync(fileId, "thumbnail")).Path.PathExport);
        image[2, 2].ShouldBe(new Rgba32(255, 0, 0));
    }

    [TestMethod]
    public async Task ExtractAtDpi()
    {
        ResetRepository();

        // 400x200 points at 144 DPI = 800x400 pixels (within the default 2048 MaxPixelSize cap):
        var fileId = await AddPdfAsync(TestPdf.Create(400, 200), "thumbnail", new PdfImageExtractionProcessor(new() {
            Dpi = 144,
        }));

        using var image = Image.Load((await _repo.GetVariantAsync(fileId, "thumbnail")).Path.PathExport);
        image.Width.ShouldBe(800);
        image.Height.ShouldBe(400);
    }

    [TestMethod]
    public async Task ExtractAtDpiCappedByMaxPixelSize()
    {
        ResetRepository();

        // 400x200 points at 144 DPI = 800x400 pixels, capped to a 400 pixel longest edge:
        var fileId = await AddPdfAsync(TestPdf.Create(400, 200), "thumbnail", new PdfImageExtractionProcessor(new() {
            Dpi = 144,
            MaxPixelSize = 400,
        }));

        using var image = Image.Load((await _repo.GetVariantAsync(fileId, "thumbnail")).Path.PathExport);
        image.Width.ShouldBe(400);
        image.Height.ShouldBe(200);
    }

    [TestMethod]
    public void NoDpiOrMaxPixelSize()
    {
        var ex = Should.Throw<ArgumentException>(() => new PdfImageExtractionProcessor(new() {
            Dpi = null,
            MaxPixelSize = null,
        }));

        ex.Message.ShouldStartWith("At least one of Dpi or MaxPixelSize must be specified in the options.");
    }

    [TestMethod]
    public async Task PageNumberOutOfRange()
    {
        ResetRepository();

        var ex = await Should.ThrowAsync<FileProcessingException>(
            AddPdfAsync(TestPdf.Create(300, 300), "page3", new PdfImageExtractionProcessor(new() {
                PageNumber = 3,
            })));

        ex.Message.ShouldBe("Cannot extract an image from page 3 of the PDF document because it only contains 1 page(s).");
    }

    [TestMethod]
    public async Task UnsupportedExtension()
    {
        ResetRepository();

        var pipeline = new PdfImageExtractionProcessor(PdfImageExtractionProcessingOptions.Standard).ToPipeline();

        await using var stream = new MemoryStream(TestPdf.Create(300, 300));

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, ".txt", leaveOpen: true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("Extension '.txt' is not allowed. Allowed extensions: .pdf");
    }

    [TestMethod]
    public async Task InvalidPdf()
    {
        ResetRepository();

        var pipeline = new PdfImageExtractionProcessor(PdfImageExtractionProcessingOptions.Standard).ToPipeline();

        await using var stream = new MemoryStream("This is not a PDF document."u8.ToArray());

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, ".pdf", leaveOpen: true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("Failed to read PDF document information.");
    }

    private async Task<FileId> AddPdfAsync(byte[] pdf, string variantId, PdfImageExtractionProcessor processor)
    {
        await using var stream = new MemoryStream(pdf);
        await using var txn = await _repo.BeginTransactionAsync();

        var added = await txn.AddAsync(stream, ".pdf", leaveOpen: true, _pdfValidationPipeline, TestContext.CancellationToken);
        await _repo.AddVariantAsync(added.FileId, variantId, processor.ToPipeline(), TestContext.CancellationToken);

        await txn.CommitAsync(TestContext.CancellationToken);
        return added.FileId;
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
