using Shouldly;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// These are the tests that are specifically related to our SelectSmallest functionality in VideoProcessor.

partial class Tests
{
    [TestMethod]
    public async Task TestSelectSmallestReencodesOversizedStreams()
    {
        // Tests that SelectSmallest re-encodes oversized streams (where re-encoding produces smaller output)
        // and preserves undersized streams (where original is already smaller than re-encoded would be).

        using var repoCtx = GetRepo(out var repo);

        // video171.mp4: Both streams are oversized, both should be re-encoded.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video171.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));

        // video172.mp4: Both streams are undersized, file should be preserved.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video172.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video173.mp4: Oversized video, undersized audio.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video173.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video174.mp4: Undersized video, oversized audio.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video174.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestSelectSmallestOnlyAffectsConfiguredStream()
    {
        // Tests that setting SelectSmallest for only video or only audio doesn't cause the other stream to change.

        using var repoCtx = GetRepo(out var repo);

        // video171.mp4 (oversized video & audio): SelectSmallest for video only - audio should be preserved.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video171.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video171.mp4 (oversized video & audio): SelectSmallest for audio only - video should be preserved.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video171.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    public static IEnumerable<object[]> TestSelectSmallestMetadataHandlingData => field ??= new bool[] { false, true }
        .SelectMany((x) => (IEnumerable<(string File, bool IsVideoOversized, bool IsAudioOversized, bool StripMetadata)>)[
            ("video188.mp4", true, true, x),
            ("video189.mp4", false, false, x),
            ("video190.mp4", true, false, x),
            ("video191.mp4", false, true, x),
        ])
        .SelectMany((x) => (IEnumerable<object[]>)[
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.SelectSmallest, StreamReencodeMode.Always],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.SelectSmallest],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest],
        ]);

    [TestMethod]
    [DynamicData(nameof(TestSelectSmallestMetadataHandlingData))]
    public async Task TestSelectSmallestMetadataHandling(
        string fileName,
        bool isVideoOversized,
        bool isAudioOversized,
        bool stripMetadata,
        StreamReencodeMode videoReencodeMode,
        StreamReencodeMode audioReencodeMode)
    {
        // This test checks that the SelectSmallest & metadata handling interact correctly. We take video171.mp4 (which has oversized video & audio streams),
        // and add metadata to both the container & the video stream, and then make a copy that is undersized (equivalent of video172.mp4) and copies that are
        // mixed oversized / undersized (equivalent of video173.mp4 and video174.mp4), but with the same metadata. We then test the combos of these files and
        // metadata stripping & SelectSmallest modes to check the interraction between the options works as intended.
        // Note: the modified versions are named 188-191.
        // Note: we run on all combos of modes (with at least 1 SelectSmallest) on the 4 input files.
        // Note: this also tests the interraction of SelectSmallest with non-SelectSmallest modes for other streams.

        using var repoCtx = GetRepo(out var repo);

        bool shouldVideoChange = videoReencodeMode switch
        {
            StreamReencodeMode.Always => true,
            StreamReencodeMode.SelectSmallest => isVideoOversized,
            StreamReencodeMode.AvoidReencoding => false,
            _ => throw new InvalidOperationException("Unexpected StreamReencodeMode value."),
        };
        bool shouldAudioChange = audioReencodeMode switch
        {
            StreamReencodeMode.Always => true,
            StreamReencodeMode.SelectSmallest => isAudioOversized,
            StreamReencodeMode.AvoidReencoding => false,
            _ => throw new InvalidOperationException("Unexpected StreamReencodeMode value."),
        };
        bool shouldFileChange = shouldVideoChange || shouldAudioChange || stripMetadata;

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = stripMetadata ? VideoMetadataStrippingMode.Required : VideoMetadataStrippingMode.None,
                VideoReencodeMode = videoReencodeMode,
                AudioReencodeMode = audioReencodeMode,
            },
            _videoFilesDir.CombineFile(fileName).PathExport,
            exceptionMessage: null,
            expectedChanges: !shouldFileChange ? null : (NewStreamCount: stripMetadata ? 2 : 3, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: !shouldVideoChange), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: !shouldAudioChange), // Audio stream
            ]),
            pathIsAbsolute: true,
            afterFinishedAction: async (processedFile) =>
            {
                string videoInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", processedFile, "-hide_banner", "-print_format", "json", "-show_streams", "-show_format", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    videoInfo.Contains("\"timecode\": \"00:00:05;00\"", StringComparison.Ordinal).ShouldBe(!stripMetadata);
                    videoInfo.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBe(!stripMetadata);
                }
                catch (Exception ex)
                {
                    throw new Exception("Video's metadata validation failed. Info: " + videoInfo, ex);
                }
            });
    }

    public static IEnumerable<object[]> TestSelectSmallestWithOversizedResolutionForMP4Data => field ??=
        ((IEnumerable<(string File, bool AreStreamsOversized, bool ForceRemux, bool LimitTo44100Hz)>)[
            ("video192.mkv", true, false, false),
            ("video192.mkv", true, false, true),
            ("video192.mkv", true, true, false),
            ("video192.mkv", true, true, true),
            ("video193.mkv", false, false, false),
            ("video193.mkv", false, false, true),
            ("video193.mkv", false, true, false),
            ("video193.mkv", false, true, true),
        ])
        .SelectMany((x) => (IEnumerable<object[]>)[
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.SelectSmallest, StreamReencodeMode.Always],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.SelectSmallest],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest],
        ]);

    [TestMethod]
    [DynamicData(nameof(TestSelectSmallestWithOversizedResolutionForMP4Data))]
    public async Task TestSelectSmallestWithOversizedResolutionForMP4(
        string fileName,
        bool areStreamsOversized,
        bool forceRemux,
        bool limitTo44100Hz,
        StreamReencodeMode videoReencodeMode,
        StreamReencodeMode audioReencodeMode)
    {
        // Tests how SelectSmallest mode interacts with videos that have resolution too large for MP4 container.
        // When the resolution exceeds MP4 limits, the video must be resized regardless of SelectSmallest outcome when we are remuxing or re-encoding (but the
        // video stream should only cause re-encoding when set to StreamReencodeMode.Always).
        // video192.mp4: oversized video, oversized audio, resolution too large for MP4.
        // video193.mp4: undersized video, undersized audio, resolution too large for MP4.
        // Note: this also tests that audio streams being re-encoded for other reasons (e.g. sample rate limiting) interacts correctly with SelectSmallest, and
        // that this interacts correctly with oversized video streams for MP4.

        using var repoCtx = GetRepo(out var repo);

        bool shouldVideoChange = videoReencodeMode != StreamReencodeMode.AvoidReencoding;
        bool shouldAudioChange = limitTo44100Hz || audioReencodeMode switch
        {
            StreamReencodeMode.Always => true,
            StreamReencodeMode.SelectSmallest => areStreamsOversized,
            StreamReencodeMode.AvoidReencoding => false,
            _ => throw new InvalidOperationException("Unexpected StreamReencodeMode value."),
        };
        bool shouldFileChange = forceRemux || shouldVideoChange || shouldAudioChange;

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = videoReencodeMode,
                AudioReencodeMode = audioReencodeMode,
                ResultFormats = forceRemux ? [MediaContainerFormat.MP4] : [MediaContainerFormat.MP4, MediaContainerFormat.Mkv],
                MaxSampleRate = limitTo44100Hz ? AudioSampleRate.Hz44100 : AudioSampleRate.Preserve,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: !shouldFileChange ? null : (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mkv", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: !shouldAudioChange), // Audio stream
            ]));
    }

    [TestMethod]
    [DataRow("video194.mkv", true, StreamReencodeMode.Always)]
    [DataRow("video194.mkv", true, StreamReencodeMode.AvoidReencoding)]
    [DataRow("video195.mkv", false, StreamReencodeMode.Always)]
    [DataRow("video195.mkv", false, StreamReencodeMode.AvoidReencoding)]
    public async Task TestSelectSmallestWithIncompatibleVideoCodecForMP4(
        string fileName,
        bool isVideoStreamOversized,
        StreamReencodeMode audioReencodeMode)
    {
        // Tests how SelectSmallest mode interacts with videos that have codecs incompatible with MP4 container.
        // When the video codec is not MP4-compatible, the video must be re-encoded regardless of SelectSmallest outcome when we are outputting to MP4.
        // video194.mkv: oversized video (VP9), AAC audio - video needs re-encoding for MP4 compatibility.
        // video195.mkv: undersized video (VP9), AAC audio - video needs re-encoding for MP4 compatibility.
        // This also tests that audio re-encoding mode (Always vs AvoidReencoding) interacts correctly with forced video re-encoding.

        using var repoCtx = GetRepo(out var repo);
        bool shouldAudioChange = audioReencodeMode != StreamReencodeMode.AvoidReencoding;
        bool shouldFileReencode = shouldAudioChange || isVideoStreamOversized;

        // File always changes because we force MP4 output and video codec is incompatible.

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = audioReencodeMode,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: !shouldFileReencode ? null : (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mkv", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: !shouldAudioChange), // Audio stream
            ]));
    }

    [TestMethod]
    [DataRow("Y0__auYqGXY-1s-low.mp4", false)]
    [DataRow("Y0__auYqGXY-1s-high.mp4", true)]
    public async Task TestSelectSmallestAndSideEffectsInteractions(
        string fileName,
        bool isVideoOversized)
    {
        // Tests that the side-effect of HDR->SDR conversion occurs correctly when a video is re-encoded for any reason
        // (but does not cause re-encoding by itself when RemapHDRToSDR is not set).
        // Y0__auYqGXY-1s-low.mp4: undersized HDR video - should not be re-encoded with SelectSmallest, so HDR should be preserved.
        // Y0__auYqGXY-1s-high.mp4: oversized HDR video - should be re-encoded with SelectSmallest, causing HDR->SDR conversion as a side effect.
        // When video is re-encoded (either via SelectSmallest choosing re-encode, or Always mode), color properties should become bt709.
        // When video is not re-encoded (AvoidReencoding, or SelectSmallest choosing copy), original HDR color properties should be preserved.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: !isVideoOversized ? null : (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
            ]),
            afterFinishedAction: async (processedFile) =>
            {
                string videoInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", processedFile, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    // Check color properties - bt709 indicates that we re-encoded to SDR, other values indicate HDR preserved.
                    bool hasSDRColorTransfer = videoInfo.Contains("\"color_transfer\": \"bt709\"", StringComparison.Ordinal);
                    bool hasSDRColorPrimaries = videoInfo.Contains("\"color_primaries\": \"bt709\"", StringComparison.Ordinal);
                    bool hasSDRColorSpace = videoInfo.Contains("\"color_space\": \"bt709\"", StringComparison.Ordinal);

                    if (isVideoOversized)
                    {
                        // When re-encoded, all three properties should be bt709 (SDR).
                        hasSDRColorTransfer.ShouldBeTrue("Expected color_transfer to be bt709 after re-encoding HDR->SDR");
                        hasSDRColorPrimaries.ShouldBeTrue("Expected color_primaries to be bt709 after re-encoding HDR->SDR");
                        hasSDRColorSpace.ShouldBeTrue("Expected color_space to be bt709 after re-encoding HDR->SDR");
                    }
                    else
                    {
                        // When not re-encoded, original HDR color properties should be preserved (not bt709).
                        hasSDRColorTransfer.ShouldBeFalse("Expected color_transfer to NOT be bt709 when HDR is preserved");
                        hasSDRColorPrimaries.ShouldBeFalse("Expected color_primaries to NOT be bt709 when HDR is preserved");
                        hasSDRColorSpace.ShouldBeFalse("Expected color_space to NOT be bt709 when HDR is preserved");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception("Video's HDR/SDR color property validation failed. Info: " + videoInfo, ex);
                }
            });
    }

    [TestMethod]
    [DataRow("video1.mp4", 2)]
    [DataRow("video2.mkv", 2)]
    [DataRow("video3.mov", 2)]
    [DataRow("video4.webm", 2)]
    [DataRow("video5.avi", 2)]
    [DataRow("video6.ts", 2)]
    [DataRow("video7.mpeg", 2)]
    [DataRow("video8.3gp", 2)]
    [DataRow("video9.avi", 2)]
    [DataRow("video10.mp4", 2)]
    [DataRow("video11.mkv", 2)]
    [DataRow("video12.mp4", 2)]
    [DataRow("video13.mp4", 2)]
    [DataRow("video14.mp4", 2)]
    [DataRow("video15.mp4", 2)]
    [DataRow("video16.mkv", 3)]
    [DataRow("video17.mkv", 3)]
    [DataRow("video18.mkv", 3)]
    [DataRow("video19.mkv", 3)]
    [DataRow("video20.mp4", 3)]
    [DataRow("video169.mkv", 3)]
    [DataRow("video170.mkv", 3)]
    public async Task TestAllInputCodecsAndFileFormatsForSelectSmallest(string fileName, int streamCount)
    {
        // Tests that SelectSmallest mode works correctly across all supported input codecs and file formats.
        // We run with 'ForceProgressiveDownload = true' to ensure that remuxing to mp4 occurs (we currently do not support skipping this when already done).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
                ForceProgressiveDownload = true,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: streamCount, StreamMapping: []));
    }

    [TestMethod]
    [DataRow("video188.mp4")]
    [DataRow("video189.mp4")]
    [DataRow("video190.mp4")]
    [DataRow("video191.mp4")]
    public async Task TestSelectSmallestWithMetadataStrippingPreservesLanguage(string fileName)
    {
        // Test our handling around the manual metadata we set in combination with our SelectSmallest logic.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
                MetadataStrippingMode = VideoMetadataStrippingMode.Required,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping: []),
            afterFinishedAction: async (processedFile) =>
            {
                string videoInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", processedFile, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    // Check we have 2 language metadata still.
                    int first = videoInfo.IndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
                    int last = videoInfo.LastIndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
                    first.ShouldBeGreaterThanOrEqualTo(0);
                    last.ShouldBeGreaterThanOrEqualTo(0);
                    first.ShouldNotBe(last);
                }
                catch (Exception ex)
                {
                    throw new Exception("Video's language metadata validation failed. Info: " + videoInfo, ex);
                }
            });
    }
}
