using PrefixClassName.MsTest;
using Shouldly;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// This is the main Tests file for FulcrumFS.Videos - most tests are in other partial class files named by category, and the helpers are split out also.
// This file contains the type declaration and miscellaneous tests that don't really fit into other categories easily.

[PrefixTestClass]
public sealed partial class Tests
{
    [TestMethod]
    public async Task TestCreateAndDelete()
    {
        // Tests the complete file repository lifecycle: create a video file with a scaled variant, then delete both.
        // Verifies files exist after commit, are removed after deletion, and parent directories are cleaned up.

        using var repoCtx = GetRepo(out var repo);

        await using var stream = _videoFilesDir.CombineFile("video1.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId;

        await using (var txn = await repo.BeginTransactionAsync())
        {
            var added = await txn.AddAsync(stream, leaveOpen: false, new VideoProcessor(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            }).ToPipeline(), TestContext.CancellationToken);
            fileId = added.FileId;

            await repo.AddVariantAsync(added.FileId, "scaled_down", new VideoProcessor(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResizeOptions = new(VideoResizeMode.FitDown, 64, 36),
            }).ToPipeline(), TestContext.CancellationToken);

            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var scaledDownPath = await repo.GetVariantAsync(fileId, "scaled_down");
        scaledDownPath.Exists.ShouldBeTrue();

        await using (var txn = await repo.BeginTransactionAsync())
        {
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        videoPath.Exists.ShouldBeFalse();
        scaledDownPath.Exists.ShouldBeFalse();

        videoPath.ParentDirectory.Exists.ShouldBeFalse();
        videoPath.ParentDirectory.ParentDirectory!.Exists.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TestTryPreserveUnrecognizedStreamsFalse()
    {
        // Tests that unrecognized streams are stripped when TryPreserveUnrecognizedStreams is false.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: no extra streams, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video20.mp4: has subtitle stream also, stripped to 2 streams.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video20.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video134.mkv: has attachment, stripped to 2 streams.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video134.mkv",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

         // video135.ts: has data stream, stripped to 2 streams.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video135.ts",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping: [])); // Can't easily compare streams as going from .ts to .mp4 makes it appear re-encoded.
    }

    [TestMethod]
    public async Task TestSingleAudioOrVideoStreamFiles()
    {
        // Tests that files with only video (video160.mp4) or only audio (video161.mp4) streams can be processed successfully.

        // video160.mp4: video-only file.
        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video161.mp4: audio-only file.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video161.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video8.3gp")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video8.3gp")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video8.3gp")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video8.3gp")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video8.3gp")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video8.3gp")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video16.mkv")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video16.mkv")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video16.mkv")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video16.mkv")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video16.mkv")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video16.mkv")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video20.mp4")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video20.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video20.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video20.mp4")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video20.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video20.mp4")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video134.mkv")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video134.mkv")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video134.mkv")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video134.mkv")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video134.mkv")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video134.mkv")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video171.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video171.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video172.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video172.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, BigBuckBunnyFullVideoFileName)]
    public async Task TestProgressCallbackMonotonicallyIncreasing(
        bool forceValidation,
        bool removeAudioStreams,
        StreamReencodeMode videoReencode,
        StreamReencodeMode audioReencode,
        string fileName)
    {
        // Tests progress callback is monotonically increasing across different processing configurations.
        // Tests important combos of: ForceValidateAllStreams, VideoReencodeMode, AudioReencodeMode, incompatible streams.

        using var repoCtx = GetRepo(out var repo);

        double lastProgress = double.MinValue;
        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = forceValidation,
            RemoveAudioStreams = removeAudioStreams,
            VideoReencodeMode = videoReencode,
            AudioReencodeMode = audioReencode,
            ResultFormats = [MediaContainerFormat.MP4],
            ProgressCallback = async (_, progress) =>
            {
                progress.ShouldBeGreaterThanOrEqualTo(0.0, "Progress should not be negative");
                progress.ShouldBeLessThanOrEqualTo(1.0, "Progress should not exceed 1.0");
                progress.ShouldBeGreaterThan(lastProgress, $"Progress did not increase from {lastProgress} to {progress}");
                lastProgress = progress;
            },
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task TestComplexFile()
    {
        // Test processing a complex file with many streams with a complex processing scenario.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = true,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.Always,
                ResultFormats = [MediaContainerFormat.MP4],
                MaxSampleRate = AudioSampleRate.Hz44100,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
                AudioQuality = AudioQuality.Lowest,
                VideoQuality = VideoQuality.High,
                VideoCompressionLevel = VideoCompressionLevel.Low,
                ForceProgressiveFrames = true,
                ForceSquarePixels = true,
                FpsOptions = new VideoFpsOptions(VideoFpsMode.LimitByIntegerDivision, 13),
                MaxChannels = AudioChannels.Mono,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
                RemapHDRToSDR = true,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 41, 37),
                TryPreserveUnrecognizedStreams = true,
                ProgressCallback = async (_, _) => { },
                ForceProgressiveDownload = true,
                VideoSourceValidation = VideoStreamValidationOptions.None with
                {
                    MinWidth = 4,
                    MaxWidth = 5341,
                    MinHeight = 7,
                    MaxHeight = 8001,
                    MinPixels = 52,
                    MaxPixels = 10_000_234,
                    MinStreams = 2,
                    MaxStreams = 8,
                    MinLength = TimeSpan.FromMilliseconds(63),
                    MaxLength = TimeSpan.FromDays(4),
                },
                AudioSourceValidation = AudioStreamValidationOptions.None with
                {
                    MinStreams = 3,
                    MaxStreams = 4,
                    MinLength = TimeSpan.FromMilliseconds(47),
                    MaxLength = TimeSpan.FromDays(13),
                },
                ResultAudioCodecs = [AudioCodec.AAC],
                ResultVideoCodecs = [VideoCodec.HEVCAnyTag, VideoCodec.H264],
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video133.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 15, StreamMapping: []));
    }
}
