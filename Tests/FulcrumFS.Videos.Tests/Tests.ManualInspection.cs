using System.Globalization;
using Shouldly;
using Singulink.IO;

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
    // Note also, we offset by ~1 frame for the TestVideoCompressionLevel tests, as they seem to be slightly out of sync otherwise for some reason.
    private async Task GenerateVideoDiffFile(
        IAbsoluteFilePath processedFile, IAbsoluteFilePath originalFile, int fps = 10, double duration = 10.0, double timeOffset = 0.0)
    {
        // Note: this method is disabled on CI as it can be quite slow and is for visual inspection only anyway.
#if !CI
        var diffFile = processedFile.ParentDirectory.CombineFile($"{processedFile.NameWithoutExtension}-diff.mp4");
        diffFile.Delete();

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-ss", timeOffset > 0 ? timeOffset.ToString(CultureInfo.InvariantCulture) : "0",
                "-i", originalFile.PathExport,
                "-ss", timeOffset < 0 ? (-timeOffset).ToString(CultureInfo.InvariantCulture) : "0",
                "-i", processedFile.PathExport,
                "-filter_complex", string.Create(CultureInfo.InvariantCulture,
                    $"[0:v]" +
                    $"fps=fps={fps}," +
                    $"format=pix_fmts=yuv444p:color_ranges=pc" +
                    $"[o0];" +
                    $"[1:v]" +
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

        var resultFile = _appDir.CombineDirectory("TestVideoCompressionLevelH264Results").CombineFile($"{level}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

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

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile, fps: 30, duration: 1.0, timeOffset: 0.017);
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

        var resultFile = _appDir.CombineDirectory("TestVideoCompressionLevelHEVCResults").CombineFile($"{level}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

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

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile, fps: 30, duration: 1.0, timeOffset: 0.017);
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
        var resultFile = _appDir.CombineDirectory($"TestVideoQuality{resultFolderName}Results").CombineFile($"{quality}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

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

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile);

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

        var videoPath = await repo.GetAsync(fileId);
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

        var videoPath = await repo.GetAsync(fileId);
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

        var resultFile = _appDir.CombineDirectory("TestVideoResizeH264Results").CombineFile(
            string.Create(CultureInfo.InvariantCulture, $"{width}x{height}.mp4"));
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

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

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);
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

        var resultFile = _appDir.CombineDirectory("TestVideoResizeHEVCResults").CombineFile(
            string.Create(CultureInfo.InvariantCulture, $"{width}x{height}.mp4"));
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

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

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);
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

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        // If extension was .mkv, also make a copy remuxed back to mkv to validate in case of weird mp4 dvd_subtitle playback issues:
        if (makeMkvCopy)
        {
            var resultFileMkv = _appDir.CombineDirectory("TestSubtitleReencodeResults").CombineFile(
                string.Create(CultureInfo.InvariantCulture, $"{fileName}.mkv"));
            resultFileMkv.Delete();
            await RunFFtoolProcessWithErrorHandling(
                "ffmpeg",
                ["-i", videoPath.PathExport, "-c", "copy", "-y", resultFileMkv.PathExport],
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
        interlacedFile.Delete();
        deinterlacedFile.Delete();

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

        var videoPath = await repo.GetAsync(fileId);
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

        var videoPath = await repo.GetAsync(fileId!);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);
    }
#endif

    [TestMethod]
    public async Task TestRotationMetadataHandling()
    {
        // This test creates a video that is physically rotated 90 degrees clockwise, but has rotation metadata set to -90 (to display correctly). It then
        // processes it with both preserve (both re-encoding & remuxing) and strip metadata modes to verify that rotation handling works correctly in all
        // cases.

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestRotationMetadataResults");
        resultsDir.Create();

        var tempRotatedInputFile = resultsDir.CombineFile("temp_input_rotated.mp4");
        var rotatedInputFile = resultsDir.CombineFile("input_rotated_with_metadata.mp4");
        var outputPreserveMetadataRemuxed = resultsDir.CombineFile("output_preserve_metadata_remuxed.mp4");
        var outputPreserveMetadataReencoded = resultsDir.CombineFile("output_preserve_metadata_reencoded.mp4");
        var outputStripMetadata = resultsDir.CombineFile("output_strip_metadata.mp4");
        tempRotatedInputFile.Delete();
        rotatedInputFile.Delete();
        outputPreserveMetadataRemuxed.Delete();
        outputPreserveMetadataReencoded.Delete();
        outputStripMetadata.Delete();

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

        var videoPath1 = await repo.GetAsync(fileId1);
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

        var videoPath2 = await repo.GetAsync(fileId2);
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

        var videoPath3 = await repo.GetAsync(fileId3);
        videoPath3.Exists.ShouldBeTrue();
        File.Copy(videoPath3.PathExport, outputStripMetadata.PathExport);
    }

#if !CI
    [TestMethod]
    [DataRow("video114")]
    [DataRow("Y0__auYqGXY-5s")]
    [DataRow("Y0__auYqGXY-20s")]
    public async Task TestHDRToSDRMapping(string fileName)
    {
        // Note: this test is excluded from CI runs since it is intended for manual inspection only (HDR->SDR logic is also tested in TestStandardizedOptions).
        // Tests HDR to SDR color mapping. Outputs the result files for manual inspection.

        var resultFileH264 = _appDir.CombineDirectory("TestHDRToSDRMappingResults").CombineFile(fileName + "-H264.mp4");
        resultFileH264.ParentDirectory?.Create();
        resultFileH264.Delete();

        var resultFileHEVC = _appDir.CombineDirectory("TestHDRToSDRMappingResults").CombineFile(fileName + "-HEVC.mp4");
        resultFileHEVC.ParentDirectory?.Create();
        resultFileHEVC.Delete();

        using var repoCtx = GetRepo(out var repo);

        var origFile = _videoFilesDir.CombineFile(fileName + ".mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        var pipelineH264 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            RemapHDRToSDR = true,
            ResultVideoCodecs = [VideoCodec.H264],
        }).ToPipeline();

        await using var txnH264 = await repo.BeginTransactionAsync();
        var fileIdH264 = (await txnH264.AddAsync(stream, true, pipelineH264, TestContext.CancellationToken)).FileId;
        await txnH264.CommitAsync(TestContext.CancellationToken);

        var videoPathH264 = await repo.GetAsync(fileIdH264);
        videoPathH264.Exists.ShouldBeTrue();

        File.Copy(videoPathH264.PathExport, resultFileH264.PathExport);

        var pipelineHEVC = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            RemapHDRToSDR = true,
            ResultVideoCodecs = [VideoCodec.HEVC],
        }).ToPipeline();

        await using var txnHEVC = await repo.BeginTransactionAsync();
        var fileIdHEVC = (await txnHEVC.AddAsync(stream, true, pipelineHEVC, TestContext.CancellationToken)).FileId;
        await txnHEVC.CommitAsync(TestContext.CancellationToken);

        var videoPathHEVC = await repo.GetAsync(fileIdHEVC);
        videoPathHEVC.Exists.ShouldBeTrue();

        File.Copy(videoPathHEVC.PathExport, resultFileHEVC.PathExport);
    }
#endif

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

        var videoPath1 = await repo.GetAsync(fileId1);
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

        var videoPath2 = await repo.GetAsync(fileId2);
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

        var videoPath3 = await repo.GetAsync(fileId3);
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

        var videoPath4 = await repo.GetAsync(fileId4);
        videoPath4.Exists.ShouldBeTrue();
        File.Copy(videoPath4.PathExport, outputStripUnrecognizedStreams.PathExport);
        await ValidateMetadata(outputStripUnrecognizedStreams, shouldHaveStartTime: true);
    }
}
