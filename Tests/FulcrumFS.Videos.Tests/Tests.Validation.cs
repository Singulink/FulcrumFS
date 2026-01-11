using Shouldly;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// NOTE: these are only the validation tests for VideoProcessor.
// These tests are the tests that perform source validation, and the tests that validate that common configs work across all of the valid test videos
// (i.e., the bulk tests).

partial class Tests
{
    [TestMethod]
    public async Task TestSourceValidation()
    {
        // Tests source video/audio stream validation options including duration, stream count, dimensions, and pixel count limits.

        using var repoCtx = GetRepo(out var repo);

        async Task RunTest(string fileName, VideoProcessingOptions options, string? expectedMessage)
        {
            try
            {
                await CheckProcessing(repo, options, fileName, expectedMessage, expectedChanges: null);
            }
            catch (Exception ex)
            {
                throw new ShouldAssertException($"Error during RunTest with file '{fileName}' and options '{options}'.", ex);
            }
        }

        // Validate the predefined configs wrt the None config, since we only test off of that:
        VideoStreamValidationOptions.StandardVideo.ShouldBe(VideoStreamValidationOptions.None with { MinStreams = 1, MaxStreams = 1 });
        AudioStreamValidationOptions.StandardAudio.ShouldBe(AudioStreamValidationOptions.None with { MinStreams = 1, MaxStreams = 1 });
        AudioStreamValidationOptions.OptionalStandardAudio.ShouldBe(AudioStreamValidationOptions.None with { MaxStreams = 1 });

        // Use the following as our base options
        var baseOptions = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = false,
        };

        // video1.mp4 has the following properties that we'll test validation with: width - 128, height - 72, duration - 1s, 1 video stream, 1 audio stream
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2) } },
            "Video stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Video stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2) } },
            "Audio stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Audio stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinWidth = 129 } },
            "Video stream 0 width is less than the minimum required width.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinWidth = 128 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxWidth = 127 } },
            "Video stream 0 width exceeds the maximum allowed width.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxWidth = 128 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinHeight = 73 } },
            "Video stream 0 height is less than the minimum required height.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinHeight = 72 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxHeight = 71 } },
            "Video stream 0 height exceeds the maximum allowed height.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxHeight = 72 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinPixels = 9217 } },
            "Video stream 0 is less than the minimum required pixel count.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinPixels = 9216 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxPixels = 9215 } },
            "Video stream 0 exceeds the maximum allowed pixel count.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxPixels = 9216 } },
            null);

        // video133.mp4 has the following properties that we'll test validation with: 8 video streams, 4 audio streams, 3 subtitle streams, 3 thumbnail streams
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 9 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 8 } },
            null);
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 7 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 8 } },
            null);
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 5 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 4 } },
            null);
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 3 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 4 } },
            null);

        // video134.mkv has the following properties that we'll test validation with: 1 video stream, 1 audio stream, 1 attachment stream
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 1 } },
            null);

        // video135.ts has the following properties that we'll test validation with: 1 video stream, 1 audio stream, 1 data stream
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheck))]
    public async Task TestPreserveOptions(string fileName)
    {
        // Note: we are also testing that oversized videos don't re-encode to H.264 unnecessarily here (e.g., video143.mp4).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve,
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheck))]
    public async Task TestH264StandardizedOptions(string fileName)
    {
        // This tests that it can succedd processing every valid file, that the stream count matches afterwards (except for those with streams incompatible
        // with MP4), and that all streams have been changed (re-encoded):

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.StandardizedH264AACMP4 with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            TryPreserveUnrecognizedStreams = true, // Allows us to test our subtitle handling code also
        };

        if (!VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) && !VideoFilesWithThumbnails.Contains(fileName))
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        }
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheckToHEVC))]
    public async Task TestHEVCStandardizedOptions(string fileName)
    {
        // Same as TestStandardizedOptions, but using HEVC as the target video codec to test our hevc handling:

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.StandardizedHEVCAACMP4 with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
#if CI
            VideoCompressionLevel = VideoCompressionLevel.Lowest, // Use lowest compression level to speed up tests
#endif
            TryPreserveUnrecognizedStreams = true, // Allows us to test our subtitle handling code also
#if DEBUG
            ForceLibFDKAACUsage = false, // Force native aac usage in debug mode to test both code paths
#endif
        };

        if (!VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) && !VideoFilesWithThumbnails.Contains(fileName))
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        }
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheck))]
    public async Task TestH264ReencodeOptions(string fileName)
    {
        // This tests that it can succeed processing every valid file (with original settings, rather than limited ones), that the stream count matches
        // afterwards (except for those with streams incompatible with MP4), and that all streams have been changed (re-encoded):

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always, // Also test libfdk_aac / fallback usage here
            ResultVideoCodecs = [VideoCodec.H264],
            ResultAudioCodecs = [AudioCodec.AAC],
            TryPreserveUnrecognizedStreams = true, // Allows us to use the same list as for TestStandardizedOptions
#if CI
            VideoCompressionLevel = VideoCompressionLevel.Lowest, // Use lowest compression level to speed up tests
#endif
        };

        bool removeStreams = VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) || VideoFilesWithThumbnails.Contains(fileName);
        if (VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencoding.Contains(fileName))
        {
            options = options with { RemoveAudioStreams = true };
            removeStreams = true;
        }

        if (!removeStreams)
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        }
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheckToHEVC))]
    public async Task TestHEVCReencodeOptions(string fileName)
    {
        // Same as TestH264ReencodeOptions, but using HEVC as the target video codec to test our hevc handling:

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always, // Also test aac usage here (in debug mode)
#if DEBUG
            ForceLibFDKAACUsage = false, // Force native aac usage in debug mode to test both code paths
#endif
            ResultVideoCodecs = [VideoCodec.H264],
            ResultAudioCodecs = [AudioCodec.AAC],
            TryPreserveUnrecognizedStreams = true, // Allows us to use the same list as for TestStandardizedOptions
        };

        bool removeStreams = VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) || VideoFilesWithThumbnails.Contains(fileName);
        if (VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencoding.Contains(fileName) ||
            VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencodingWithNativeAAC.Contains(fileName))
        {
            options = options with { RemoveAudioStreams = true };
            removeStreams = true;
        }

        if (!removeStreams)
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        }
    }

    [TestMethod]
    [DynamicData(nameof(VideoFilesWithSubtitles))]
    public async Task TestStandardizedOptionsNotPreservingUnrecognizedStreams(string fileName)
    {
        // Tests that subtitle streams are correctly stripped when TryPreserveUnrecognizedStreams is disabled (default for StandardizedH264AACMP4).

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.StandardizedH264AACMP4 with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
        }).ToPipeline();

        await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ThrowWhenSourceUnchanged()
    {
        // Tests that FileProcessingException is thrown when throwWhenSourceUnchanged is enabled and the file would be unchanged.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video1.mp4",
            exceptionMessage: "File processing did not result in any changes to the source file.",
            expectedChanges: null,
            throwWhenSourceUnchanged: true);
    }

    [TestMethod]
    public async Task TestUnsupportedExtension()
    {
        // Tests that files with extensions not matching the allowed SourceFormats are rejected early with a clear error.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceFormats = [MediaContainerFormat.MP4],
            },
            "video2.mkv",
            exceptionMessage: "Extension '.mkv' is not allowed. Allowed extensions: .mp4, .mov, .m4a, .3gp, .3g2, .mj2",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestUnsupportedFormat()
    {
        // Tests that files with container formats not matching SourceFormats are rejected after format detection.
        // Uses .mp4 extension override to bypass extension check and trigger actual format validation.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceFormats = [MediaContainerFormat.MP4],
            },
            "video2.mkv",
            exceptionMessage: "The source video format 'matroska,webm' is not supported by this processor.",
            expectedChanges: null,
            addAsyncExtensionOverride: ".mp4");
    }

    [TestMethod]
    public async Task TestSupportedFormat()
    {
        // Tests that files matching the SourceFormats constraint are accepted and processed normally.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceFormats = [MediaContainerFormat.MP4],
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestOtherInputValidation()
    {
        // Tests validation of videos with misleading metadata. Uses video158.mkv which has advertised duration of 0.5s
        // but actual measured duration of ~1s, to verify both advertised and measured duration checks work correctly.

        using var repoCtx = GetRepo(out var repo);

        async Task RunTest(string fileName, VideoProcessingOptions options, string? expectedMessage)
        {
            try
            {
                await CheckProcessing(repo, options, fileName, expectedMessage, expectedChanges: null);
            }
            catch (Exception ex)
            {
                throw new ShouldAssertException($"Error during RunTest with file '{fileName}' and options '{options}'.", ex);
            }
        }

        var configNoForceCheck = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = false,
        };

        // video158.mkv has an advertised duration of 0.5s, but actual duration is just over 1s:
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Measured video stream duration exceeds the maximum allowed length.");
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1.5) } },
            null);
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Measured audio stream duration exceeds the maximum allowed length.");
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1.5) } },
            null);
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.25) } },
            "Video stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            null);
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.25) } },
            "Audio stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            null);

        // video196.mkv has an advertised duration of 1.5s, but actual duration is just over 1s:
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            "Measured video stream duration is less than the minimum required length.");
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.0) } },
            null);
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            "Measured audio stream duration is less than the minimum required length.");
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.0) } },
            null);
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2.0) } },
            "Video stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            null);
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2.0) } },
            "Audio stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            null);
    }

    [TestMethod]
    public async Task TestUnsupportedVideoCodec()
    {
        // Tests that video files with codecs not in SourceVideoCodecs are rejected.
        // Uses video10.mp4 (HEVC) with H264-only source constraint.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.H264],
            },
            "video10.mp4",
            exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestSupportedVideoCodec()
    {
        // Tests that video files with codecs matching SourceVideoCodecs are accepted.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.H264],
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestUnsupportedAudioCodec()
    {
        // Tests that files with audio codecs not in SourceAudioCodecs are rejected.
        // Uses video4.webm (Opus audio) with AAC-only source constraint.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceAudioCodecs = [AudioCodec.AAC],
            },
            "video4.webm",
            exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestSupportedAudioCodec()
    {
        // Tests that files with audio codecs matching SourceAudioCodecs are accepted.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceAudioCodecs = [AudioCodec.AAC],
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestNoVideoOrAudioStreamsThrows()
    {
        // Tests that processing fails with clear error message when input contains no audio or video streams.

        using var repoCtx = GetRepo(out var repo);

        // video159.ts: no audio or video streams, but one data stream.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video159.ts",
            exceptionMessage: "The source video contains no audio or video streams.",
            expectedChanges: null);

        // video168.mp4: no streams at all.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video168.mp4",
            exceptionMessage: "The source video contains no audio or video streams.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRemoveAudioThrows()
    {
        // Tests that requesting audio removal from an audio-only file (video161.mp4) results in an error,
        // but succeeds for video-only files (video160.mp4) since there's nothing to remove.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemoveAudioStreams = true,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemoveAudioStreams = true,
            },
            "video161.mp4",
            exceptionMessage: "The source video contains no video streams.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRandomBytesAsVideoThrows()
    {
        // This test generates random bytes with a specific seed, which forms an invalid an mp4,
        // and verifies that a FileProcessingException is thrown because the file is invalid.

        using var repoCtx = GetRepo(out var repo);

        var random = new Random(42); // Use a fixed seed for reproducibility
        byte[] randomBytes = new byte[1024];
        random.NextBytes(randomBytes);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve).ToPipeline();

        await using var stream = new MemoryStream(randomBytes);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
        {
            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, ".mp4", true, pipeline, TestContext.CancellationToken);
        });

        // The exception message should indicate the file is invalid/unrecognized
        ex.Message.ShouldNotBeNullOrEmpty();
    }

    [TestMethod]
    [DataRow("video1.mp4", "h264")]
    [DataRow("video4.webm", "vp9")]
    [DataRow("video6.ts", "mpeg2video")]
    [DataRow("video7.mpeg", "mpeg1video")]
    [DataRow("video8.3gp", "h263")]
    [DataRow("video9.avi", "h263")]
    [DataRow("video11.mkv", "vp8")]
    [DataRow("video12.mp4", "av1")]
    [DataRow("video15.mp4", "vvc")]
    [DataRow("video164.mp4", "hevc")]
    public async Task TestSourceVideoCodecDetection(string fileName, string expectedVideoCodecName)
    {
        // Tests that SourceVideoCodecs correctly identifies video codecs by iterating through all known codecs.
        // Each test file should only match its expected codec and reject all others.

        using var repoCtx = GetRepo(out var repo);

        foreach (var codec in VideoCodec.AllSourceCodecs)
        {
            // Skip HEVCAnyTag as it is a special case.
            if (codec == VideoCodec.HEVCAnyTag)
                continue;

            bool isCorrect = codec.Name == expectedVideoCodecName;

            if (isCorrect)
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceVideoCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: null);
            }
            else
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceVideoCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
                    expectedChanges: null);
            }
        }
    }

    [TestMethod]
    [DataRow("video1.mp4", "aac", "LC")]
    [DataRow("video4.webm", "opus", null)]
    [DataRow("video6.ts", "mp3", null)]
    [DataRow("video7.mpeg", "mp2", null)]
    [DataRow("video13.mp4", "aac", "HE-AAC")]
    [DataRow("video14.mp4", "vorbis", null)]
    public async Task TestSourceAudioCodecDetection(string fileName, string expectedAudioCodecName, string? expectedAudioCodecProfile)
    {
        // Tests that SourceAudioCodecs correctly identifies audio codecs by iterating through all known codecs.
        // Each test file should only match its expected codec and reject all others.

        using var repoCtx = GetRepo(out var repo);

        foreach (var codec in AudioCodec.AllSourceCodecs)
        {
            // Match using the same logic as VideoProcessor.MatchAudioCodecByName:
            bool isCorrect = codec.Name == expectedAudioCodecName && (codec.Profile == null || codec.Profile == expectedAudioCodecProfile);

            if (isCorrect)
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceAudioCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: null);
            }
            else
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceAudioCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
                    expectedChanges: null);
            }
        }
    }

    [TestMethod]
    [DataRow("video1.mp4", "mov,mp4,m4a,3gp,3g2,mj2")]
    [DataRow("video2.mkv", "matroska,webm")]
    [DataRow("video3.mov", "mov,mp4,m4a,3gp,3g2,mj2")]
    [DataRow("video4.webm", "matroska,webm")]
    [DataRow("video5.avi", "avi")]
    [DataRow("video6.ts", "mpegts")]
    [DataRow("video7.mpeg", "mpeg")]
    [DataRow("video8.3gp", "mov,mp4,m4a,3gp,3g2,mj2")]
    public async Task TestSourceFormatDetection(string fileName, string expectedFormatName)
    {
        // Tests that SourceFormats correctly identifies container formats by iterating through all known formats.
        // Each test file should only match its expected format and reject all others.

        using var repoCtx = GetRepo(out var repo);

        foreach (var format in MediaContainerFormat.AllSourceFormats)
        {
            bool isCorrect = format.Name == expectedFormatName;

            if (isCorrect)
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceFormats = [format],
                    },
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: null);
            }
            else
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceFormats = [format],
                    },
                    fileName,
                    exceptionMessage: $"The source video format '{expectedFormatName}' is not supported by this processor.",
                    expectedChanges: null,
                    addAsyncExtensionOverride: format.CommonExtensions.First());
            }
        }
    }

    [TestMethod]
    [DataRow("video10.mp4", false)]
    [DataRow("video164.mp4", true)]
    public async Task TestSourceVideoCodecHEVCTagDetection(string fileName, bool isHvc1)
    {
        // Tests HEVC tag detection: VideoCodec.HEVC matches only hvc1-tagged files, while HEVCAnyTag matches any HEVC file.
        // video10.mp4 is hev1-tagged, video164.mp4 is hvc1-tagged.

        using var repoCtx = GetRepo(out var repo);

        // HEVC has TagName="hvc1", so it only matches hvc1 tagged files.
        // HEVCAnyTag has TagName=null, so it matches any hevc file regardless of tag.

        // Test HEVC (hvc1 only):
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.HEVC],
            },
            fileName,
            exceptionMessage: isHvc1 ? null : "One or more streams use a codec that is not supported by this processor.",
            expectedChanges: null);

        // Test HEVCAnyTag (matches any tag):
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.HEVCAnyTag],
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestForceValidateAllStreams()
    {
        // Tests that ForceValidateAllStreams causes invalid streams to be detected even when they would be missed otherwise.

        using var repoCtx = GetRepo(out var repo);

        // video175.mp4 has a validation error that is only found with ForceValidateAllStreams set to true (or if re-encoding).
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = false,
            },
            "video175.mp4",
            exceptionMessage: null,
            expectedChanges: null);
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = true,
            },
            "video175.mp4",
            exceptionMessage: "An error occurred while validating the source video streams.",
            expectedChanges: null);
    }
}
