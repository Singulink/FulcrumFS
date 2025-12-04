using PrefixClassName.MsTest;
using Shouldly;
using Singulink.IO;
using SixLabors.ImageSharp;

namespace FulcrumFS.Images;

[PrefixTestClass]
public sealed class Tests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteFilePath _imageFile = _appDir.CombineDirectory("Images").CombineFile("test-1024x768.jpg");
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot");

    private static readonly FileRepo _repo = new(_repoDir, options => {
        options.DeleteDelay = TimeSpan.Zero;
        options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(120);
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
            await txn.AddAsync(stream, true, pipeline);
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
            await tx.AddAsync(stream, true, pipeline);
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
            var added = await txn.AddAsync(stream, leaveOpen: false, new ImageProcessor(ImageProcessingOptions.Preserve).ToPipeline());
            fileId = added.FileId;

            await _repo.AddVariantAsync(added.FileId, "thumbnail", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.FitDown, 100, 100),
            }).ToPipeline());

            await txn.CommitAsync();
        }

        var imagePath = await _repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        var thumbnailPath = await _repo.GetVariantAsync(fileId, "thumbnail");
        imagePath.Exists.ShouldBeTrue();

        await using (var txn = await _repo.BeginTransactionAsync())
        {
            await txn.DeleteAsync(fileId);
            await txn.CommitAsync();
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
                var added = await txn.AddAsync(stream, true, GetPipeline(ImageQuality.Highest));
                fileId = added.FileId;
            }

            await _repo.AddVariantAsync(fileId, "high", GetPipeline(ImageQuality.High));
            await _repo.AddVariantAsync(fileId, "medium", GetPipeline(ImageQuality.Medium));
            await _repo.AddVariantAsync(fileId, "low", GetPipeline(ImageQuality.Low));

            await txn.CommitAsync();
        }

        var highest = await _repo.GetAsync(fileId);
        var high = await _repo.GetVariantAsync(fileId, "high");
        var medium = await _repo.GetVariantAsync(fileId, "medium");
        var low = await _repo.GetVariantAsync(fileId, "low");

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
            }).ToPipeline());

            await _repo.AddVariantAsync(fileId, "crop", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.CropDown, 2000, 2000),
            }).ToPipeline());

            await _repo.AddVariantAsync(fileId, "pad1", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.PadDown, 800, 800),
                BackgroundColor = ImageBackgroundColor.FromRgb(0, 255, 0, true),
            }).ToPipeline());

            await _repo.AddVariantAsync(fileId, "pad2", new ImageProcessor(ImageProcessingOptions.Preserve with {
                Resize = new(ImageResizeMode.PadDown, 2000, 2000) {
                    PadColor = ImageBackgroundColor.FromRgb(255, 0, 0, true),
                },
            }).ToPipeline());

            await txn.CommitAsync();
        }

        using (var image = Image.Load((await _repo.GetAsync(fileId)).PathExport))
        {
            image.Width.ShouldBe(1024);
            image.Height.ShouldBe(768);
        }

        using (var max = Image.Load((await _repo.GetVariantAsync(fileId, "max")).PathExport))
        {
            max.Width.ShouldBe(1024);
            max.Height.ShouldBe(768);
        }

        using (var crop = Image.Load((await _repo.GetVariantAsync(fileId, "crop")).PathExport))
        {
            crop.Width.ShouldBe(768);
            crop.Height.ShouldBe(768);
        }

        using (var pad1 = Image.Load((await _repo.GetVariantAsync(fileId, "pad1")).PathExport))
        {
            pad1.Width.ShouldBe(800);
            pad1.Height.ShouldBe(800);
        }

        using (var pad2 = Image.Load((await _repo.GetVariantAsync(fileId, "pad2")).PathExport))
        {
            pad2.Width.ShouldBe(1024);
            pad2.Height.ShouldBe(1024);
        }
    }

    [TestMethod]
    public async Task ThrowWhenSourceUnchanged()
    {
        ResetRepository();

        var pipeline = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            Resize = new(ImageResizeMode.FitDown, 2000, 2000),
            ReencodeMode = ImageReencodeMode.AvoidReencoding,
        }).ToPipeline(throwWhenSourceUnchanged: true);

        await using var stream = _imageFile.OpenAsyncStream();

        var ex = await Should.ThrowAsync<FileSourceUnchangedException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline);
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
            await txn.AddAsync(stream, true, pipeline);
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
            await txn.AddAsync(stream, ".png", true, pipeline);
        });

        ex.Message.ShouldBe("Image format 'JPEG' is not allowed. Allowed formats: PNG");
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
        }
    }
}
