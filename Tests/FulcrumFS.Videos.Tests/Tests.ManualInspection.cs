using System.Globalization;
using Shouldly;
using Singulink.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// NOTE: these are only the manual inspection tests for VideoProcessor.
// These tests should be re-run locally when making changes to related functionality to ensure it looks visually / sounds audibly correct.

partial class Tests
{
    // Helper to generate a video file that calculates the diff between the original and processed & exemplifies them.
    // Difference is converted to a heatmap for easy viewing, scale is ~5.3x (that is, a difference in brightness of ~19% = full white), and goes from black
    // (no difference) to white (maximum difference) through green and cyan.
    // File is outputted at 10fps for most videos (except for the ones for TestVideoCompressionLevel, which are 30fps) to save time.
    // Note: both inputs have their timestamps zero-based (setpts=PTS-STARTPTS) so that the fps filter samples both on the same content-relative grid,
    // regardless of any differing start offsets between the original & processed files (e.g. a start_time that was rounded differently by the container).
    private async Task GenerateVideoDiffFile(
        IAbsoluteFilePath processedFile, IAbsoluteFilePath originalFile, int fps = 10, double duration = 10.0)
    {
        // Note: this method is disabled on CI as it can be quite slow and is for visual inspection only anyway.
#if !CI
        var diffFile = processedFile.ParentDirectory.CombineFile($"{processedFile.NameWithoutExtension}-diff.mp4");
        diffFile.Delete();

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-sws_flags", "accurate_rnd+bitexact",
                "-i", originalFile.PathExport,
                "-i", processedFile.PathExport,
                "-filter_complex", string.Create(CultureInfo.InvariantCulture,
                    $"[0:v]" +
                    $"setpts=PTS-STARTPTS," +
                    $"fps=fps={fps}," +
                    $"format=pix_fmts=yuv444p:color_ranges=pc" +
                    $"[o0];" +
                    $"[1:v]" +
                    $"setpts=PTS-STARTPTS," +
                    $"fps=fps={fps}," +
                    $"format=pix_fmts=yuv444p:color_ranges=pc" +
                    $"[o1];" +
                    $"[o0][o1]" +
                    $"blend=all_mode=difference," +
                    $"format=gray," +
                    $"geq='r=min(max(16*r(X,Y)-510,0),255):g=min(max(16*g(X,Y),0),255):b=min(max(16*b(X,Y)-255,0),255)'"),
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-y",
                diffFile.PathExport
            ],
            TestContext.CancellationToken);
#endif
    }

#if !CI
    [TestMethod]
    [DataRow(VideoCompressionLevel.Lowest)]
    [DataRow(VideoCompressionLevel.Low)]
    [DataRow(VideoCompressionLevel.Medium)]
    [DataRow(VideoCompressionLevel.High)]
    [DataRow(VideoCompressionLevel.Highest)]
    public async Task TestVideoCompressionLevelH264(VideoCompressionLevel level)
    {
        // Note: this test is disabled on CI as it can be quite slow.
        // Tests H.264 video compression at different levels. Outputs result files and diff heatmaps for visual comparison (they should all look similar).
        // Files should be sized approximately smaller at higher compression levels, but not guaranteed; however, they should certainly be more consistently
        // sized.

        var resultsDir = _appDir.CombineDirectory("TestVideoCompressionLevelH264Results");
        resultsDir.Create();

        var resultFile = resultsDir.CombineFile($"{level}.mp4");
        var originalFrameFile = resultsDir.CombineFile($"frame_original_{level}.png");
        var outputFrameFile = resultsDir.CombineFile($"frame_{level}.png");
        resultFile.Delete();
        originalFrameFile.Delete();
        outputFrameFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            VideoCompressionLevel = level,
            VideoQuality = VideoQuality.High,
            RemoveAudioStreams = true,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile, fps: 30, duration: 1.0);

        // Extract comparison frames & ensure the output displays similar to the original at every compression level:
        await ExtractVideoFrame(origFile, originalFrameFile, 0.5);
        await ExtractVideoFrame(resultFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(originalFrameFile, outputFrameFile, outputFrameFile.Name);
    }

    [TestMethod]
    [DataRow(VideoCompressionLevel.Lowest)]
    [DataRow(VideoCompressionLevel.Low)]
    [DataRow(VideoCompressionLevel.Medium)]
    [DataRow(VideoCompressionLevel.High)]
    [DataRow(VideoCompressionLevel.Highest)]
    public async Task TestVideoCompressionLevelHEVC(VideoCompressionLevel level)
    {
        // Note: this test is disabled on CI as it can be quite slow.
        // Tests HEVC video compression at different levels. Outputs result files and diff heatmaps for visual comparison (they should all look similar).
        // Files should be sized approximately smaller at higher compression levels, but not guaranteed; however, they should certainly be more consistently
        // sized.

        var resultsDir = _appDir.CombineDirectory("TestVideoCompressionLevelHEVCResults");
        resultsDir.Create();

        var resultFile = resultsDir.CombineFile($"{level}.mp4");
        var originalFrameFile = resultsDir.CombineFile($"frame_original_{level}.png");
        var outputFrameFile = resultsDir.CombineFile($"frame_{level}.png");
        resultFile.Delete();
        originalFrameFile.Delete();
        outputFrameFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.HEVC],
            VideoReencodeMode = StreamReencodeMode.Always,
            VideoCompressionLevel = level,
            VideoQuality = VideoQuality.High,
            RemoveAudioStreams = true,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile, fps: 30, duration: 1.0);

        // Extract comparison frames & ensure the output displays similar to the original at every compression level:
        await ExtractVideoFrame(origFile, originalFrameFile, 0.5);
        await ExtractVideoFrame(resultFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(originalFrameFile, outputFrameFile, outputFrameFile.Name);
    }
#endif

    [TestMethod]
    public async Task TestVideoQualityH264()
    {
        // Tests H.264 video quality at different levels. Outputs result files and diff heatmaps for visual comparison.
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        VideoQuality[] qualities = [VideoQuality.Lowest, VideoQuality.Low, VideoQuality.Medium, VideoQuality.High, VideoQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestVideoQualityImpl("H264", VideoCodec.H264, item.Quality, ct));

        // Verify file sizes increase with quality
        for (int i = 1; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    [TestMethod]
    public async Task TestVideoQualityHEVC()
    {
        // Tests HEVC video quality at different levels. Outputs result files and diff heatmaps for visual comparison.
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        VideoQuality[] qualities = [VideoQuality.Lowest, VideoQuality.Low, VideoQuality.Medium, VideoQuality.High, VideoQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestVideoQualityImpl("HEVC", VideoCodec.HEVC, item.Quality, ct));

        // Verify file sizes increase with quality
        for (int i = 1; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    private async Task<long> TestVideoQualityImpl(string resultFolderName, VideoCodec codec, VideoQuality quality, CancellationToken cancellationToken)
    {
        // Helper to encode a clip at a given quality/codec, emit diff output, and return size.
        var resultsDir = _appDir.CombineDirectory($"TestVideoQuality{resultFolderName}Results");
        resultsDir.Create();

        var resultFile = resultsDir.CombineFile($"{quality}.mp4");
        var originalFrameFile = resultsDir.CombineFile($"frame_original_{quality}.png");
        var outputFrameFile = resultsDir.CombineFile($"frame_{quality}.png");
        resultFile.Delete();
        originalFrameFile.Delete();
        outputFrameFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [codec],
            VideoReencodeMode = StreamReencodeMode.Always,
            VideoQuality = quality,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, cancellationToken)).FileId;
        await txn.CommitAsync(cancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile);

        // Extract comparison frames & ensure the output displays similar to the original at every quality level (frame file names include the quality since
        // the quality levels run in parallel):
        await ExtractVideoFrame(origFile, originalFrameFile, 0.5);
        await ExtractVideoFrame(resultFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(originalFrameFile, outputFrameFile, outputFrameFile.Name);

        return resultFile.Length;
    }

    // Helper to generate an audio spectrogram set for processed vs original files. Produces a 3840x2160 spectrogram of the processed audio.
    // A difference image is produced for easy viewing of changes, which is converted to a heatmap for easy viewing, scale is ~5.3x (that is, a difference in
    // brightness of ~19% = full white), and goes from black (no difference) to white (maximum difference) through green and cyan.
    // Duration controls how much of the clip is visualized.
    // The spectrogram diff does not tell the full story of audio quality, but is a useful quick visual reference for comparison within a specific encoder.
    private async Task GenerateAudioSpectrogramFile(IAbsoluteFilePath processedFile, IAbsoluteFilePath originalFile, double duration = 24.0)
    {
        // Note: this method is disabled on CI as it can be quite slow and is for visual inspection only anyway.
#if !CI
        var spectrogramFile = processedFile.ParentDirectory.CombineFile($"{processedFile.NameWithoutExtension}-spectrogram.png");
        spectrogramFile.Delete();

        var spectrogramDiffFile = processedFile.ParentDirectory.CombineFile($"{processedFile.NameWithoutExtension}-spectrogram-diff.png");
        spectrogramDiffFile.Delete();

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-sws_flags", "accurate_rnd+bitexact",
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-i", processedFile.PathExport,
                "-filter_complex", "[0:a]showspectrumpic=s=3840x2160",
                "-y",
                spectrogramFile.PathExport
            ],
            TestContext.CancellationToken);

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-sws_flags", "accurate_rnd+bitexact",
                "-i", spectrogramFile.PathExport,
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-i", originalFile.PathExport,
                "-filter_complex",
                    "[0:v]" +
                    "format=pix_fmts=rgb24:color_ranges=pc" +
                    "[0i];" +
                    "[1:a]" +
                    "showspectrumpic=s=3840x2160," +
                    "format=pix_fmts=rgb24:color_ranges=pc" +
                    "[1i];" +
                    "[0i][1i]" +
                    "blend=all_mode=difference," +
                    "format=gray," +
                    "geq='r=min(max(16*r(X,Y)-510,0),255):g=min(max(16*g(X,Y),0),255):b=min(max(16*b(X,Y)-255,0),255)'",
                "-y",
                spectrogramDiffFile.PathExport
            ],
            TestContext.CancellationToken);
#endif
    }

    [TestMethod]
    public async Task TestAudioQualityLibFDKAAC()
    {
        // Tests AAC audio quality at different levels using libfdk_aac encoder. Outputs spectrograms for visual comparison (not the most accurate method, but
        // useful for a quick comparison within a specific encoder).
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        AudioQuality[] qualities = [AudioQuality.Lowest, AudioQuality.Low, AudioQuality.Medium, AudioQuality.High, AudioQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestAudioQualityLibFDKAACImpl(item.Quality, ct));

        // Verify file sizes increase with quality (note: Lowest happens to be larger than Low due to random chance, so we skip that check)
        for (int i = 2; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    private async Task<long> TestAudioQualityLibFDKAACImpl(AudioQuality quality, CancellationToken cancellationToken)
    {
        // Helper to encode audio with libfdk_aac at a given quality and generate spectrograms.
        var resultFile = _appDir.CombineDirectory("TestAudioQualityLibFDKAACResults").CombineFile($"{quality}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
#if DEBUG
            ForceLibFDKAACUsage = true,
#endif
            AudioReencodeMode = StreamReencodeMode.Always,
            AudioQuality = quality,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, cancellationToken)).FileId;
        await txn.CommitAsync(cancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateAudioSpectrogramFile(resultFile, origFile);

        return resultFile.Length;
    }

#if DEBUG
    [TestMethod]
    public async Task TestAudioQualityAAC()
    {
        // Tests AAC audio quality at different levels using default aac encoder. Outputs spectrograms for visual comparison (not the most accurate method, but
        // useful for a quick comparison within a specific encoder).
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        AudioQuality[] qualities = [AudioQuality.Lowest, AudioQuality.Low, AudioQuality.Medium, AudioQuality.High, AudioQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestAudioQualityAACImpl(item.Quality, ct));

        // Verify file sizes increase with quality
        for (int i = 1; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    private async Task<long> TestAudioQualityAACImpl(AudioQuality quality, CancellationToken cancellationToken)
    {
        // Helper to encode audio with native AAC at a given quality and generate spectrograms.
        var resultFile = _appDir.CombineDirectory("TestAudioQualityAACResults").CombineFile($"{quality}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ForceLibFDKAACUsage = false,
            AudioReencodeMode = StreamReencodeMode.Always,
            AudioQuality = quality,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, cancellationToken)).FileId;
        await txn.CommitAsync(cancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateAudioSpectrogramFile(resultFile, origFile);

        return resultFile.Length;
    }
#endif

#if !CI
    [TestMethod]
    [DataRow(1920, 1080)]
    [DataRow(1280, 720)]
    [DataRow(1024, 576)]
    [DataRow(960, 540)]
    [DataRow(640, 360)]
    [DataRow(320, 180)]
    public async Task TestVideoResizeH264(int width, int height)
    {
        // Note: this test is disabled on CI as it's for visual inspection primarily (video resizing is also tested elsewhere).
        // Tests H.264 video resizing at various target dimensions. Outputs resized result files.

        var resultsDir = _appDir.CombineDirectory("TestVideoResizeH264Results");
        resultsDir.Create();

        var resultFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"{width}x{height}.mp4"));
        var originalFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_original_{width}x{height}.png"));
        var outputFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_{width}x{height}.png"));
        resultFile.Delete();
        originalFrameFile.Delete();
        outputFrameFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, width, height),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        // Extract comparison frames & ensure the resized output displays the same as the original (the SSIM comparison scales the original frame down to
        // the output size):
        await ExtractVideoFrame(origFile, originalFrameFile, 0.5);
        await ExtractVideoFrame(resultFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(originalFrameFile, outputFrameFile, outputFrameFile.Name);
    }

    [TestMethod]
    [DataRow(1920, 1080)]
    [DataRow(1280, 720)]
    [DataRow(1024, 576)]
    [DataRow(960, 540)]
    [DataRow(640, 360)]
    [DataRow(320, 180)]
    public async Task TestVideoResizeHEVC(int width, int height)
    {
        // Note: this test is disabled on CI as it's for visual inspection primarily (video resizing is also tested elsewhere).
        // Tests HEVC video resizing at various target dimensions. Outputs resized result files.

        var resultsDir = _appDir.CombineDirectory("TestVideoResizeHEVCResults");
        resultsDir.Create();

        var resultFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"{width}x{height}.mp4"));
        var originalFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_original_{width}x{height}.png"));
        var outputFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_{width}x{height}.png"));
        resultFile.Delete();
        originalFrameFile.Delete();
        outputFrameFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.HEVC],
            VideoReencodeMode = StreamReencodeMode.Always,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, width, height),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        // Extract comparison frames & ensure the resized output displays the same as the original (the SSIM comparison scales the original frame down to
        // the output size):
        await ExtractVideoFrame(origFile, originalFrameFile, 0.5);
        await ExtractVideoFrame(resultFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(originalFrameFile, outputFrameFile, outputFrameFile.Name);
    }
#endif

    [TestMethod]
    [DataRow("video16", ".mkv", false)]
    [DataRow("video17", ".mkv", false)]
    [DataRow("video18", ".mkv", false)]
    [DataRow("video19", ".mkv", false)]
    [DataRow("video20", ".mp4", false)]
    [DataRow("video169", ".mkv", true)]
    [DataRow("video170", ".mkv", true)]
    public async Task TestSubtitleReencode(string fileName, string extension, bool makeMkvCopy)
    {
        // All of these files should end up with subtitles that are playable in VLC after re-encoding (note: you may have to try playing the video more than
        // once, due to VLC struggling with short subtitles near the start).
        // Note: the subtitles for 169 won't look right when playing as mp4 necessarily, as support for dvd_subtitles in mp4 in some players is poor, but if
        // remuxed back to mkv, it should look correct (which this test does for you); file 170 is the same, but it doesn't look entirely correct even after.

        var resultFile = _appDir.CombineDirectory("TestSubtitleReencodeResults").CombineFile(string.Create(CultureInfo.InvariantCulture, $"{fileName}.mp4"));
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName + extension);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        // If extension was .mkv, also make a copy remuxed back to mkv to validate in case of weird mp4 dvd_subtitle playback issues:
        // Note: '-map 0' ensures all streams are kept (default stream selection only picks one stream per type, losing extra subtitle streams), and
        // '-bitexact' keeps the remux deterministic (Matroska otherwise writes a random SegmentUID and the writing date).
        if (makeMkvCopy)
        {
            var resultFileMkv = _appDir.CombineDirectory("TestSubtitleReencodeResults").CombineFile(
                string.Create(CultureInfo.InvariantCulture, $"{fileName}.mkv"));
            resultFileMkv.Delete();
            await RunFFtoolProcessWithErrorHandling(
                "ffmpeg",
                ["-i", videoPath.PathExport, "-map", "0", "-c", "copy", "-bitexact", "-y", resultFileMkv.PathExport],
                TestContext.CancellationToken);
        }

        // Validate we have 3 streams still:
        (await GetStreamCount(videoPath.PathExport, TestContext.CancellationToken)).ShouldBe(3);
    }

    [TestMethod]
    [DataRow("tff")]
    [DataRow("bff")]
    public async Task TestDeinterlacing(string interlaceMode)
    {
        // This test creates an interlaced version of the big buck bunny file using ffmpeg's interlace filter,
        // then processes it through the library with ForceProgressiveFrames = true to validate de-interlacing works,
        // and copies both the interlaced and de-interlaced versions to a folder for manual inspection.

        var resultsDir = _appDir.CombineDirectory("TestDeinterlacingResults");
        resultsDir.Create();

        var interlacedFile = resultsDir.CombineFile($"interlaced_{interlaceMode}.mp4");
        var deinterlacedFile = resultsDir.CombineFile($"deinterlaced_{interlaceMode}.mp4");
        var originalFrameFile = resultsDir.CombineFile($"frame_original_{interlaceMode}.png");
        var deinterlacedFrameFile = resultsDir.CombineFile($"frame_deinterlaced_{interlaceMode}.png");
        interlacedFile.Delete();
        deinterlacedFile.Delete();
        originalFrameFile.Delete();
        deinterlacedFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);

        // Create an interlaced version of the original file using ffmpeg's interlace filter:
        // The interlace filter converts progressive video to interlaced, with tff (top field first) or bff (bottom field first).
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", $"interlace=scan={interlaceMode}:lowpass=complex",
                "-c:v", "libx264",
                "-x264-params", $"{interlaceMode}=1",
                "-c:a", "copy",
                "-y", interlacedFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Verify the interlaced file has interlaced field order:
        var (interlacedProbeOutput, _, interlacedProbeReturnCode) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", interlacedFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        interlacedProbeReturnCode.ShouldBe(0);

        // The interlaced file should have field_order set to "tt" (top first) or "bb" (bottom first):
        string expectedFieldOrder = interlaceMode == "tff" ? "\"field_order\": \"tt\"" : "\"field_order\": \"bb\"";
        interlacedProbeOutput.Contains(expectedFieldOrder, StringComparison.Ordinal).ShouldBeTrue();

        // The interlace filter combines two source frames into one interlaced frame, so the 60fps source should become 30fps:
        interlacedProbeOutput.Contains("\"r_frame_rate\": \"30/1\"", StringComparison.Ordinal).ShouldBeTrue();

        // Process the interlaced file through the library with ForceProgressiveFrames = true:
        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
        }).ToPipeline();

        await using var stream = interlacedFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        // Copy the de-interlaced file to the results directory:
        File.Copy(videoPath.PathExport, deinterlacedFile.PathExport);

        // Verify the de-interlaced file has progressive field order:
        var (deinterlacedProbeOutput, _, deinterlacedProbeReturnCode) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", deinterlacedFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        deinterlacedProbeReturnCode.ShouldBe(0);

        // The de-interlaced file should have field_order set to "progressive":
        deinterlacedProbeOutput.Contains(
            "\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue("Expected de-interlaced file to be progressive");

        // De-interlacing outputs one frame per field (bwdif send_field mode and its hardware equivalents), so the 30fps interlaced file should become 60fps:
        deinterlacedProbeOutput.Contains(
            "\"r_frame_rate\": \"60/1\"", StringComparison.Ordinal).ShouldBeTrue("Expected de-interlaced file to be 60fps (one frame per field)");

        // Extract comparison frames & ensure the de-interlaced output displays the same as the original progressive file (which also catches field order
        // mishandling, e.g. bff content de-interlaced as tff):
        await ExtractVideoFrame(origFile, originalFrameFile, 0.5);
        await ExtractVideoFrame(deinterlacedFile, deinterlacedFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(originalFrameFile, deinterlacedFrameFile, deinterlacedFrameFile.Name);
    }

#if !CI
    [TestMethod]
    [DataRow(BigBuckBunnyFullVideoFileName, AudioChannels.Stereo, AudioChannels.Mono)]
    public async Task TestAudioChannelDownmixQuality(string fileName, AudioChannels inputChannels, AudioChannels maxChannels)
    {
        // Note: this test is excluded from CI runs since it is intended for manual inspection only (audio channel downmixing is also tested elsewhere).
        // Tests audio channel downmixing quality for manual inspection. Outputs downmixed result files.

        var resultFile = _appDir.CombineDirectory("TestAudioChannelDownmixQualityResults").CombineFile($"{inputChannels}To{maxChannels}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MaxChannels = maxChannels,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId!)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);
    }
#endif

    [TestMethod]
    public async Task TestRotationMetadataHandling()
    {
        // This test creates a video that is physically rotated 90 degrees clockwise, but has rotation metadata set to -90 (to display correctly). It then
        // processes it with both preserve (both re-encoding & remuxing) and strip metadata modes to verify that rotation handling works correctly in all
        // cases. Re-encoding should bake the rotation into the video frames, while remuxing (including with metadata stripping, since the rotation is
        // functional metadata) should preserve the original frames and rotation metadata. Comparison frames are extracted from the input & outputs (as a
        // player would display them) to verify all outputs display the same as the input.

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestRotationMetadataResults");
        resultsDir.Create();

        var tempRotatedInputFile = resultsDir.CombineFile("temp_input_rotated.mp4");
        var rotatedInputFile = resultsDir.CombineFile("input_rotated_with_metadata.mp4");
        var outputPreserveMetadataRemuxed = resultsDir.CombineFile("output_preserve_metadata_remuxed.mp4");
        var outputPreserveMetadataReencoded = resultsDir.CombineFile("output_preserve_metadata_reencoded.mp4");
        var outputStripMetadata = resultsDir.CombineFile("output_strip_metadata.mp4");
        var inputFrameFile = resultsDir.CombineFile("frame_input.png");
        var reencodedFrameFile = resultsDir.CombineFile("frame_preserve_metadata_reencoded.png");
        var remuxedFrameFile = resultsDir.CombineFile("frame_preserve_metadata_remuxed.png");
        var stripMetadataFrameFile = resultsDir.CombineFile("frame_strip_metadata.png");
        tempRotatedInputFile.Delete();
        rotatedInputFile.Delete();
        outputPreserveMetadataRemuxed.Delete();
        outputPreserveMetadataReencoded.Delete();
        outputStripMetadata.Delete();
        inputFrameFile.Delete();
        reencodedFrameFile.Delete();
        remuxedFrameFile.Delete();
        stripMetadataFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a video that is physically rotated 90 degrees clockwise, with rotation metadata set to -90 so that it displays correctly when played.
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "transpose=1",
                "-c:v", "libx264",
                "-c:a", "copy",
                "-y", tempRotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-display_rotation", "90",
                "-i", tempRotatedInputFile.PathExport,
                "-c", "copy",
                "-y", rotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        tempRotatedInputFile.Delete();

        // Process with metadata preservation (MetadataStrippingMode.None) and forced re-encoding:
        var pipeline1 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream1 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream1, true, pipeline1, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);

        var videoPath1 = (await repo.GetAsync(fileId1)).Path;
        videoPath1.Exists.ShouldBeTrue();
        File.Copy(videoPath1.PathExport, outputPreserveMetadataReencoded.PathExport);

        // Process with metadata preservation (MetadataStrippingMode.None) and forced remuxing:
        var pipeline2 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream2 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream2, true, pipeline2, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);

        var videoPath2 = (await repo.GetAsync(fileId2)).Path;
        videoPath2.Exists.ShouldBeTrue();
        File.Copy(videoPath2.PathExport, outputPreserveMetadataRemuxed.PathExport);

        // Process with metadata stripping (MetadataStrippingMode.Required):
        var pipeline3 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();

        await using var stream3 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn3 = await repo.BeginTransactionAsync();
        var fileId3 = (await txn3.AddAsync(stream3, true, pipeline3, TestContext.CancellationToken)).FileId;
        await txn3.CommitAsync(TestContext.CancellationToken);

        var videoPath3 = (await repo.GetAsync(fileId3)).Path;
        videoPath3.Exists.ShouldBeTrue();
        File.Copy(videoPath3.PathExport, outputStripMetadata.PathExport);

        // Validate the dimensions and rotation metadata of the input & outputs:
        var (probeOutput0, _, probeReturnCode0) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", rotatedInputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode0.ShouldBe(0);

        var (probeOutput1, _, probeReturnCode1) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputPreserveMetadataReencoded.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode1.ShouldBe(0);

        var (probeOutput2, _, probeReturnCode2) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputPreserveMetadataRemuxed.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode2.ShouldBe(0);

        var (probeOutput3, _, probeReturnCode3) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputStripMetadata.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode3.ShouldBe(0);

        probeOutput0.Contains("\"width\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"height\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeTrue();

        probeOutput1.Contains("\"width\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"height\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeFalse();

        probeOutput2.Contains("\"width\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"height\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeTrue();

        probeOutput3.Contains("\"width\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput3.Contains("\"height\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput3.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeTrue();

        // Extract comparison frames & ensure all outputs display the same as the input:
        await ExtractVideoFrame(rotatedInputFile, inputFrameFile, 0.5);
        await ExtractVideoFrame(outputPreserveMetadataReencoded, reencodedFrameFile, 0.5);
        await ExtractVideoFrame(outputPreserveMetadataRemuxed, remuxedFrameFile, 0.5);
        await ExtractVideoFrame(outputStripMetadata, stripMetadataFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(inputFrameFile, reencodedFrameFile, "frame_preserve_metadata_reencoded.png");
        await CompareFrameToReferenceSSIM(inputFrameFile, remuxedFrameFile, "frame_preserve_metadata_remuxed.png");
        await CompareFrameToReferenceSSIM(inputFrameFile, stripMetadataFrameFile, "frame_strip_metadata.png");
    }

    [TestMethod]
    public async Task TestRotationMetadataResizingHandling()
    {
        // This test creates a video that is physically rotated 90 degrees clockwise, but has rotation metadata set to -90 (to display correctly).
        // We then resize it to a smaller resolution & verify that it is handled correctly.

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestRotationMetadataResizingResults");
        resultsDir.Create();

        var tempRotatedInputFile = resultsDir.CombineFile("temp_input_rotated.mp4");
        var rotatedInputFile = resultsDir.CombineFile("input_rotated_with_metadata.mp4");
        var outputPreserveMetadataReencoded = resultsDir.CombineFile("output_preserve_metadata_reencoded.mp4");
        var outputStripMetadata = resultsDir.CombineFile("output_strip_metadata.mp4");
        var inputFrameFile = resultsDir.CombineFile("frame_input.png");
        var reencodedFrameFile = resultsDir.CombineFile("frame_preserve_metadata_reencoded.png");
        var stripMetadataFrameFile = resultsDir.CombineFile("frame_strip_metadata.png");
        tempRotatedInputFile.Delete();
        rotatedInputFile.Delete();
        outputPreserveMetadataReencoded.Delete();
        outputStripMetadata.Delete();
        inputFrameFile.Delete();
        reencodedFrameFile.Delete();
        stripMetadataFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a video that is physically rotated 90 degrees clockwise, with rotation metadata set to -90 so that it displays correctly when played.
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "transpose=1",
                "-c:v", "libx264",
                "-c:a", "copy",
                "-y", tempRotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-display_rotation", "90",
                "-i", tempRotatedInputFile.PathExport,
                "-c", "copy",
                "-y", rotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        tempRotatedInputFile.Delete();

        // Process with metadata preservation (MetadataStrippingMode.None) and forced re-encoding:
        var pipeline1 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1280, 720),
        }).ToPipeline();

        await using var stream1 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream1, true, pipeline1, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);

        var videoPath1 = (await repo.GetAsync(fileId1)).Path;
        videoPath1.Exists.ShouldBeTrue();
        File.Copy(videoPath1.PathExport, outputPreserveMetadataReencoded.PathExport);

        // Process with metadata stripping (MetadataStrippingMode.Required):
        var pipeline2 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1280, 720),
        }).ToPipeline();

        await using var stream2 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream2, true, pipeline2, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);

        var videoPath2 = (await repo.GetAsync(fileId2)).Path;
        videoPath2.Exists.ShouldBeTrue();
        File.Copy(videoPath2.PathExport, outputStripMetadata.PathExport);

        // Validate that we don't have any rotation metadata & our size is correct
        var (probeOutput0, _, probeReturnCode0) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", rotatedInputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode0.ShouldBe(0);

        var (probeOutput1, _, probeReturnCode1) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputPreserveMetadataReencoded.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode1.ShouldBe(0);

        var (probeOutput2, _, probeReturnCode2) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputStripMetadata.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode2.ShouldBe(0);

        probeOutput0.Contains("\"width\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"height\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeTrue();

        probeOutput1.Contains("\"width\": 1280", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"height\": 720", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeFalse();

        probeOutput2.Contains("\"width\": 1280", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"height\": 720", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeFalse();

        // Extract comparison frames & ensure both outputs display the same as the input:
        await ExtractVideoFrame(rotatedInputFile, inputFrameFile, 0.5);
        await ExtractVideoFrame(outputPreserveMetadataReencoded, reencodedFrameFile, 0.5);
        await ExtractVideoFrame(outputStripMetadata, stripMetadataFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(inputFrameFile, reencodedFrameFile, "frame_preserve_metadata_reencoded.png");
        await CompareFrameToReferenceSSIM(inputFrameFile, stripMetadataFrameFile, "frame_strip_metadata.png");
    }

    [TestMethod]
#if !CI
    [DataRow("Y0__auYqGXY-20s")]
    [DataRow("video114")]
#endif
    [DataRow("Y0__auYqGXY-5s")]
    public async Task TestHDRToSDRMapping(string fileName)
    {
        // Note: most of this test is excluded from CI runs since it is primarily intended for manual inspection only (HDR->SDR logic is also tested in
        // TestStandardizedOptions).
        // Tests HDR to SDR color mapping. Outputs the result files for manual inspection.

        async Task ValidateStaleHDRMetadataExistence(IAbsoluteFilePath file, bool expected)
        {
            // The HDR mastering display / content light level metadata should have been stripped during the HDR->SDR conversion, since it no longer applies to
            // the tonemapped SDR frames. We check both the stream & first frame side data, since the metadata can be surfaced at either level depending on
            // whether it comes from the container or the codec SEI.
            string videoInfo = await RunFFtoolProcessWithErrorHandling(
                "ffprobe",
                [
                    "-i", file.PathExport, "-hide_banner", "-print_format", "json", "-select_streams", "v:0",
                    "-show_streams", "-show_frames", "-read_intervals", "%+#1", "-v", "error",
                ],
                TestContext.CancellationToken);

            try
            {
                videoInfo.Contains("Mastering display metadata", StringComparison.OrdinalIgnoreCase).ShouldBe(expected);
                videoInfo.Contains("Content light level metadata", StringComparison.OrdinalIgnoreCase).ShouldBe(expected);
            }
            catch (Exception ex)
            {
                throw new Exception("Video's metadata validation failed. Info: " + videoInfo, ex);
            }
        }

        var resultFileH264 = _appDir.CombineDirectory("TestHDRToSDRMappingResults").CombineFile(fileName + "-H264.mp4");
        resultFileH264.ParentDirectory?.Create();
        resultFileH264.Delete();

#if !CI
        var resultFileHEVC = _appDir.CombineDirectory("TestHDRToSDRMappingResults").CombineFile(fileName + "-HEVC.mp4");
        resultFileHEVC.ParentDirectory?.Create();
        resultFileHEVC.Delete();
#endif

        using var repoCtx = GetRepo(out var repo);

        var origFile = _videoFilesDir.CombineFile(fileName + ".mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await ValidateStaleHDRMetadataExistence(origFile, expected: true);

        var pipelineH264 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            RemapHDRToSDR = true,
            ResultVideoCodecs = [VideoCodec.H264],
        }).ToPipeline();

        await using var txnH264 = await repo.BeginTransactionAsync();
        var fileIdH264 = (await txnH264.AddAsync(stream, true, pipelineH264, TestContext.CancellationToken)).FileId;
        await txnH264.CommitAsync(TestContext.CancellationToken);

        var videoPathH264 = (await repo.GetAsync(fileIdH264)).Path;
        videoPathH264.Exists.ShouldBeTrue();

        File.Copy(videoPathH264.PathExport, resultFileH264.PathExport);

        await ValidateStaleHDRMetadataExistence(resultFileH264, expected: false);

#if !CI
        var pipelineHEVC = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            RemapHDRToSDR = true,
            ResultVideoCodecs = [VideoCodec.HEVC],
        }).ToPipeline();

        await using var txnHEVC = await repo.BeginTransactionAsync();
        var fileIdHEVC = (await txnHEVC.AddAsync(stream, true, pipelineHEVC, TestContext.CancellationToken)).FileId;
        await txnHEVC.CommitAsync(TestContext.CancellationToken);

        var videoPathHEVC = (await repo.GetAsync(fileIdHEVC)).Path;
        videoPathHEVC.Exists.ShouldBeTrue();

        File.Copy(videoPathHEVC.PathExport, resultFileHEVC.PathExport);

        await ValidateStaleHDRMetadataExistence(resultFileHEVC, expected: false);
#endif
    }

    [TestMethod]
    public async Task TestStartTimeMetadataHandling()
    {
        // This test creates a video that starts at 5s into the timeline, with start time metadata set accordingly. It then processes it with all of preserve (
        // both re-encoding & remuxing), strip metadata mode, and unrecognized stream stripping to verify that start time handling works correctly in all cases
        // we expect.
        // Note: we re-encode the video to 30fps, as quicktime seems to struggle with non-zero start times at 60fps.
        // The expected result is that only 'output_strip_metadata.mp4' loses the start time metadata, while all other outputs retain it.

        async Task ValidateMetadata(IAbsoluteFilePath file, bool shouldHaveStartTime)
        {
            string videoInfo = await RunFFtoolProcessWithErrorHandling(
                "ffprobe",
                ["-i", file.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                TestContext.CancellationToken);

            try
            {
                videoInfo.Contains("\"timecode\": \"00:00:05;00\"", StringComparison.Ordinal).ShouldBe(shouldHaveStartTime);
            }
            catch (Exception ex)
            {
                throw new Exception("Video's metadata validation failed. Info: " + videoInfo, ex);
            }
        }

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestStartTimeMetadataResults");
        resultsDir.Create();

        var offsettedInputFile = resultsDir.CombineFile("input_offsetted_with_metadata.mp4");
        var outputPreserveMetadataRemuxed = resultsDir.CombineFile("output_preserve_metadata_remuxed.mp4");
        var outputPreserveMetadataReencoded = resultsDir.CombineFile("output_preserve_metadata_reencoded.mp4");
        var outputStripMetadata = resultsDir.CombineFile("output_strip_metadata.mp4");
        var outputStripUnrecognizedStreams = resultsDir.CombineFile("output_strip_unrecognized_streams.mp4");
        offsettedInputFile.Delete();
        outputPreserveMetadataRemuxed.Delete();
        outputPreserveMetadataReencoded.Delete();
        outputStripMetadata.Delete();
        outputStripUnrecognizedStreams.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a video with the timecode set to start at 5s.
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "fps=fps=30",
                "-timecode", "00:00:05.00",
                "-r", "30",
                "-c:a", "copy",
                "-c:v", "libx264",
                "-y", offsettedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await ValidateMetadata(offsettedInputFile, shouldHaveStartTime: true);

        await using var stream = offsettedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        // Process with metadata preservation (MetadataStrippingMode.None) and forced re-encoding:
        var pipeline1 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream, true, pipeline1, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);

        var videoPath1 = (await repo.GetAsync(fileId1)).Path;
        videoPath1.Exists.ShouldBeTrue();
        File.Copy(videoPath1.PathExport, outputPreserveMetadataReencoded.PathExport);
        await ValidateMetadata(outputPreserveMetadataReencoded, shouldHaveStartTime: true);

        // Process with metadata preservation (MetadataStrippingMode.None) and forced remuxing:
        var pipeline2 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();
        stream.Position = 0;

        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream, true, pipeline2, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);

        var videoPath2 = (await repo.GetAsync(fileId2)).Path;
        videoPath2.Exists.ShouldBeTrue();
        File.Copy(videoPath2.PathExport, outputPreserveMetadataRemuxed.PathExport);
        await ValidateMetadata(outputPreserveMetadataRemuxed, shouldHaveStartTime: true);

        // Process with metadata stripping (MetadataStrippingMode.Required):
        var pipeline3 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();
        stream.Position = 0;

        await using var txn3 = await repo.BeginTransactionAsync();
        var fileId3 = (await txn3.AddAsync(stream, true, pipeline3, TestContext.CancellationToken)).FileId;
        await txn3.CommitAsync(TestContext.CancellationToken);

        var videoPath3 = (await repo.GetAsync(fileId3)).Path;
        videoPath3.Exists.ShouldBeTrue();
        File.Copy(videoPath3.PathExport, outputStripMetadata.PathExport);
        await ValidateMetadata(outputStripMetadata, shouldHaveStartTime: false);

        // Process with unrecognized stream stripping (TryPreserveUnrecognizedStreams = false):
        var pipeline4 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            TryPreserveUnrecognizedStreams = false,
        }).ToPipeline();
        stream.Position = 0;

        await using var txn4 = await repo.BeginTransactionAsync();
        var fileId4 = (await txn4.AddAsync(stream, true, pipeline4, TestContext.CancellationToken)).FileId;
        await txn4.CommitAsync(TestContext.CancellationToken);

        var videoPath4 = (await repo.GetAsync(fileId4)).Path;
        videoPath4.Exists.ShouldBeTrue();
        File.Copy(videoPath4.PathExport, outputStripUnrecognizedStreams.PathExport);
        await ValidateMetadata(outputStripUnrecognizedStreams, shouldHaveStartTime: true);
    }

    [TestMethod]
    public async Task TestLimitedToFullRangeConversion()
    {
        // This test creates a solid color (#95eb14) video in full (pc) color range, converts it to limited (tv) range (in 8-bit, 10-bit, and 8-bit interlaced
        // versions), then processes the limited range versions through the library with forced re-encoding to validate that the outputs get converted back to
        // full (pc) range (as well as back to 8-bit for the 10-bit version, de-interlaced for the interlaced version, and at the correct size when resizing).
        // It also extracts a video frame from the limited range file using VideoFrameExtractionProcessor to validate that frame extraction handles limited
        // range input correctly. Frames are extracted from each result and compared against a frame from the original full range video, ensuring all pixel
        // color channels are within 3% of each other.
        // All files are kept in the results folder for manual inspection.

        var resultsDir = _appDir.CombineDirectory("TestLimitedToFullRangeConversionResults");
        resultsDir.Create();

        var fullRangeFile = resultsDir.CombineFile("input_full_range.mp4");
        var limitedRangeFile = resultsDir.CombineFile("input_limited_range.mp4");
        var limitedRange10BitFile = resultsDir.CombineFile("input_limited_range_10bit.mp4");
        var limitedRangeInterlacedFile = resultsDir.CombineFile("input_limited_range_interlaced.mp4");
        var limitedRangeOversizedFile = resultsDir.CombineFile("input_limited_range_oversized.mp4");
        var processedFile = resultsDir.CombineFile("output_processed_full_range.mp4");
        var processed8BitFile = resultsDir.CombineFile("output_processed_full_range_8bit.mp4");
        var processedDeinterlacedFile = resultsDir.CombineFile("output_processed_full_range_deinterlaced.mp4");
        var processedScaledFile = resultsDir.CombineFile("output_processed_full_range_scaled.mp4");
        var fullRangeFrameFile = resultsDir.CombineFile("frame_full_range.png");
        var processedFrameFile = resultsDir.CombineFile("frame_processed.png");
        var processed8BitFrameFile = resultsDir.CombineFile("frame_processed_8bit.png");
        var processedDeinterlacedFrameFile = resultsDir.CombineFile("frame_processed_deinterlaced.png");
        var processedScaledFrameFile = resultsDir.CombineFile("frame_processed_scaled.png");
        var extractedFrameFile = resultsDir.CombineFile("frame_extracted_limited_range.png");
        fullRangeFile.Delete();
        limitedRangeFile.Delete();
        limitedRange10BitFile.Delete();
        limitedRangeInterlacedFile.Delete();
        limitedRangeOversizedFile.Delete();
        processedFile.Delete();
        processed8BitFile.Delete();
        processedDeinterlacedFile.Delete();
        processedScaledFile.Delete();
        fullRangeFrameFile.Delete();
        processedFrameFile.Delete();
        processed8BitFrameFile.Delete();
        processedDeinterlacedFrameFile.Delete();
        processedScaledFrameFile.Delete();
        extractedFrameFile.Delete();

        // Local helper to get the ffprobe stream info for a file:
        async Task<string> ProbeStreams(IAbsoluteFilePath file)
        {
            var (output, _, returnCode) = await RunFFtoolProcess(
                "ffprobe",
                ["-i", file.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                TestContext.CancellationToken);
            returnCode.ShouldBe(0);
            return output;
        }

        // Create a 2 second solid #95eb14 video in full (pc) range:
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-f", "lavfi",
                "-i", "color=c=0x95eb14:size=320x240:rate=30:duration=2",
                "-vf", "format=pix_fmts=yuvj420p:color_ranges=pc",
                "-c:v", "libx264",
                "-color_range", "pc",
                "-y", fullRangeFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Create with limited (tv) range, 8-bit version:
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-f", "lavfi",
                "-i", "color=c=0x95eb14:size=320x240:rate=30:duration=2",
                "-vf", "format=pix_fmts=yuv420p:color_ranges=tv",
                "-c:v", "libx264",
                "-color_range", "tv",
                "-y", limitedRangeFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Create with limited (tv) range, 10-bit version:
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-f", "lavfi",
                "-i", "color=c=0x95eb14:size=320x240:rate=30:duration=2",
                "-vf", "format=pix_fmts=yuv420p10le:color_ranges=tv",
                "-c:v", "libx265",
                "-tag:v", "hvc1",
                "-color_range", "tv",
                "-y", limitedRange10BitFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Create with limited (tv) range, 8-bit interlaced (tff) version:
        // Note: the interlace filter halves the frame rate (60fps to 30fps), and its vertical lowpass has no effect on a solid color, so the frame comparison
        // below remains valid.
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-f", "lavfi",
                "-i", "color=c=0x95eb14:size=320x240:rate=60:duration=2",
                "-vf", "format=pix_fmts=yuv420p:color_ranges=tv,interlace=scan=tff:lowpass=complex",
                "-c:v", "libx264",
                "-x264-params", "tff=1",
                "-color_range", "tv",
                "-y", limitedRangeInterlacedFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Create with limited (tv) range, 8-bit oversized (640x480) version, so that scaling back down to the reference size can be tested:
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-f", "lavfi",
                "-i", "color=c=0x95eb14:size=640x480:rate=30:duration=2",
                "-vf", "format=pix_fmts=yuv420p:color_ranges=tv",
                "-c:v", "libx264",
                "-color_range", "tv",
                "-y", limitedRangeOversizedFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Sanity check the color ranges (and pixel formats) of the input files:
        string fullRangeProbeOutput = await ProbeStreams(fullRangeFile);
        fullRangeProbeOutput.Contains("\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeTrue("Expected full range input file to be pc range");
        fullRangeProbeOutput.Contains("\"pix_fmt\": \"yuvj420p\"", StringComparison.Ordinal).ShouldBeTrue("Expected full range input file to be yuvj420p");

        string limitedRangeProbeOutput = await ProbeStreams(limitedRangeFile);
        limitedRangeProbeOutput.Contains(
            "\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeFalse("Expected limited range input file to be tv range");
        limitedRangeProbeOutput.Contains(
            "\"pix_fmt\": \"yuv420p\"", StringComparison.Ordinal).ShouldBeTrue("Expected limited range input file to be yuv420p");

        string limitedRange10BitProbeOutput = await ProbeStreams(limitedRange10BitFile);
        limitedRange10BitProbeOutput.Contains(
            "\"color_range\": \"tv\"", StringComparison.Ordinal).ShouldBeTrue("Expected 10-bit limited range input file to be tv range");
        limitedRange10BitProbeOutput.Contains(
            "\"pix_fmt\": \"yuv420p10le\"", StringComparison.Ordinal).ShouldBeTrue("Expected 10-bit limited range input file to be 10-bit");

        string limitedRangeInterlacedProbeOutput = await ProbeStreams(limitedRangeInterlacedFile);
        limitedRangeInterlacedProbeOutput.Contains(
            "\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeFalse("Expected interlaced limited range input file to be tv range");
        limitedRangeInterlacedProbeOutput.Contains(
            "\"pix_fmt\": \"yuv420p\"", StringComparison.Ordinal).ShouldBeTrue("Expected interlaced limited range input file to be yuv420p");
        limitedRangeInterlacedProbeOutput.Contains(
            "\"field_order\": \"tt\"", StringComparison.Ordinal).ShouldBeTrue("Expected interlaced limited range input file to be interlaced (tff)");

        string limitedRangeOversizedProbeOutput = await ProbeStreams(limitedRangeOversizedFile);
        limitedRangeOversizedProbeOutput.Contains(
            "\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeFalse("Expected oversized limited range input file to be tv range");
        limitedRangeOversizedProbeOutput.Contains(
            "\"pix_fmt\": \"yuv420p\"", StringComparison.Ordinal).ShouldBeTrue("Expected oversized limited range input file to be yuv420p");
        limitedRangeOversizedProbeOutput.Contains(
            "\"width\": 640", StringComparison.Ordinal).ShouldBeTrue("Expected oversized limited range input file to be 640 wide");

        // Extract the reference frame from the middle of the original full range video (also kept for manual inspection):
        await ExtractVideoFrame(fullRangeFile, fullRangeFrameFile, 1.0);

        using var repoCtx = GetRepo(out var repo);

        // Case 1: process the limited range file through the library with forced re-encoding, and verify it gets converted back to full (pc) range:

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream = limitedRangeFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, processedFile.PathExport);

        string processedProbeOutput = await ProbeStreams(processedFile);
        processedProbeOutput.Contains(
            "\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeTrue("Expected processed file to be converted to pc range");

        await ExtractVideoFrame(processedFile, processedFrameFile, 1.0);

        CompareFrameToReference(fullRangeFrameFile, processedFrameFile, "frame_processed.png", tolerance: 0.03);

        // Case 2: process the 10-bit limited range file with a maximum of 8 bits per channel, and verify it gets converted to an 8-bit full (pc) range
        // result:

        var pipeline8Bit = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            MaximumBitsPerChannel = BitsPerChannel.Bits8,
        }).ToPipeline();

        await using var stream8Bit = limitedRange10BitFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn8Bit = await repo.BeginTransactionAsync();
        var fileId8Bit = (await txn8Bit.AddAsync(stream8Bit, true, pipeline8Bit, TestContext.CancellationToken)).FileId;
        await txn8Bit.CommitAsync(TestContext.CancellationToken);

        var videoPath8Bit = (await repo.GetAsync(fileId8Bit)).Path;
        videoPath8Bit.Exists.ShouldBeTrue();

        File.Copy(videoPath8Bit.PathExport, processed8BitFile.PathExport);

        string processed8BitProbeOutput = await ProbeStreams(processed8BitFile);
        processed8BitProbeOutput.Contains(
            "\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeTrue("Expected 8-bit processed file to be converted to pc range");
        processed8BitProbeOutput.Contains(
            "\"pix_fmt\": \"yuvj420p\"", StringComparison.Ordinal).ShouldBeTrue("Expected 8-bit processed file to be converted to 8-bit");

        await ExtractVideoFrame(processed8BitFile, processed8BitFrameFile, 1.0);

        CompareFrameToReference(fullRangeFrameFile, processed8BitFrameFile, "frame_processed_8bit.png", tolerance: 0.03);

        // Case 3: process the interlaced limited range file with forced progressive frames, and verify it gets converted to a de-interlaced full (pc) range
        // result:

        var pipelineDeinterlaced = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
        }).ToPipeline();

        await using var streamInterlaced = limitedRangeInterlacedFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txnInterlaced = await repo.BeginTransactionAsync();
        var fileIdInterlaced = (await txnInterlaced.AddAsync(streamInterlaced, true, pipelineDeinterlaced, TestContext.CancellationToken)).FileId;
        await txnInterlaced.CommitAsync(TestContext.CancellationToken);

        var videoPathInterlaced = (await repo.GetAsync(fileIdInterlaced)).Path;
        videoPathInterlaced.Exists.ShouldBeTrue();

        File.Copy(videoPathInterlaced.PathExport, processedDeinterlacedFile.PathExport);

        string processedDeinterlacedProbeOutput = await ProbeStreams(processedDeinterlacedFile);
        processedDeinterlacedProbeOutput.Contains(
            "\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeTrue("Expected de-interlaced processed file to be converted to pc range");
        processedDeinterlacedProbeOutput.Contains(
            "\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue("Expected de-interlaced processed file to be progressive");

        await ExtractVideoFrame(processedDeinterlacedFile, processedDeinterlacedFrameFile, 1.0);

        CompareFrameToReference(fullRangeFrameFile, processedDeinterlacedFrameFile, "frame_processed_deinterlaced.png", tolerance: 0.03);

        // Case 4: process the oversized limited range file with resizing, and verify it gets converted to a full (pc) range result scaled down to the
        // reference size:

        var pipelineScaled = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 320, 240),
        }).ToPipeline();

        await using var streamScaled = limitedRangeOversizedFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txnScaled = await repo.BeginTransactionAsync();
        var fileIdScaled = (await txnScaled.AddAsync(streamScaled, true, pipelineScaled, TestContext.CancellationToken)).FileId;
        await txnScaled.CommitAsync(TestContext.CancellationToken);

        var videoPathScaled = (await repo.GetAsync(fileIdScaled)).Path;
        videoPathScaled.Exists.ShouldBeTrue();

        File.Copy(videoPathScaled.PathExport, processedScaledFile.PathExport);

        string processedScaledProbeOutput = await ProbeStreams(processedScaledFile);
        processedScaledProbeOutput.Contains(
            "\"color_range\": \"pc\"", StringComparison.Ordinal).ShouldBeTrue("Expected scaled processed file to be converted to pc range");
        processedScaledProbeOutput.Contains(
            "\"width\": 320", StringComparison.Ordinal).ShouldBeTrue("Expected scaled processed file to be 320 wide");
        processedScaledProbeOutput.Contains(
            "\"height\": 240", StringComparison.Ordinal).ShouldBeTrue("Expected scaled processed file to be 240 tall");

        await ExtractVideoFrame(processedScaledFile, processedScaledFrameFile, 1.0);

        CompareFrameToReference(fullRangeFrameFile, processedScaledFrameFile, "frame_processed_scaled.png", tolerance: 0.03);

        // Case 5: extract a video frame from the limited range file using VideoFrameExtractionProcessor, and verify its colors are correct too:

        var framePipeline = new VideoFrameExtractionProcessor(VideoFrameExtractionProcessingOptions.Standard with
        {
            ImageTimestampFraction = 0.5,
        }).ToPipeline();

        await using var frameStream = limitedRangeFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var frameTxn = await repo.BeginTransactionAsync();
        var frameFileId = (await frameTxn.AddAsync(frameStream, true, framePipeline, TestContext.CancellationToken)).FileId;
        await frameTxn.CommitAsync(TestContext.CancellationToken);

        var extractedImagePath = (await repo.GetAsync(frameFileId)).Path;
        extractedImagePath.Exists.ShouldBeTrue();

        File.Copy(extractedImagePath.PathExport, extractedFrameFile.PathExport);

        CompareFrameToReference(fullRangeFrameFile, extractedFrameFile, "frame_extracted_limited_range.png", tolerance: 0.03);
    }

    [TestMethod]
    public async Task TestSarInterlacedResizingHandling()
    {
        // This test creates an interlaced (tff) 64x60 video with a non-square 3:4 SAR (i.e., a 48x60 display size), then processes it through the library
        // with ForceProgressiveFrames = true and resizing with both square pixel modes, verifying that de-interlacing, SAR, and resizing are handled correctly
        // together:
        // - With ForceSquarePixels = true, the pixels should be made square while resizing, so fitting the 48x60 display size within 20x20 should produce a
        //   16x20 result with a 1:1 SAR.
        // - With ForceSquarePixels = false, the 64x60 coded frame should just be scaled directly to fit within 16x16 (15x16, which becomes 16x16 after
        //   rounding to even dimensions), with no extra SAR-based scaling - the SAR instead ends up adjusted to preserve the display aspect ratio
        //   (3:4 * (64/16)/(60/16) = 4:5).
        // Both outputs should also be de-interlaced (progressive, at double the interlaced frame rate) and are kept for manual inspection.
        // Two more outputs are also produced from a full-resolution (1920x1080) version of the same interlaced 3:4 SAR input, resized to roughly half size,
        // so that the results are large enough to be usefully inspected:
        // - With ForceSquarePixels = true, fitting the 1440x1080 display size within 720x540 produces exactly 720x540 with a 1:1 SAR.
        // - With ForceSquarePixels = false, the 1920x1080 coded frame is scaled directly to 960x540, with the SAR unchanged at 3:4 since an exact half-size
        //   scale preserves the display aspect ratio without any SAR adjustment.

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestSarInterlacedResizingResults");
        resultsDir.Create();

        var interlacedInputFile = resultsDir.CombineFile("input_interlaced_with_sar.mp4");
        var largeInterlacedInputFile = resultsDir.CombineFile("input_interlaced_with_sar_large.mp4");
        var outputSquarePixels = resultsDir.CombineFile("output_square_pixels.mp4");
        var outputNonSquarePixels = resultsDir.CombineFile("output_non_square_pixels.mp4");
        var outputSquarePixelsLarge = resultsDir.CombineFile("output_square_pixels_large.mp4");
        var outputNonSquarePixelsLarge = resultsDir.CombineFile("output_non_square_pixels_large.mp4");
        var largeInputFrameFile = resultsDir.CombineFile("frame_original_large.png");
        var squarePixelsLargeFrameFile = resultsDir.CombineFile("frame_square_pixels_large.png");
        var nonSquarePixelsLargeFrameFile = resultsDir.CombineFile("frame_non_square_pixels_large.png");
        interlacedInputFile.Delete();
        largeInterlacedInputFile.Delete();
        outputSquarePixels.Delete();
        outputNonSquarePixels.Delete();
        outputSquarePixelsLarge.Delete();
        outputNonSquarePixelsLarge.Delete();
        largeInputFrameFile.Delete();
        squarePixelsLargeFrameFile.Delete();
        nonSquarePixelsLargeFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create an interlaced version of the original file at 64x60 with a 3:4 SAR:
        // The interlace filter converts progressive video to interlaced (halving the frame rate from 60fps to 30fps).
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "scale=w=64:h=60:force_original_aspect_ratio=disable,interlace=scan=tff:lowpass=complex,setsar=3/4",
                "-c:v", "libx264",
                "-x264-params", "tff=1",
                "-c:a", "copy",
                "-y", interlacedInputFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Create an interlaced version of the original file at its full 1920x1080 resolution with a 3:4 SAR:
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "interlace=scan=tff:lowpass=complex,setsar=3/4",
                "-c:v", "libx264",
                "-x264-params", "tff=1",
                "-c:a", "copy",
                "-y", largeInterlacedInputFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Process with square pixels forced:
        var pipeline1 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
            ForceSquarePixels = true,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 20, 20),
        }).ToPipeline();

        await using var stream1 = interlacedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream1, true, pipeline1, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);

        var videoPath1 = (await repo.GetAsync(fileId1)).Path;
        videoPath1.Exists.ShouldBeTrue();
        File.Copy(videoPath1.PathExport, outputSquarePixels.PathExport);

        // Process without square pixels forced:
        var pipeline2 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
            ForceSquarePixels = false,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 16, 16),
        }).ToPipeline();

        await using var stream2 = interlacedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream2, true, pipeline2, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);

        var videoPath2 = (await repo.GetAsync(fileId2)).Path;
        videoPath2.Exists.ShouldBeTrue();
        File.Copy(videoPath2.PathExport, outputNonSquarePixels.PathExport);

        // Process the large input with square pixels forced, resizing to half the display size:
        var pipeline3 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
            ForceSquarePixels = true,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 720, 540),
        }).ToPipeline();

        await using var stream3 = largeInterlacedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn3 = await repo.BeginTransactionAsync();
        var fileId3 = (await txn3.AddAsync(stream3, true, pipeline3, TestContext.CancellationToken)).FileId;
        await txn3.CommitAsync(TestContext.CancellationToken);

        var videoPath3 = (await repo.GetAsync(fileId3)).Path;
        videoPath3.Exists.ShouldBeTrue();
        File.Copy(videoPath3.PathExport, outputSquarePixelsLarge.PathExport);

        // Process the large input without square pixels forced, resizing to half the coded size:
        var pipeline4 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
            ForceSquarePixels = false,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 960, 540),
        }).ToPipeline();

        await using var stream4 = largeInterlacedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn4 = await repo.BeginTransactionAsync();
        var fileId4 = (await txn4.AddAsync(stream4, true, pipeline4, TestContext.CancellationToken)).FileId;
        await txn4.CommitAsync(TestContext.CancellationToken);

        var videoPath4 = (await repo.GetAsync(fileId4)).Path;
        videoPath4.Exists.ShouldBeTrue();
        File.Copy(videoPath4.PathExport, outputNonSquarePixelsLarge.PathExport);

        // Validate the input file has the expected dimensions, SAR, and interlacing:
        var (probeOutput0, _, probeReturnCode0) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", interlacedInputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode0.ShouldBe(0);

        var (probeOutput1, _, probeReturnCode1) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputSquarePixels.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode1.ShouldBe(0);

        var (probeOutput2, _, probeReturnCode2) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputNonSquarePixels.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode2.ShouldBe(0);

        var (probeOutput3, _, probeReturnCode3) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputSquarePixelsLarge.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode3.ShouldBe(0);

        var (probeOutput4, _, probeReturnCode4) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputNonSquarePixelsLarge.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode4.ShouldBe(0);

        probeOutput0.Contains("\"width\": 64", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"height\": 60", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"sample_aspect_ratio\": \"3:4\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"field_order\": \"tt\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"r_frame_rate\": \"30/1\"", StringComparison.Ordinal).ShouldBeTrue();

        // With square pixels forced, the video should be de-interlaced and have its pixels made square while resizing:
        probeOutput1.Contains("\"width\": 16", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"height\": 20", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"sample_aspect_ratio\": \"1:1\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"r_frame_rate\": \"60/1\"", StringComparison.Ordinal).ShouldBeTrue();

        // Without square pixels forced, the video should be de-interlaced and the coded frame scaled directly, with the SAR adjusted to preserve the display
        // aspect ratio:
        probeOutput2.Contains("\"width\": 16", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"height\": 16", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"sample_aspect_ratio\": \"4:5\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput2.Contains("\"r_frame_rate\": \"60/1\"", StringComparison.Ordinal).ShouldBeTrue();

        // The large output with square pixels forced should be de-interlaced with its pixels made square while resizing to half the 1440x1080 display size:
        probeOutput3.Contains("\"width\": 720", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput3.Contains("\"height\": 540", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput3.Contains("\"sample_aspect_ratio\": \"1:1\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput3.Contains("\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput3.Contains("\"r_frame_rate\": \"60/1\"", StringComparison.Ordinal).ShouldBeTrue();

        // The large output without square pixels forced should be de-interlaced and the coded frame scaled directly to half size, with the SAR unchanged
        // since an exact half-size scale preserves the display aspect ratio:
        probeOutput4.Contains("\"width\": 960", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput4.Contains("\"height\": 540", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput4.Contains("\"sample_aspect_ratio\": \"3:4\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput4.Contains("\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput4.Contains("\"r_frame_rate\": \"60/1\"", StringComparison.Ordinal).ShouldBeTrue();

        // Extract comparison frames & ensure the large outputs display the same as the original. The original file is used as the reference (rather than
        // the interlaced input, whose lowpass-filtered content would make for a poorer reference) - it has the same 1920x1080 coded frame but square pixels,
        // which still compares correctly since frame extraction does not apply the SAR (the PNGs keep the coded dimensions), while the SSIM comparison scales
        // the larger frame down to the smaller frame's exact dimensions, applying the appropriate SAR correction for each pair:
        // - Original (1920x1080, square pixels) vs square pixels output (720x540, 1:1 SAR): the non-uniform 1920x1080 -> 720x540 scale applies the same 3:4
        //   SAR squeeze that the library applied while resizing.
        // - Original (1920x1080, square pixels) vs non-square pixels output (960x540, 3:4 SAR): a uniform half-size scale, since the output's coded frame was
        //   scaled directly without any SAR-based scaling.
        await ExtractVideoFrame(origFile, largeInputFrameFile, 0.5);
        await ExtractVideoFrame(outputSquarePixelsLarge, squarePixelsLargeFrameFile, 0.5);
        await ExtractVideoFrame(outputNonSquarePixelsLarge, nonSquarePixelsLargeFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(largeInputFrameFile, squarePixelsLargeFrameFile, squarePixelsLargeFrameFile.Name);
        await CompareFrameToReferenceSSIM(largeInputFrameFile, nonSquarePixelsLargeFrameFile, nonSquarePixelsLargeFrameFile.Name);
    }

    [TestMethod]
    [DataRow(90)]
    [DataRow(180)]
    [DataRow(-90)]
    public async Task TestRotationDegreesHandling(int displayRotation)
    {
        // This test creates a video that is physically rotated by the opposite of the given display rotation, with rotation metadata set to the given display
        // rotation so that it displays correctly when played. It then re-encodes it through the library to verify that the rotation gets baked into the video
        // frames correctly for every rotation amount, comparing an extracted frame against one from the input (as a player would display them).

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestRotationDegreesResults");
        resultsDir.Create();

        var tempRotatedInputFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"temp_input_rotated_{displayRotation}.mp4"));
        var rotatedInputFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"input_rotated_{displayRotation}.mp4"));
        var outputFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"output_{displayRotation}.mp4"));
        var inputFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_input_{displayRotation}.png"));
        var outputFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_output_{displayRotation}.png"));
        tempRotatedInputFile.Delete();
        rotatedInputFile.Delete();
        outputFile.Delete();
        inputFrameFile.Delete();
        outputFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a video that is physically rotated by the opposite of the display rotation, with rotation metadata set to the display rotation so that it
        // displays correctly when played.
        string physicalRotationFilter = displayRotation switch
        {
            90 => "transpose=1",
            -90 => "transpose=2",
            _ => "vflip,hflip",
        };
        (int inputWidth, int inputHeight) = displayRotation is 90 or -90 ? (1080, 1920) : (1920, 1080);

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", physicalRotationFilter,
                "-c:v", "libx264",
                "-c:a", "copy",
                "-y", tempRotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-display_rotation", displayRotation.ToString(CultureInfo.InvariantCulture),
                "-i", tempRotatedInputFile.PathExport,
                "-c", "copy",
                "-y", rotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        tempRotatedInputFile.Delete();

        // Process with forced re-encoding:
        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();
        File.Copy(videoPath.PathExport, outputFile.PathExport);

        // Validate the dimensions and rotation metadata of the input & output:
        var (probeOutput0, _, probeReturnCode0) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", rotatedInputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode0.ShouldBe(0);

        var (probeOutput1, _, probeReturnCode1) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode1.ShouldBe(0);

        probeOutput0.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {inputWidth}"), StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {inputHeight}"), StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeTrue();

        probeOutput1.Contains("\"width\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"height\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeFalse();

        // Extract comparison frames & ensure the output displays the same as the input:
        await ExtractVideoFrame(rotatedInputFile, inputFrameFile, 0.5);
        await ExtractVideoFrame(outputFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(
            inputFrameFile, outputFrameFile, string.Create(CultureInfo.InvariantCulture, $"frame_output_{displayRotation}.png"));
    }

    [TestMethod]
    public async Task TestRotationDeinterlacingHandling()
    {
        // This test creates an interlaced (tff) video that is physically rotated 90 degrees clockwise, but has rotation metadata set to -90 (to display
        // correctly). It then processes it through the library with ForceProgressiveFrames = true and forced re-encoding to verify that de-interlacing and
        // rotation are handled correctly together, comparing an extracted frame against one from the original progressive un-rotated video (as a player would
        // display them).
        // Note: original file should be played in VLC for most likely chance that it is interpreted correctly.

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestRotationDeinterlacingResults");
        resultsDir.Create();

        var tempRotatedInputFile = resultsDir.CombineFile("temp_input_rotated_interlaced.mp4");
        var rotatedInputFile = resultsDir.CombineFile("input_rotated_interlaced.mp4");
        var outputFile = resultsDir.CombineFile("output_deinterlaced.mp4");
        var originalFrameFile = resultsDir.CombineFile("frame_original.png");
        var outputFrameFile = resultsDir.CombineFile("frame_output.png");
        tempRotatedInputFile.Delete();
        rotatedInputFile.Delete();
        outputFile.Delete();
        originalFrameFile.Delete();
        outputFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create an interlaced video that is physically rotated 90 degrees clockwise, with rotation metadata set to -90 so that it displays correctly when
        // played. The interlace filter converts progressive video to interlaced (halving the frame rate from 60fps to 30fps).
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "transpose=1,interlace=scan=tff:lowpass=complex",
                "-c:v", "libx264",
                "-x264-params", "tff=1",
                "-c:a", "copy",
                "-y", tempRotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-display_rotation", "90",
                "-i", tempRotatedInputFile.PathExport,
                "-c", "copy",
                "-y", rotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        tempRotatedInputFile.Delete();

        // Process with forced progressive frames and re-encoding:
        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
        }).ToPipeline();

        await using var stream = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();
        File.Copy(videoPath.PathExport, outputFile.PathExport);

        // Validate the dimensions, rotation metadata, interlacing, and frame rate of the input & output:
        var (probeOutput0, _, probeReturnCode0) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", rotatedInputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode0.ShouldBe(0);

        var (probeOutput1, _, probeReturnCode1) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode1.ShouldBe(0);

        probeOutput0.Contains("\"width\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"height\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"field_order\": \"tt\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"r_frame_rate\": \"30/1\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeTrue();

        probeOutput1.Contains("\"width\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"height\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"r_frame_rate\": \"60/1\"", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeFalse();

        // Extract comparison frames & ensure the output displays the same as the original:
        // Note: the original file is used as the reference (rather than the interlaced input) since de-interlacing an extracted frame of the rotated input
        // would operate on the wrong field orientation after the extraction auto-rotates it.
        await ExtractVideoFrame(origFile, originalFrameFile, 0.5);
        await ExtractVideoFrame(outputFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(originalFrameFile, outputFrameFile, "frame_output.png");
    }

    [TestMethod]
    public async Task TestHEVCMinimumSizeUpscalingHandling()
    {
        // This test creates a tiny 14x8 video (below the 16 pixel minimum dimension that HEVC encoding requires), then re-encodes it to HEVC through the
        // library to verify that it gets upscaled to meet the minimum dimension requirement (28x16, preserving the aspect ratio) and still looks correct,
        // comparing an extracted frame against one from the input (as a player would display them).

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestHEVCMinimumSizeUpscalingResults");
        resultsDir.Create();

        var smallInputFile = resultsDir.CombineFile("input_small.mp4");
        var outputFile = resultsDir.CombineFile("output_upscaled.mp4");
        var inputFrameFile = resultsDir.CombineFile("frame_input.png");
        var outputFrameFile = resultsDir.CombineFile("frame_output.png");
        smallInputFile.Delete();
        outputFile.Delete();
        inputFrameFile.Delete();
        outputFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a tiny version of the original file that is below the HEVC minimum dimension:
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "scale=w=14:h=8:force_original_aspect_ratio=disable,setsar=1",
                "-c:v", "libx264",
                "-c:a", "copy",
                "-y", smallInputFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Process with forced re-encoding to HEVC:
        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.HEVC],
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream = smallInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();
        File.Copy(videoPath.PathExport, outputFile.PathExport);

        // Validate the dimensions of the input & output:
        var (probeOutput0, _, probeReturnCode0) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", smallInputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode0.ShouldBe(0);

        var (probeOutput1, _, probeReturnCode1) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode1.ShouldBe(0);

        probeOutput0.Contains("\"width\": 14", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"height\": 8", StringComparison.Ordinal).ShouldBeTrue();

        probeOutput1.Contains("\"width\": 28", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"height\": 16", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"codec_name\": \"hevc\"", StringComparison.Ordinal).ShouldBeTrue();

        // Extract comparison frames & ensure the output displays the same as the input:
        await ExtractVideoFrame(smallInputFile, inputFrameFile, 0.5);
        await ExtractVideoFrame(outputFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(inputFrameFile, outputFrameFile, "frame_output.png");
    }

    [TestMethod]
    [DataRow("hflip")]
    [DataRow("vflip")]
    public async Task TestFlipMetadataHandling(string flipMode)
    {
        // This test creates a video that is physically flipped, with flip metadata set in the display matrix (via -display_hflip / -display_vflip) so that it
        // displays correctly when played. It then re-encodes it through the library to verify that the flip gets baked into the video frames correctly (which
        // also exercises the flip detection that disables hardware acceleration, since ffmpeg only auto-applies flips when decoding in software), comparing an
        // extracted frame against one from the input (as a player would display them).
        // Note: flips cannot be detected from the rotation that ffprobe reports (hflip reports -180 and vflip reports 0), only from the display matrix itself.

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestFlipMetadataResults");
        resultsDir.Create();

        var tempFlippedInputFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"temp_input_flipped_{flipMode}.mp4"));
        var flippedInputFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"input_flipped_{flipMode}.mp4"));
        var outputFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"output_{flipMode}.mp4"));
        var inputFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_input_{flipMode}.png"));
        var outputFrameFile = resultsDir.CombineFile(string.Create(CultureInfo.InvariantCulture, $"frame_output_{flipMode}.png"));
        tempFlippedInputFile.Delete();
        flippedInputFile.Delete();
        outputFile.Delete();
        inputFrameFile.Delete();
        outputFrameFile.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a video that is physically flipped, with flip metadata set in the display matrix so that it displays correctly when played (flips are
        // self-inverse).
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", flipMode,
                "-c:v", "libx264",
                "-c:a", "copy",
                "-y", tempFlippedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                $"-display_{flipMode}",
                "-i", tempFlippedInputFile.PathExport,
                "-c", "copy",
                "-y", flippedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        tempFlippedInputFile.Delete();

        // Process with forced re-encoding:
        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream = flippedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = (await repo.GetAsync(fileId)).Path;
        videoPath.Exists.ShouldBeTrue();
        File.Copy(videoPath.PathExport, outputFile.PathExport);

        // Validate the dimensions and flip metadata of the input & output:
        var (probeOutput0, _, probeReturnCode0) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", flippedInputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode0.ShouldBe(0);

        var (probeOutput1, _, probeReturnCode1) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", outputFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        probeReturnCode1.ShouldBe(0);

        probeOutput0.Contains("\"width\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"height\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput0.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeTrue();

        probeOutput1.Contains("\"width\": 1920", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"height\": 1080", StringComparison.Ordinal).ShouldBeTrue();
        probeOutput1.Contains("\"Display Matrix\"", StringComparison.Ordinal).ShouldBeFalse();

        // Extract comparison frames & ensure the output displays the same as the input:
        await ExtractVideoFrame(flippedInputFile, inputFrameFile, 0.5);
        await ExtractVideoFrame(outputFile, outputFrameFile, 0.5);
        await CompareFrameToReferenceSSIM(
            inputFrameFile, outputFrameFile, string.Create(CultureInfo.InvariantCulture, $"frame_output_{flipMode}.png"));
    }
}
