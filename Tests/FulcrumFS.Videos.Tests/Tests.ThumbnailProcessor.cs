using Shouldly;
using Singulink.IO;
using SixLabors.ImageSharp;

namespace FulcrumFS.Videos;

// This file contains all of the ThumbnailProcessor-specific tests.

partial class Tests
{
#if !CI
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExtractThumbnailFromHDRVideoTest(bool remapHDRToSDR)
    {
        // Test extracting a thumbnail from an HDR video, with and without remapping to SDR.
        // This is a visual inspection test - the resulting images should be checked manually to ensure correct color representation.

        var resultFile = _appDir.CombineDirectory("TestHDRThumbnailResults").CombineFile(remapHDRToSDR ? "thumbnail_sdr.png" : "thumbnail_hdr.png");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard with
        {
            RemapHDRToSDR = remapHDRToSDR,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("Y0__auYqGXY-20s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        File.Copy(imagePath.PathExport, resultFile.PathExport);
    }
#endif

    [TestMethod]
    [DataRow("bbb_sunflower_1080p_60fps_normal-25s.mp4", 10, 0.5, false)]
    [DataRow("bbb_sunflower_1080p_60fps_normal-25s.mp4", 15, 0.5, true)]
    public async Task TestThumbnailTimestampSelection(string fileName, double seconds, double fraction, bool useFraction)
    {
        // Tests that ThumbnailProcessor selects the correct frame timestamp when both ImageTimestamp and ImageTimestampFraction are specified.
        // The processor should take the minimum of the two when both have meaning.

        using var repoCtx = GetRepo(out var repo);

        // Helper to extract thumbnail with given options and return the image path:
        async Task<IAbsoluteFilePath> ExtractThumbnail(ThumbnailProcessingOptions options)
        {
            var pipeline = new ThumbnailProcessor(options).ToPipeline();
            var origFile = _videoFilesDir.CombineFile(fileName);
            await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);

            return await repo.GetAsync(fileId);
        }

        // Extract with both options set
        var bothOptionsPath = await ExtractThumbnail(new ThumbnailProcessingOptions
        {
            ImageTimestamp = TimeSpan.FromSeconds(seconds),
            ImageTimestampFraction = fraction,
            RemapHDRToSDR = true,
        });

        // Extract with only absolute timestamp:
        var absoluteOnlyPath = await ExtractThumbnail(new ThumbnailProcessingOptions
        {
            ImageTimestamp = TimeSpan.FromSeconds(seconds),
            RemapHDRToSDR = true,
        });

        // Extract with only fraction:
        var fractionOnlyPath = await ExtractThumbnail(new ThumbnailProcessingOptions
        {
            ImageTimestampFraction = fraction,
            RemapHDRToSDR = true,
        });

        // Check if we used the expected image:
        AreImagePixelsEqual(bothOptionsPath.PathExport, fractionOnlyPath.PathExport).ShouldBe(useFraction);
        AreImagePixelsEqual(bothOptionsPath.PathExport, absoluteOnlyPath.PathExport).ShouldBe(!useFraction);
    }

    [TestMethod]
    public async Task TestThumbnailFromExplicitThumbnailStream()
    {
        // Tests that when IncludeThumbnailVideoStreams is true, the thumbnail is extracted from the embedded thumbnail stream.
        // Uses video53.mp4 which has an embedded PNG thumbnail stream.
        // When IncludeThumbnailVideoStreams is true, the result should match test_image_1.png.
        // When IncludeThumbnailVideoStreams is false, the result should be different (taken from the main video stream).

        using var repoCtx = GetRepo(out var repo);

        var expectedThumbnailPath = _videoFilesDir.CombineFile("test_image_1.png");

        // Helper to extract thumbnail with given options and return the image path:
        async Task<IAbsoluteFilePath> ExtractThumbnail(ThumbnailProcessingOptions options)
        {
            var pipeline = new ThumbnailProcessor(options).ToPipeline();
            var origFile = _videoFilesDir.CombineFile("video53.mp4");
            await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);

            return await repo.GetAsync(fileId);
        }

        // Extract with IncludeThumbnailVideoStreams = true - should use embedded thumbnail stream:
        var withThumbnailStreamPath = await ExtractThumbnail(ThumbnailProcessingOptions.Standard with
        {
            IncludeThumbnailVideoStreams = true,
        });

        // Extract with IncludeThumbnailVideoStreams = false - should use main video stream:
        var withoutThumbnailStreamPath = await ExtractThumbnail(ThumbnailProcessingOptions.Standard with
        {
            IncludeThumbnailVideoStreams = false,
        });

        // When using thumbnail stream, should match the expected image:
        AreImagePixelsEqual(withThumbnailStreamPath.PathExport, expectedThumbnailPath.PathExport)
            .ShouldBeTrue("When IncludeThumbnailVideoStreams is true, the result should match the embedded thumbnail (test_image_1.png).");

        // When not using thumbnail stream, should be different from the expected image:
        AreImagePixelsEqual(withoutThumbnailStreamPath.PathExport, expectedThumbnailPath.PathExport)
            .ShouldBeFalse("When IncludeThumbnailVideoStreams is false, the result should differ from the embedded thumbnail.");
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosWithVideoStreamsToCheck))]
    public async Task TestThumbnailProcessorAllOptionsConfigurations(string fileName)
    {
        // Brute force test that verifies ThumbnailProcessor can successfully process all valid video files
        // with all important combinations of options.

        using var repoCtx = GetRepo(out var repo);

        // Define the option variations to test:
        bool[] includeThumbnailVideoStreamsValues = [true, false];
        bool[] remapHDRToSDRValues = [true, false];
        bool[] forceSquarePixelsValues = [true, false];

        foreach (bool includeThumbnailVideoStreams in includeThumbnailVideoStreamsValues)
        {
            foreach (bool remapHDRToSDR in remapHDRToSDRValues)
            {
                foreach (bool forceSquarePixels in forceSquarePixelsValues)
                {
                    var options = ThumbnailProcessingOptions.Standard with
                    {
                        IncludeThumbnailVideoStreams = includeThumbnailVideoStreams,
                        RemapHDRToSDR = remapHDRToSDR,
                        ForceSquarePixels = forceSquarePixels,
                    };

                    try
                    {
                        var pipeline = new ThumbnailProcessor(options).ToPipeline();
                        var origFile = _videoFilesDir.CombineFile(fileName);
                        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

                        await using var txn = await repo.BeginTransactionAsync();
                        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
                        await txn.CommitAsync(TestContext.CancellationToken);

                        var imagePath = await repo.GetAsync(fileId);
                        imagePath.Exists.ShouldBeTrue();

                        // Verify it's a valid image by loading it:
                        using var image = Image.Load(imagePath.PathExport);
                        image.Width.ShouldBeGreaterThan(0);
                        image.Height.ShouldBeGreaterThan(0);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(
                            $"Failed to process '{fileName}' with options: IncludeThumbnailVideoStreams={includeThumbnailVideoStreams}, " +
                            $"RemapHDRToSDR={remapHDRToSDR}, ForceSquarePixels={forceSquarePixels}",
                            ex);
                    }
                }
            }
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TestThumbnailHDRColorSpaceRemapping(bool remapHDRToSDR)
    {
        // Tests that the HDR to SDR color space remapping works correctly for thumbnail extraction.
        // Uses an HDR video and verifies the color properties of the resulting thumbnail using ffprobe.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(new ThumbnailProcessingOptions
        {
            ImageTimestampFraction = 0.3,
            RemapHDRToSDR = remapHDRToSDR,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("Y0__auYqGXY-5s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Use ffprobe to check color properties of the resulting image:
        string imageInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", imagePath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            // Check color properties - bt709/bt709 indicates SDR, other values indicate HDR preserved or unexpected SDR colors.
            imageInfo.Contains("\"color_transfer\": \"bt709\"", StringComparison.Ordinal).ShouldBe(remapHDRToSDR);
            imageInfo.Contains("\"color_primaries\": \"bt709\"", StringComparison.Ordinal).ShouldBe(remapHDRToSDR);
            imageInfo.Contains("\"color_space\": \"gbr\"", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Thumbnail HDR/SDR color property validation failed. Info: " + imageInfo, ex);
        }
    }

    [TestMethod]
    [DataRow("video159.ts")]
    [DataRow("video161.mp4")]
    [DataRow("video168.mp4")]
    public async Task TestThumbnailFailsWithNoVideoStreams(string fileName)
    {
        // Tests that ThumbnailProcessor fails with a clear error when the input file has no video streams.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(new ThumbnailProcessingOptions
        {
            ImageTimestampFraction = 0.5,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
        {
            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("No suitable video stream found to extract thumbnail from.");
    }

    [TestMethod]
    public async Task TestThumbnailFailsWhenTimestampExceedsDuration()
    {
        // Tests that ThumbnailProcessor throws an exception when the requested timestamp exceeds the video duration.
        // Uses video1.mp4 which is 1 second long, with a 2 second timestamp request.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(new ThumbnailProcessingOptions
        {
            ImageTimestamp = TimeSpan.FromSeconds(2),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video1.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
        {
            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("Specified thumbnail timestamp is beyond the end of the video.");
    }

    [TestMethod]
    [DataRow("video1.mp4", "rgb24")]
    [DataRow("video32.mp4", "rgb48be")]
    [DataRow("video43.mp4", "rgb48be")]
    public async Task TestThumbnailBitDepthPreservation(string videoFileName, string expectedPixelFormat)
    {
        // Tests that 8-bit input videos produce 8-bpc PNG thumbnails, and higher bit depth videos produce 16-bpc PNG thumbnails.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(videoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Use ffprobe to get pix_fmt from the PNG:
        string imageInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", imagePath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        // Check pixel format:
        imageInfo.Contains($"\"pix_fmt\": \"{expectedPixelFormat}\"", StringComparison.Ordinal)
            .ShouldBeTrue($"Expected pixel format {expectedPixelFormat} for {videoFileName}.");
    }

    [TestMethod]
    public async Task TestThumbnailSelectsDefaultVideoStreamOverNonDefault()
    {
        // Tests that ThumbnailProcessor selects a non-first video stream that is marked as default over a first video stream that is not marked as default.
        // Creates a test video with two video streams: first is non-default, second is default.

        using var repoCtx = GetRepo(out var repo);

        // Create a combined video file with two video streams where the second is marked as default:
        // We'll use video1.mp4 as the first (non-default) stream and video2.mkv as the second (default) stream.
        string combinedVideoPath = GetUniqueTempFilePath(".mp4");

        // Use ffmpeg to create a file with two video streams, marking the second as default:
        var file1 = _videoFilesDir.CombineFile("video1.mp4");
        var file2 = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", file1.PathExport,
                "-i", file2.PathExport,
                "-map", "0:v:0",
                "-map", "1:v:0",
                "-c", "copy",
                "-disposition:v:0", "-default",
                "-disposition:v:1", "+default",
                "-t", "1",
                "-y",
                combinedVideoPath,
            ],
            TestContext.CancellationToken);

        // Extract thumbnail from the combined video:
        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard).ToPipeline();

        await using var stream = FilePath.ParseAbsolute(combinedVideoPath).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, ".mp4", true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var thumbnailPath = await repo.GetAsync(fileId);
        thumbnailPath.Exists.ShouldBeTrue();

        // Also extract thumbnails from the individual source videos for comparison:
        await using var stream1 = file1.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream1, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);
        var thumbnail1Path = await repo.GetAsync(fileId1);

        await using var stream2 = file2.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream2, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);
        var thumbnail2Path = await repo.GetAsync(fileId2);

        // The thumbnail from the combined video should match video2 (the default stream), not video1:
        AreImagePixelsEqual(thumbnailPath.PathExport, thumbnail2Path.PathExport).ShouldBeTrue();
        AreImagePixelsEqual(thumbnailPath.PathExport, thumbnail1Path.PathExport).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("video111.mp4", true, 171, 128)]
    [DataRow("video111.mp4", false, 128, 128)]
    [DataRow("video166.mp4", true, 128, 128)]
    [DataRow("video166.mp4", false, 96, 128)]
    public async Task TestThumbnailNonSquarePixelHandling(string fileName, bool forceSquarePixels, int expectedWidth, int expectedHeight)
    {
        // Tests that ForceSquarePixels option correctly handles videos with non-square pixel aspect ratios (SAR).
        // When ForceSquarePixels is true, the output dimensions should be adjusted to account for the SAR.
        // When ForceSquarePixels is false, the original pixel dimensions should be preserved.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard with
        {
            ForceSquarePixels = forceSquarePixels,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Verify the output dimensions:
        using var image = Image.Load(imagePath.PathExport);
        image.Width.ShouldBe(expectedWidth);
        image.Height.ShouldBe(expectedHeight);
    }

    [TestMethod]
    public async Task TestThumbnailMaxResolutionCapping()
    {
        // Tests that the ThumbnailProcessor correctly caps output dimensions to 32767x32767.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard).ToPipeline();

        // video143.mp4 has a resolution of 64x65534, which exceeds the max height.
        var videoPath = _videoFilesDir.CombineFile("video143.mp4");
        await using var stream = videoPath.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, ".mp4", true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Verify the output dimensions are capped:
        using var image = Image.Load(imagePath.PathExport);
        image.Width.ShouldBe(32);
        image.Height.ShouldBe(32767);
    }
}
