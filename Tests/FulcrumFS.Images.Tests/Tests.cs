using PrefixClassName.MsTest;
using Shouldly;
using Singulink.IO;
using SixLabors.ImageSharp;

namespace FulcrumFS.Images.Tests;

[PrefixTestClass]
public sealed class Tests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteFilePath _imageFile = _appDir.CombineDirectory("Images").CombineFile("test-1024x768.jpg");
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot");

    private static readonly FileRepo _repo = new FileRepo(new FileRepoOptions(_repoDir) {
        MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(120),
    });

    private static bool _initialized;

    [TestMethod]
    public async Task SourceValidation()
    {
        ResetRepository();

        var processor = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            SourceValidation = new() {
                MaxPixels = 500 * 500,
            },
        });

        await using var stream = _imageFile.OpenAsyncStream();

        var ex = await Should.ThrowAsync<FileProcessException>(async () => {
            await using var tx = await _repo.BeginTransactionAsync();
            await tx.AddAsync(stream, true, processor);
        });

        ex.Message.ShouldBe("The image is too large.");

        processor = new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg)],
            SourceValidation = new() {
                MaxWidth = 500,
                MaxHeight = 500,
            },
        });

        stream.Position = 0;

        ex = await Should.ThrowAsync<FileProcessException>(async () => {
            await using var tx = await _repo.BeginTransactionAsync();
            await tx.AddAsync(stream, true, processor);
        });

        ex.Message.ShouldBe("The image is too large. Maximum size is 500x500.");
    }

    [TestMethod]
    public async Task CreateAndDelete()
    {
        ResetRepository();

        using var stream = _imageFile.OpenStream();

        FileId fileId;

        await using (var tx = await _repo.BeginTransactionAsync())
        {
            var added = await tx.AddAsync(stream, leaveOpen: false, new ImageProcessor(new()));
            fileId = added.FileId;

            await _repo.GetOrAddVariantAsync(added.FileId, "thumbnail", new ImageProcessor(new() {
                Resize = new(ImageResizeMode.Max, 100, 100),
            }));

            await tx.CommitAsync();
        }

        var imagePath = await _repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        var thumbnailPath = await _repo.GetVariantAsync(fileId, "thumbnail");
        imagePath.Exists.ShouldBeTrue();

        await using (var tx = await _repo.BeginTransactionAsync())
        {
            await tx.DeleteAsync(fileId);
            await tx.CommitAsync();
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

        await using (var tx = await _repo.BeginTransactionAsync())
        {
            await using (var stream = _imageFile.OpenAsyncStream())
            {
                var added = await tx.AddAsync(stream, true, GetProcessor(100));
                fileId = added.FileId;
            }

            await _repo.GetOrAddVariantAsync(fileId, "75", GetProcessor(75));
            await _repo.GetOrAddVariantAsync(fileId, "50", GetProcessor(50));
            await _repo.GetOrAddVariantAsync(fileId, "25", GetProcessor(25));

            await tx.CommitAsync();
        }

        var i100 = await _repo.GetAsync(fileId);
        var i75 = await _repo.GetVariantAsync(fileId, "75");
        var i50 = await _repo.GetVariantAsync(fileId, "50");
        var i25 = await _repo.GetVariantAsync(fileId, "25");

        i100.Length.ShouldBeGreaterThan(i75.Length);
        i75.Length.ShouldBeGreaterThan(i50.Length);
        i50.Length.ShouldBeGreaterThan(i25.Length);

        FileProcessor GetProcessor(int quality) => new ImageProcessor(new() {
            Formats = [new(ImageFormat.Jpeg) {
                Quality = quality,
            }],
            Resize = new(ImageResizeMode.Max, 300, 300),
        });
    }

    [TestMethod]
    public async Task NoUpsizing()
    {
        ResetRepository();

        await using var stream = _imageFile.OpenAsyncStream();

        FileId fileId;

        await using (var tx = await _repo.BeginTransactionAsync())
        {
            var added = await tx.AddAsync(stream, leaveOpen: false, new ImageProcessor(new()));
            fileId = added.FileId;

            await _repo.GetOrAddVariantAsync(fileId, "max", new ImageProcessor(new() {
                Resize = new(ImageResizeMode.Max, 2000, 2000),
            }));

            await _repo.GetOrAddVariantAsync(fileId, "crop", new ImageProcessor(new() {
                Resize = new(ImageResizeMode.Crop, 2000, 2000),
            }));

            await _repo.GetOrAddVariantAsync(fileId, "pad1", new ImageProcessor(new() {
                Resize = new(ImageResizeMode.Pad, 800, 800),
                BackgroundColor = BackgroundColor.FromRgb(0, 255, 0, true),
            }));

            await _repo.GetOrAddVariantAsync(fileId, "pad2", new ImageProcessor(new() {
                Resize = new(ImageResizeMode.Pad, 2000, 2000) {
                    PadColor = BackgroundColor.FromRgb(255, 0, 0, true),
                },
            }));

            await tx.CommitAsync();
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
