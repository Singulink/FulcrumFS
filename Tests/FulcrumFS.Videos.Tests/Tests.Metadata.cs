using Shouldly;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// Tests relating to video file / stream metadata handling that don't fit into other categories (e.g., not TestSelectSmallestMetadataHandling, which is in
// Tests.SelectSmallest.cs). I.e, tests relating to metadata handling alone.

partial class Tests
{
    [TestMethod]
    public async Task TestTryStripThumbnails()
    {
        // Tests that TryStripThumbnails removes embedded video thumbnail streams from the output.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: no thumbnail stream, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video53.mp4: has a png thumbnail stream.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video53.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video54.mp4: has a mjpeg thumbnail stream.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video54.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestStreamLanguagePreservedWithMetadataStripping()
    {
        // Tests that stream language metadata is preserved even when general metadata stripping is enabled.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();

        // video20.mp4: unknown language.
        await using var stream20 = _videoFilesDir.CombineFile("video20.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId20;

        await using var txn20 = await repo.BeginTransactionAsync();
        fileId20 = (await txn20.AddAsync(stream20, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn20.CommitAsync(TestContext.CancellationToken);

        var videoPath20 = await repo.GetAsync(fileId20!);
        videoPath20.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (this subtitle has an unknown language):
        var (outputModified20, _, returnCodeModified20) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath20.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified20.ShouldBe(0);
        outputModified20.Contains("\"language\": \"und\"", StringComparison.Ordinal).ShouldBeTrue();

        // video21.mp4: eng language.
        await using var stream21 = _videoFilesDir.CombineFile("video21.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId21;

        await using var txn21 = await repo.BeginTransactionAsync();
        fileId21 = (await txn21.AddAsync(stream21, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn21.CommitAsync(TestContext.CancellationToken);

        var videoPath21 = await repo.GetAsync(fileId21!);
        videoPath21.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (the subtitle has "eng" language):
        var (outputModified21, _, returnCodeModified21) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath21.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified21.ShouldBe(0);
        outputModified21.Contains("\"language\": \"eng\"", StringComparison.Ordinal).ShouldBeTrue();

        // video163.mp4: eng video, fre audio.
        await using var stream163 = _videoFilesDir.CombineFile("video163.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId163;

        await using var txn163 = await repo.BeginTransactionAsync();
        fileId163 = (await txn163.AddAsync(stream163, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn163.CommitAsync(TestContext.CancellationToken);

        var videoPath163 = await repo.GetAsync(fileId163!);
        videoPath163.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (the video stream has "eng" language, and the audio stream has "fre" language):
        var (outputModified163, _, returnCodeModified163) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath163.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified163.ShouldBe(0);
        int engIdx = outputModified163.IndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
        int freIdx = outputModified163.IndexOf("\"language\": \"fre\"", StringComparison.Ordinal);
        engIdx.ShouldBeGreaterThan(-1);
        freIdx.ShouldBeGreaterThan(-1);
        engIdx.ShouldBeLessThan(freIdx);
    }

    [TestMethod]
    public async Task TestRequiredMetadataStrippingMode()
    {
        // Tests that VideoMetadataStrippingMode.Required removes thumbnails, standard metadata, and custom metadata.
        // Uses video53.mp4 (has a thumbnail) and video162.mp4 (has artist & custom_metadata tags).

        using var repoCtx = GetRepo(out var repo);

        // Test if it successfully removes thumbnails from a video with thumbnails:
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.Required,
            },
            "video53.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping: []));

        // Test if can remove standard metadata & custom metadata from the file:

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video162.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var (outputOriginal, _, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        var (outputModified, _, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeOriginal.ShouldBe(0);
        returnCodeModified.ShouldBe(0);

        outputOriginal.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputOriginal.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();

        outputModified.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeFalse();
        outputModified.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    [TestMethod]
    public async Task TestMetadataPreservation()
    {
        // Tests that VideoMetadataStrippingMode.None preserves standard and custom metadata even when re-encoding.
        // Uses video162.mp4 with artist & custom_metadata tags.

        using var repoCtx = GetRepo(out var repo);

        // Test if can preserve standard metadata & custom metadata in the file:

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video162.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var (outputOriginal, _, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        var (outputModified, _, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeOriginal.ShouldBe(0);
        returnCodeModified.ShouldBe(0);

        outputOriginal.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputOriginal.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();

        outputModified.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputModified.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();
    }

    [TestMethod]
    public async Task TestStreamMetadataPreservation()
    {
        // Tests that stream-level metadata (language tags) is preserved when re-encoding with MetadataStrippingMode.None.
        // Uses video163.mp4 which has eng video stream and fre audio stream.

        using var repoCtx = GetRepo(out var repo);

        // Test if can preserve stream metadata in the file:
        // Note: the logic used to achieve this is different to TestStreamLanguagePreservedWithMetadataStripping, since the logic used with metadata stripping
        // on vs off is substantially different.

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream = _videoFilesDir.CombineFile("video163.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (the video stream has "eng" language, and the audio stream has "fre" language):
        var (outputModified, _, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified.ShouldBe(0);
        int engIdx = outputModified.IndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
        int freIdx = outputModified.IndexOf("\"language\": \"fre\"", StringComparison.Ordinal);
        engIdx.ShouldBeGreaterThan(-1);
        freIdx.ShouldBeGreaterThan(-1);
        engIdx.ShouldBeLessThan(freIdx);
    }

    [TestMethod]
    public async Task TestMetadataStrippingPreferredMode()
    {
        // Tests that VideoMetadataStrippingMode.Preferred preserves metadata when no re-encoding is needed, but strips it when re-encoding is forced.
        // Uses video162.mp4 with artist & custom_metadata tags.

        using var repoCtx = GetRepo(out var repo);

        // Test that it doesn't cause re-encoding:
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.Preferred,
            },
            "video162.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test if it strips the metadata when re-encoding is forced:

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Preferred,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video162.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var (outputOriginal, _, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        var (outputModified, _, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeOriginal.ShouldBe(0);
        returnCodeModified.ShouldBe(0);

        outputOriginal.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputOriginal.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();

        outputModified.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeFalse();
        outputModified.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    [TestMethod]
    public async Task TestMetadataStrippingRequiredModeForcesRemuxing()
    {
        // Tests that VideoMetadataStrippingMode.Required forces remuxing even when no re-encoding is needed.

        using var repoCtx = GetRepo(out var repo);

        // Test that it remuxes the file even when no other changes are needed:
        // Note: it won't cause re-encoding for our purposes here, since we compare ignoring metadata.

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.Required,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }
}
