using Shouldly;
using Singulink.IO;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// This file contains the tests directly related to container processing handling.

partial class Tests
{
    [TestMethod]
    public async Task TestUnwantedOutputFormatRemuxes()
    {
        // Tests that files in formats not matching ResultFormats are remuxed (not re-encoded) to a supported format.
        // Uses video2.mkv with MP4-only result format to verify streams are preserved but container changes.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultFormats = [MediaContainerFormat.MP4],
            },
            "video2.mkv",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestWantedOutputFormatDoesntRemuxUnnecessarily()
    {
        // Tests that files already in an allowed ResultFormats container are not unnecessarily remuxed.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultFormats = [MediaContainerFormat.MP4, MediaContainerFormat.Mkv],
            },
            "video2.mkv",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    [DoNotParallelize] // See the comment in the function body halfway about why - we don't want any potential deadlocks on Linux due to using '-v trace'.
    public async Task TestForceProgressiveDownload()
    {
        // Tests that ForceProgressiveDownload correctly moves the moov atom before mdat for streaming compatibility.
        // Verifies the original file has moov after mdat, and the processed file has moov before mdat - this provides improved streaming performance, but does
        // not make it seekable.

        // Put all of our 'using' resources to ensure we close everything asap (on Linux, using '-v trace' requires them to be disposed first for some reason):
        IAbsoluteFilePath videoPath;
        {
            using var repoCtx = GetRepo(out var repo);

            var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ForceProgressiveDownload = true,
            }).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile("video1.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            FileId fileId;

            await using var txn = await repo.BeginTransactionAsync();
            fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);

            videoPath = await repo.GetAsync(fileId);
            videoPath.Exists.ShouldBeTrue();
        }

        // Check that the original file was not progressive download, and the new one is (note: the check is very basic & could be fooled, but is sufficient
        // for test purposes):
        var (_, errorOriginal, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", _videoFilesDir.CombineFile("video1.mp4").PathExport, "-v", "trace"],
            TestContext.CancellationToken);
        var (_, errorModified, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-v", "trace"],
            TestContext.CancellationToken);
        returnCodeOriginal.ShouldBe(0);
        returnCodeModified.ShouldBe(0);
        int idxMoovOriginal = errorOriginal.IndexOf("moov", StringComparison.Ordinal);
        int idxMoovModified = errorModified.IndexOf("moov", StringComparison.Ordinal);
        int idxMdatOriginal = errorOriginal.IndexOf("mdat", StringComparison.Ordinal);
        int idxMdatModified = errorModified.IndexOf("mdat", StringComparison.Ordinal);
        idxMoovOriginal.ShouldBeGreaterThanOrEqualTo(0);
        idxMoovModified.ShouldBeGreaterThanOrEqualTo(0);
        idxMdatOriginal.ShouldBeGreaterThanOrEqualTo(0);
        idxMdatModified.ShouldBeGreaterThanOrEqualTo(0);
        idxMoovOriginal.ShouldBeGreaterThan(idxMdatOriginal);
        idxMoovModified.ShouldBeLessThan(idxMdatModified);
    }

    [TestMethod]
    public async Task TestMP4FileWithMkvExtensionWithCopying()
    {
        // Tests handling of an MP4 file masquerading with .mkv extension when file is unchanged (direct copy).
        // Verifies the output gets the correct .mp4 extension based on actual container format, but doesn't cause remuxing.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null,
            addAsyncExtensionOverride: ".mkv",
            afterFinishedAction: async (newPath) => FilePath.ParseAbsolute(newPath).Extension.ShouldBe(".mp4"));
    }

    [TestMethod]
    public async Task TestMP4FileWithMkvExtensionWithRemuxing()
    {
        // Tests handling of an MP4 file masquerading with .mkv extension when remuxing (via ForceProgressiveDownload).
        // Verifies the output gets the correct .mp4 extension based on actual container format.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ForceProgressiveDownload = true,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true),
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true),
            ]),
            addAsyncExtensionOverride: ".mkv",
            afterFinishedAction: async (newPath) => FilePath.ParseAbsolute(newPath).Extension.ShouldBe(".mp4"));
    }

    [TestMethod]
    public async Task TestMP4FileWithMkvExtensionWithReencoding()
    {
        // Tests handling of an MP4 file masquerading with .mkv extension when re-encoding.
        // Verifies the output gets the correct .mp4 extension based on actual container format.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false),
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false),
            ]),
            addAsyncExtensionOverride: ".mkv",
            afterFinishedAction: async (newPath) => FilePath.ParseAbsolute(newPath).Extension.ShouldBe(".mp4"));
    }
}
