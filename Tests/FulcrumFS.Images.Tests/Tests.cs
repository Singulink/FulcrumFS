using PrefixClassName.MsTest;
using Shouldly;
using Singulink.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace FulcrumFS.Images;

[PrefixTestClass]
public sealed class Tests
{
    public required TestContext TestContext { get; set; }

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteFilePath _imageFile = _appDir.CombineDirectory("Images").CombineFile("test-1024x768.jpg");
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot");

    private static readonly FileRepo _repo = new(_repoDir, RepoFormatVersion.Latest, options => {
        options.DeleteMode = DeleteMode.Immediate;
        options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
    });

    private static bool _initialized;

    [TestMethod]
    public async Task SourceValidation()
    {
        ResetRepository();

        var pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            SourceValidation = new() {
                MaxPixels = 500 * 500,
            },
        }).ToPipeline();

        await using var stream = _imageFile.OpenAsyncStream();

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("The image is too large.");

        pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            SourceValidation = new() {
                MaxWidth = 500,
                MaxHeight = 500,
            },
        }).ToPipeline();

        stream.Position = 0;

        ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var tx = await _repo.BeginTransactionAsync();
            await tx.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("The image is too large. Maximum size is 500x500.");
    }

    [TestMethod]
    public async Task CreateAndDelete()
    {
        ResetRepository();

        await using var stream = _imageFile.OpenStream();

        FileId fileId;

        await using (var txn = await _repo.BeginTransactionAsync())
        {
            var added = await txn.AddAsync(stream, leaveOpen: false, new ImageProcessor(ImageProcessingOptions.Preserve).ToPipeline(), TestContext.CancellationToken);
            fileId = added.FileId;

            await _repo.AddVariantAsync(added.FileId, "thumbnail", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.FitDown, 100, 100),
            }).ToPipeline(), TestContext.CancellationToken);

            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var imagePath = (await _repo.GetAsync(fileId)).Path;
        imagePath.Exists.ShouldBeTrue();

        var thumbnailPath = (await _repo.GetVariantAsync(fileId, "thumbnail")).Path;
        imagePath.Exists.ShouldBeTrue();

        await using (var txn = await _repo.BeginTransactionAsync())
        {
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        imagePath.Exists.ShouldBeFalse();
        thumbnailPath.Exists.ShouldBeFalse();

        imagePath.ParentDirectory.Exists.ShouldBeFalse();
        imagePath.ParentDirectory.ParentDirectory!.Exists.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Quality()
    {
        ResetRepository();

        FileId fileId;

        await using (var txn = await _repo.BeginTransactionAsync())
        {
            await using (var stream = _imageFile.OpenAsyncStream())
            {
                var added = await txn.AddAsync(stream, true, GetPipeline(ImageQuality.Highest), TestContext.CancellationToken);
                fileId = added.FileId;
            }

            await _repo.AddVariantAsync(fileId, "high", GetPipeline(ImageQuality.High), TestContext.CancellationToken);
            await _repo.AddVariantAsync(fileId, "medium", GetPipeline(ImageQuality.Medium), TestContext.CancellationToken);
            await _repo.AddVariantAsync(fileId, "low", GetPipeline(ImageQuality.Low), TestContext.CancellationToken);

            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var highest = (await _repo.GetAsync(fileId)).Path;
        var high = (await _repo.GetVariantAsync(fileId, "high")).Path;
        var medium = (await _repo.GetVariantAsync(fileId, "medium")).Path;
        var low = (await _repo.GetVariantAsync(fileId, "low")).Path;

        highest.Length.ShouldBeGreaterThan(high.Length);
        high.Length.ShouldBeGreaterThan(medium.Length);
        medium.Length.ShouldBeGreaterThan(low.Length);

        static FileProcessingPipeline GetPipeline(ImageQuality quality) => new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            Resize = new(ImageResizeMode.FitDown, 300, 300),
            Quality = quality,
        }).ToPipeline();
    }

    [TestMethod]
    public async Task NoUpsizing()
    {
        ResetRepository();

        await using var stream = _imageFile.OpenAsyncStream();

        FileId fileId;

        await using (var txn = await _repo.BeginTransactionAsync())
        {
            var added = await txn.AddAsync(stream, leaveOpen: false, new ImageProcessor(ImageProcessingOptions.Preserve).ToPipeline());
            fileId = added.FileId;

            await _repo.AddVariantAsync(fileId, "max", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.FitDown, 2000, 2000),
            }).ToPipeline(), TestContext.CancellationToken);

            await _repo.AddVariantAsync(fileId, "crop", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.CropDown, 2000, 2000),
            }).ToPipeline(), TestContext.CancellationToken);

            await _repo.AddVariantAsync(fileId, "pad1", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.PadDown, 800, 800),
                BackgroundColor = ImageBackgroundColor.FromRgb(0, 255, 0, true),
            }).ToPipeline(), TestContext.CancellationToken);

            await _repo.AddVariantAsync(fileId, "pad2", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.PadDown, 2000, 2000) {
                    PadColor = ImageBackgroundColor.FromRgb(255, 0, 0, true),
                },
            }).ToPipeline(), TestContext.CancellationToken);

            await txn.CommitAsync(TestContext.CancellationToken);
        }

        using (var image = Image.Load((await _repo.GetAsync(fileId)).Path.PathExport))
        {
            image.Width.ShouldBe(1024);
            image.Height.ShouldBe(768);
        }

        using (var max = Image.Load((await _repo.GetVariantAsync(fileId, "max")).Path.PathExport))
        {
            max.Width.ShouldBe(1024);
            max.Height.ShouldBe(768);
        }

        using (var crop = Image.Load((await _repo.GetVariantAsync(fileId, "crop")).Path.PathExport))
        {
            crop.Width.ShouldBe(768);
            crop.Height.ShouldBe(768);
        }

        using (var pad1 = Image.Load((await _repo.GetVariantAsync(fileId, "pad1")).Path.PathExport))
        {
            pad1.Width.ShouldBe(800);
            pad1.Height.ShouldBe(800);
        }

        using (var pad2 = Image.Load((await _repo.GetVariantAsync(fileId, "pad2")).Path.PathExport))
        {
            pad2.Width.ShouldBe(1024);
            pad2.Height.ShouldBe(1024);
        }
    }

    [TestMethod]
    public async Task SkipWhenSourceUnchanged()
    {
        ResetRepository();

        var pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            Resize = new(ImageResizeMode.FitDown, 2000, 2000),
            ReencodeMode = ImageReencodeMode.AvoidReencoding,
        }).ToPipeline(throwWhenMainSourceUnchanged: true);

        await using var stream = _imageFile.OpenAsyncStream();

        var ex = await Should.ThrowAsync<FileSourceUnchangedException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("File processing did not result in any changes to the source file.");
    }

    [TestMethod]
    public async Task UnsupportedExtension()
    {
        ResetRepository();

        var pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Png)],
        }).ToPipeline();

        await using var stream = _imageFile.OpenAsyncStream();

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("Extension '.jpg' is not allowed. Allowed extensions: .png, .apng");
    }

    [TestMethod]
    public async Task UnsupportedFormat()
    {
        ResetRepository();
        var pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Png)],
        }).ToPipeline();

        await using var stream = _imageFile.OpenAsyncStream();

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, ".png", true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("Image format 'JPEG' is not allowed. Allowed formats: PNG");
    }

    [TestMethod]
    public async Task SourceQualityIsNotExceeded()
    {
        ResetRepository();

        // A source encoded at a low quality: encoding it again at a higher one would only make the file larger, so the quality read from the source caps the
        // quality of the result, whatever the options ask for.
        using var source = new MemoryStream();

        using (var image = Image.Load(_imageFile.PathExport))
            image.SaveAsJpeg(source, new JpegEncoder { Quality = 30 });

        source.Position = 0;
        Image.Identify(source).Metadata.GetJpegMetadata().Quality.ShouldBeInRange(28, 32);
        source.Position = 0;

        var pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            Quality = ImageQuality.Highest,
            ReencodeMode = ImageReencodeMode.Always,
        }).ToPipeline();

        FileId fileId;

        await using (var txn = await _repo.BeginTransactionAsync())
        {
            fileId = (await txn.AddAsync(source, ".jpg", true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        Image.Identify((await _repo.GetAsync(fileId)).Path.PathExport).Metadata.GetJpegMetadata().Quality.ShouldBeInRange(28, 32);
    }

    [TestMethod]
    public async Task BackgroundColorReplacesTransparency()
    {
        ResetRepository();

        // A fully transparent image converted to a format without transparency takes the background color, channel for channel.
        using var source = new MemoryStream();

        using (var image = new Image<Rgba32>(32, 32))
            image.SaveAsPng(source);

        source.Position = 0;

        var pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Png, ImageFormat.Jpeg)],
            BackgroundColor = ImageBackgroundColor.FromRgb(20, 200, 90),
            Quality = ImageQuality.Highest,
        }).ToPipeline();

        FileId fileId;

        await using (var txn = await _repo.BeginTransactionAsync())
        {
            fileId = (await txn.AddAsync(source, ".png", true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var path = (await _repo.GetAsync(fileId)).Path;
        path.Extension.ShouldBe(".jpg");

        using var result = Image.Load<Rgb24>(path.PathExport);
        result.Width.ShouldBe(32);
        result.Height.ShouldBe(32);

        // JPEG is lossy, so each channel is allowed a small difference.
        var pixel = result[16, 16];
        ((int)pixel.R).ShouldBeInRange(16, 24);
        ((int)pixel.G).ShouldBeInRange(196, 204);
        ((int)pixel.B).ShouldBeInRange(86, 94);
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
