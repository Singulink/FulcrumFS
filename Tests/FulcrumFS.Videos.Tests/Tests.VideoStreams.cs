using System.Diagnostics;
using System.Globalization;
using Shouldly;
using Singulink.IO;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// This file contains the tests directly related to video stream processing handling.

partial class Tests
{
    [TestMethod]
    public async Task TestUnwantedOutputVideoCodecReencodes()
    {
        // Tests that video streams with codecs not in ResultVideoCodecs are re-encoded while audio is preserved.
        // Uses video10.mp4 (HEVC) with H264-only result to verify video re-encoding, audio stream copy.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultVideoCodecs = [VideoCodec.H264],
            },
            "video10.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestWantedOutputVideoCodecDoesntReencodeUnnecessarily()
    {
        // Tests that video streams already in an allowed ResultVideoCodecs codec are not unnecessarily re-encoded.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultVideoCodecs = [VideoCodec.HEVCAnyTag],
            },
            "video10.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestAlwaysReencodeVideoStreams()
    {
        // Tests that VideoReencodeMode.Always forces video stream re-encoding even when codec is acceptable.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: video re-encoded, audio unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video160.mp4: video-only, re-encoded.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
            ]));

        // video161.mp4: audio-only, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
            },
            "video161.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestMaxChromasubsamplingRespected()
    {
        // Tests that MaximumChromaSubsampling is correctly enforced, with downsampling when needed.

        using var repoCtx = GetRepo(out var repo);

        // Test video is 4:2:0, max is 4:2:0.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:0, max is 4:2:2.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:0, max is 4:4:4.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling444,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:2, max is 4:2:2.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
            },
            "video30.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:2, max is 4:4:4.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling444,
            },
            "video30.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:4:4, max is 4:4:4.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling444,
            },
            "video31.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:2, max is 4:2:0.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
            },
            "video30.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:0 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 4:4:4, max is 4:2:2.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
            },
            "video31.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:2 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 4:4:4, max is 4:2:0.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
            },
            "video31.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:0 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });
    }

    [TestMethod]
    public async Task TestMaxBitsPerSampleRespected()
    {
        // Tests that MaximumBitsPerChannel is correctly enforced, with downsampling when needed.

        using var repoCtx = GetRepo(out var repo);

        // Test video is 8 bpc, max is 8 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 8 bpc, max is 10 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits10,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 8 bpc, max is 12 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits12,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 10 bpc, max is 10 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits10,
            },
            "video32.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 10 bpc, max is 12 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits12,
            },
            "video32.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 12 bpc, max is 12 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits12,
            },
            "video43.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 10 bpc, max is 8 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
            },
            "video32.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 8 bpc now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumBitsPerChannel = BitsPerChannel.Bits8,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 12 bpc, max is 10 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits10,
            },
            "video43.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 10 bpc now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumBitsPerChannel = BitsPerChannel.Bits10,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 12 bpc, max is 8 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
            },
            "video43.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:0 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumBitsPerChannel = BitsPerChannel.Bits8,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });
    }

    [TestMethod]
    [DataRow("video1.mp4", 420, 8, false)] // video1.mp4 is yuv420p / pc
    [DataRow("video30.mp4", 422, 8, false)] // video30.mp4 is yuv422p / pc
    [DataRow("video31.mp4", 444, 8, false)] // video31.mp4 is yuv444p / pc
    [DataRow("video32.mp4", 420, 10, false)] // video32.mp4 is yuv420p10le / pc
    [DataRow("video33.mp4", 422, 10, false)] // video33.mp4 is yuv422p10le / pc
    [DataRow("video34.mp4", 444, 10, false)] // video34.mp4 is yuv444p10le / pc
    [DataRow("video35.mp4", 420, 8, false)] // video35.mp4 is yuv420p / tv
    [DataRow("video36.mp4", 422, 8, false)] // video36.mp4 is yuv422p / tv
    [DataRow("video37.mp4", 444, 8, false)] // video37.mp4 is yuv444p / tv
    [DataRow("video38.mp4", 420, 10, false)] // video38.mp4 is yuv420p10le / tv
    [DataRow("video39.mp4", 422, 10, false)] // video39.mp4 is yuv422p10le / tv
    [DataRow("video40.mp4", 444, 10, false)] // video40.mp4 is yuv444p10le / tv
    [DataRow("video41.mp4", 444, 8, true)] // video41.mp4 is gbrp / pc
    [DataRow("video42.mp4", 444, 10, true)] // video42.mp4 is gbrp10le / pc
    [DataRow("video43.mp4", 420, 12, false)] // video43.mp4 is yuv420p12le / pc
    [DataRow("video44.mp4", 422, 12, false)] // video44.mp4 is yuv422p12le / pc
    [DataRow("video45.mp4", 444, 12, false)] // video45.mp4 is yuv444p12le / pc
    [DataRow("video46.mp4", 444, 12, true)] // video46.mp4 is gbrp12le / pc
    [DataRow("video47.mp4", 420, 12, false)] // video47.mp4 is yuv420p12le / tv
    [DataRow("video48.mp4", 422, 12, false)] // video48.mp4 is yuv422p12le / tv
    [DataRow("video49.mp4", 444, 12, false)] // video49.mp4 is yuv444p12le / tv
    [DataRow("video50.mp4", 420, 10, false)] // video50.mp4 is yuv420p10le / pc
    [DataRow("video51.webm", 420, 8, true)] // video51.webm is yuva420p / pc
    [DataRow("video52.webm", 420, 8, true)] // video52.webm is yuva420p / tv
    public async Task TestPixelFormats(string fileName, int actualChromaSubsampling, int actualBpc, bool isAbnormal)
    {
        // Note: we are validate all of the following here: all known pixel formats are handled as expected, abnormal pixel formats are re-encoded when max
        // chroma subsampling is not "Preserve", max bits per channel / chroma subsampling correctly re-encodes / preserves, both pc & tv variants of pixel
        // formats (that support both variants) are handled correctly.

        using var repoCtx = GetRepo(out var repo);

        await Parallel.ForEachAsync(
        [
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Bits8),
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Bits10),
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Bits12),
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Preserve),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Bits8),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Bits10),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Bits12),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Preserve),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Bits8),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Bits10),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Bits12),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Preserve),
            (ChromaSubsampling.Preserve, BitsPerChannel.Bits8),
            (ChromaSubsampling.Preserve, BitsPerChannel.Bits10),
            (ChromaSubsampling.Preserve, BitsPerChannel.Bits12),
        ], TestContext.CancellationToken, async (info, _) =>
        {
            var (subsampling, bpc) = info;

            // Determine if we should reencode based on the actual vs. max settings:
            bool shouldReencode =
                (isAbnormal && subsampling != ChromaSubsampling.Preserve) ||
                (actualChromaSubsampling > subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 420,
                    ChromaSubsampling.Subsampling422 => 422,
                    ChromaSubsampling.Subsampling444 => 444,
                    ChromaSubsampling.Preserve => actualChromaSubsampling,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                }) ||
                (actualBpc > bpc switch
                {
                    BitsPerChannel.Bits8 => 8,
                    BitsPerChannel.Bits10 => 10,
                    BitsPerChannel.Bits12 => 12,
                    BitsPerChannel.Preserve => actualBpc,
                    _ => throw new UnreachableException("Unimplemented bits per channel value."),
                });

            // For normal pixel formats, we just want to validate that the bpc / chroma subsampling are detected correctly, as we have already tested
            // re-encoding / preserving combos in ValidateMaxChromaSubsamplingRespected / ValidateMaxBitsPerSampleRespected. Therefore, we only test exact
            // match, next lower bpc, and next lower chroma subsampling for normal pixel formats.
            if (!isAbnormal)
            {
                bool isExactMatch = actualChromaSubsampling == subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 420,
                    ChromaSubsampling.Subsampling422 => 422,
                    ChromaSubsampling.Subsampling444 => 444,
                    ChromaSubsampling.Preserve => int.MaxValue,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                };
                if (isExactMatch)
                {
                    isExactMatch = actualBpc == bpc switch
                    {
                        BitsPerChannel.Bits8 => 8,
                        BitsPerChannel.Bits10 => 10,
                        BitsPerChannel.Bits12 => 12,
                        BitsPerChannel.Preserve => int.MaxValue,
                        _ => throw new UnreachableException("Unimplemented bits per channel value."),
                    };
                }

                bool isNextLowerBpc = actualChromaSubsampling == subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 420,
                    ChromaSubsampling.Subsampling422 => 422,
                    ChromaSubsampling.Subsampling444 => 444,
                    ChromaSubsampling.Preserve => int.MaxValue,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                };
                if (isNextLowerBpc)
                {
                    isNextLowerBpc = actualBpc == bpc switch
                    {
                        BitsPerChannel.Bits8 => 10,
                        BitsPerChannel.Bits10 => 12,
                        BitsPerChannel.Bits12 => -1,
                        BitsPerChannel.Preserve => int.MaxValue,
                        _ => throw new UnreachableException("Unimplemented bits per channel value."),
                    };
                }

                bool isNextLowerChromaSubsampling = actualChromaSubsampling == subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 422,
                    ChromaSubsampling.Subsampling422 => 444,
                    ChromaSubsampling.Subsampling444 => -1,
                    ChromaSubsampling.Preserve => int.MaxValue,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                };
                if (isNextLowerChromaSubsampling)
                {
                    isNextLowerChromaSubsampling = actualBpc == bpc switch
                    {
                        BitsPerChannel.Bits8 => 8,
                        BitsPerChannel.Bits10 => 10,
                        BitsPerChannel.Bits12 => 12,
                        BitsPerChannel.Preserve => int.MaxValue,
                        _ => throw new UnreachableException("Unimplemented bits per channel value."),
                    };
                }

                if (!isExactMatch && !isNextLowerBpc && !isNextLowerChromaSubsampling)
                {
                    return;
                }
            }

            // Helper for our potential checks below:
            async Task Check(VideoProcessingOptions options, bool shouldReencode)
            {
                await CheckProcessing(
                    repo,
                    options,
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: shouldReencode ? (NewStreamCount: 2, StreamMapping:
                    [
                        (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                        (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
                    ]) : null, afterFinishedAction: shouldReencode ? async (newPath) =>
                    {
                        // Validate that we actually have what we expected now:
                        await CheckProcessing(
                            repo,
                            VideoProcessingOptions.Preserve with
                            {
                                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                                MaximumChromaSubsampling = subsampling,
                                MaximumBitsPerChannel = bpc,
                            },
                            newPath,
                            exceptionMessage: null,
                            expectedChanges: null,
                            pathIsAbsolute: true);
                    } : null);
            }

            // Run the processor & check if we reencoded or not as expected:
            await Check(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = subsampling,
                MaximumBitsPerChannel = bpc,
#if CI
                VideoCompressionLevel = VideoCompressionLevel.Low, // Use low compression level to speed up tests
#endif
            }, shouldReencode);

            // Validate it works in HEVC also:
            await Check(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
                ResultVideoCodecs = [VideoCodec.HEVC],
                MaximumChromaSubsampling = subsampling,
                MaximumBitsPerChannel = bpc,
#if CI
                VideoCompressionLevel = VideoCompressionLevel.Low, // Use low compression level to speed up tests
#endif
            }, true);

            // Validate it works in H.264 if we haven't already tested it (note: if shouldReencode is true, then we've already checked for H.264 earlier):
            if (!shouldReencode)
            {
                await Check(VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    VideoReencodeMode = StreamReencodeMode.Always,
                    ResultVideoCodecs = [VideoCodec.H264],
                    MaximumChromaSubsampling = subsampling,
                    MaximumBitsPerChannel = bpc,
#if CI
                    VideoCompressionLevel = VideoCompressionLevel.Low, // Use low compression level to speed up tests
#endif
                }, true);
            }
        });
    }

    public static IEnumerable<object?[]> TestSquarePixelHandlingData => field ??=
    [
        ["video1.mp4", true, null, (1, 1), (1, 1), (128, 72), (128, 72)],
        ["video1.mp4", false, null, (1, 1), (1, 1), (128, 72), (128, 72)],
        ["video1.mp4", true, (100, 100), (1, 1), (1, 1), (128, 72), (100, 56)],
        ["video1.mp4", false, (100, 100), (1, 1), (224, 225), (128, 72), (100, 56)],
        ["video1.mp4", true, (10, 10), (1, 1), (1, 1), (128, 72), (10, 6)],
        ["video1.mp4", false, (10, 10), (1, 1), (16, 15), (128, 72), (10, 6)],
        ["video111.mp4", true, null, (4, 3), (1, 1), (128, 128), (170, 128)],
        ["video111.mp4", false, null, (4, 3), (4, 3), (128, 128), (128, 128)],
        ["video111.mp4", true, (100, 100), (4, 3), (1, 1), (128, 128), (100, 76)],
        ["video111.mp4", false, (100, 100), (4, 3), (4, 3), (128, 128), (100, 100)],
        ["video111.mp4", true, (10, 10), (4, 3), (1, 1), (128, 128), (10, 8)],
        ["video111.mp4", false, (10, 10), (4, 3), (4, 3), (128, 128), (10, 10)],
        ["video166.mp4", true, null, (4, 3), (1, 1), (96, 128), (128, 128)],
        ["video166.mp4", false, null, (4, 3), (4, 3), (96, 128), (96, 128)],
        ["video166.mp4", true, (100, 100), (4, 3), (1, 1), (96, 128), (100, 100)],
        ["video166.mp4", false, (100, 100), (4, 3), (25, 19), (96, 128), (76, 100)],
        ["video166.mp4", true, (10, 10), (4, 3), (1, 1), (96, 128), (10, 10)],
        ["video166.mp4", false, (10, 10), (4, 3), (5, 4), (96, 128), (8, 10)],
    ];

    [TestMethod]
    [DynamicData(nameof(TestSquarePixelHandlingData))]
    public async Task TestSquarePixelHandling(
        string fileName,
        bool forceSquarePixels,
        (int W, int H)? maxSize,
        (int W, int H) inputSar,
        (int W, int H) outputSar,
        (int W, int H) inputSize,
        (int W, int H) outputSize)
    {
        // Tests handling of non-square pixel aspect ratios (SAR) and ForceSquarePixels option.
        // Verifies correct dimension/SAR transformations for various input files and resize settings.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceSquarePixels = forceSquarePixels,
            ResizeOptions = maxSize is not null ? new VideoResizeOptions(VideoResizeMode.FitDown, maxSize.Value.W, maxSize.Value.H) : null,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Validate our expectations:

        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {inputSize.W}"), StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {inputSize.H}"), StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"sample_aspect_ratio\": \"{inputSar.W}:{inputSar.H}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with original info: " + originalInfo, ex);
        }

        try
        {
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {outputSize.W}"), StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {outputSize.H}"), StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"sample_aspect_ratio\": \"{outputSar.W}:{outputSar.H}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with processed info: " + processedInfo, ex);
        }
    }

    public static IEnumerable<(bool H264, bool HEVC, object?[] Value)> TestResizeHandlingData => field ??=
    [
        (H264: true, HEVC: true, ["video1.mp4", null, null, (128, 72), (128, 72)]),
        (H264: true, HEVC: true, ["video1.mp4", null, (100, 100), (128, 72), (100, 56)]),
        (H264: true, HEVC: true, ["video1.mp4", null, (28, 28), (128, 72), (28, 16)]),
        (H264: true, HEVC: false, ["video1.mp4", null, (10, 10), (128, 72), (10, 6)]),
        (H264: false, HEVC: true, ["video1.mp4", "Cannot re-encode video to fit within specified dimensions.", (10, 10), (128, 72), null]),
        (H264: true, HEVC: true, ["video1.mp4", null, (50, 1000), (128, 72), (50, 28)]),
        (H264: true, HEVC: true, ["video1.mp4", null, (1000, 50), (128, 72), (88, 50)]),
        (H264: true, HEVC: true, ["video111.mp4", null, null, (128, 128), (128, 128)]),
        (H264: true, HEVC: true, ["video111.mp4", null, (100, 100), (128, 128), (100, 100)]),
        (H264: true, HEVC: true, ["video111.mp4", null, (16, 16), (128, 128), (16, 16)]),
        (H264: true, HEVC: false, ["video111.mp4", null, (10, 10), (128, 128), (10, 10)]),
        (H264: false, HEVC: true, ["video111.mp4", "Cannot re-encode video to fit within specified dimensions.", (10, 10), (128, 128), null]),
        (H264: true, HEVC: true, ["video111.mp4", "Cannot re-encode video to fit within specified dimensions.", (1, 1), (128, 128), null]),
        (H264: true, HEVC: true, ["video111.mp4", null, (50, 1000), (128, 128), (50, 50)]),
        (H264: true, HEVC: true, ["video111.mp4", null, (1000, 50), (128, 128), (50, 50)]),
        (H264: true, HEVC: true, ["video166.mp4", null, null, (96, 128), (96, 128)]),
        (H264: true, HEVC: true, ["video166.mp4", null, (100, 100), (96, 128), (76, 100)]),
        (H264: true, HEVC: true, ["video166.mp4", null, (22, 22), (96, 128), (16, 22)]),
        (H264: true, HEVC: false, ["video166.mp4", null, (10, 10), (96, 128), (8, 10)]),
        (H264: false, HEVC: true, ["video166.mp4", "Cannot re-encode video to fit within specified dimensions.", (10, 10), (96, 128), null]),
        (H264: true, HEVC: true, ["video166.mp4", null, (50, 1000), (96, 128), (50, 66)]),
        (H264: true, HEVC: true, ["video166.mp4", null, (1000, 50), (96, 128), (38, 50)]),
        (H264: true, HEVC: false, ["video143.mp4", null, null, (64, 65534), (16, 16384)]),
        (H264: false, HEVC: true, ["video143.mp4", null, null, (64, 65534), (64, 65534)]),
        (H264: true, HEVC: false, ["video146.mp4", null, null, (65535, 64), (16384, 16)]),
        (H264: false, HEVC: true, ["video146.mp4", null, null, (65535, 64), (65535, 64)]),
        (H264: true, HEVC: false, ["video136.mp4", null, null, (2, 16384), (2, 16384)]),
        (H264: false, HEVC: true, ["video136.mp4", "Cannot re-encode video to fit within specified dimensions.", null, (2, 16384), null]),
        (H264: false, HEVC: true, ["video185.mkv", null, null, (64, 65536), (64, 65534)]),
        (H264: true, HEVC: false, ["video185.mkv", null, null, (64, 65536), (16, 16384)]),
    ];

    public static IEnumerable<object?[]> TestH264ResizeHandlingData => field ??= TestResizeHandlingData.Where((x) => x.H264).Select((x) => x.Value);
    public static IEnumerable<object?[]> TestHEVCResizeHandlingData => field ??= TestResizeHandlingData.Where((x) => x.HEVC).Select((x) => x.Value);

    [TestMethod]
    [DynamicData(nameof(TestH264ResizeHandlingData))]
    public async Task TestResizeHandlingH264(
        string fileName, string? expectedError, (int W, int H)? maxSize, (int W, int H) inputSize, (int W, int H)? outputSize)
    {
        await TestResizeHandlingImpl(VideoCodec.H264, fileName, expectedError, maxSize, inputSize, outputSize);
    }

    [TestMethod]
    [DynamicData(nameof(TestHEVCResizeHandlingData))]
    public async Task TestResizeHandlingHEVC(
        string fileName, string? expectedError, (int W, int H)? maxSize, (int W, int H) inputSize, (int W, int H)? outputSize)
    {
        await TestResizeHandlingImpl(VideoCodec.HEVC, fileName, expectedError, maxSize, inputSize, outputSize);
    }

    private async Task TestResizeHandlingImpl(
        VideoCodec resultCodec, string fileName, string? expectedError, (int W, int H)? maxSize, (int W, int H) inputSize, (int W, int H)? outputSize)
    {
        // Tests H.264 / HEVC video resizing and validation of minimum dimension requirements for various input sizes.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            ResultVideoCodecs = [resultCodec],
            ResizeOptions = maxSize is not null ? new VideoResizeOptions(VideoResizeMode.FitDown, maxSize.Value.W, maxSize.Value.H) : null,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        IAbsoluteFilePath videoPath;

        if (expectedError is not null)
        {
            var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
            {
                await using var txn = await repo.BeginTransactionAsync();
                await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
                await txn.CommitAsync(TestContext.CancellationToken);
            });

            ex.Message.ShouldBe(expectedError);
            videoPath = null;
        }
        else
        {
            await using var txn = await repo.BeginTransactionAsync();
            var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);
            videoPath = await repo.GetAsync(fileId);
            videoPath.Exists.ShouldBeTrue();
        }

        // Validate our expectations:

        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {inputSize.W}"), StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {inputSize.H}"), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with original info: " + originalInfo, ex);
        }

        if (videoPath is null)
            return;

        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {outputSize!.Value.W}"), StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {outputSize.Value.H}"), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with processed info: " + processedInfo, ex);
        }
    }

    public static IEnumerable<object?[]> TestFpsLimitHandlingData => field ??=
    [
        ["video1.mp4", true, VideoFpsMode.LimitToExact, 24, (30, 1), (24, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitToExact, 15, (30, 1), (15, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitToExact, 10, (30, 1), (10, 1)],
        ["video1.mp4", false, VideoFpsMode.LimitToExact, 30, (30, 1), null],
        ["video1.mp4", false, VideoFpsMode.LimitToExact, 60, (30, 1), null],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 24, (30, 1), (15, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 15, (30, 1), (15, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 14, (30, 1), (10, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 10, (30, 1), (10, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 9, (30, 1), (15, 2)],
        ["video1.mp4", false, VideoFpsMode.LimitByIntegerDivision, 30, (30, 1), null],
        ["video1.mp4", false, VideoFpsMode.LimitByIntegerDivision, 60, (30, 1), null],
        ["video95.mp4", true, VideoFpsMode.LimitToExact, 30, (48000, 1001), (30, 1)],
        ["video95.mp4", true, VideoFpsMode.LimitToExact, 24, (48000, 1001), (24, 1)],
        ["video95.mp4", false, VideoFpsMode.LimitToExact, 48, (48000, 1001), null],
        ["video95.mp4", false, VideoFpsMode.LimitToExact, 60, (48000, 1001), null],
        ["video95.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (48000, 1001), (24000, 1001)],
        ["video95.mp4", true, VideoFpsMode.LimitByIntegerDivision, 24, (48000, 1001), (24000, 1001)],
        ["video95.mp4", true, VideoFpsMode.LimitByIntegerDivision, 23, (48000, 1001), (16000, 1001)],
        ["video95.mp4", false, VideoFpsMode.LimitByIntegerDivision, 48, (48000, 1001), null],
        ["video95.mp4", false, VideoFpsMode.LimitByIntegerDivision, 60, (48000, 1001), null],
        ["video100.mp4", true, VideoFpsMode.LimitToExact, 60, (300, 1), (60, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitToExact, 30, (300, 1), (30, 1)],
        ["video100.mp4", false, VideoFpsMode.LimitToExact, 300, (300, 1), null],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 60, (300, 1), (60, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 59, (300, 1), (50, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (300, 1), (30, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 29, (300, 1), (300, 11)],
        ["video100.mp4", false, VideoFpsMode.LimitByIntegerDivision, 300, (300, 1), null],
        ["video101.mp4", true, VideoFpsMode.LimitToExact, 60, (299999, 1000), (60, 1)],
        ["video101.mp4", true, VideoFpsMode.LimitToExact, 30, (299999, 1000), (30, 1)],
        ["video101.mp4", false, VideoFpsMode.LimitToExact, 300, (299999, 1000), null],
        ["video101.mp4", true, VideoFpsMode.LimitByIntegerDivision, 60, (299999, 1000), (299999, 5000)],
        ["video101.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (299999, 1000), (299999, 10000)],
        ["video101.mp4", false, VideoFpsMode.LimitByIntegerDivision, 300, (299999, 1000), null],
        ["video102.mp4", true, VideoFpsMode.LimitToExact, 60, (1000573, 4001), (60, 1)],
        ["video102.mp4", true, VideoFpsMode.LimitToExact, 30, (1000573, 4001), (30, 1)],
        ["video102.mp4", false, VideoFpsMode.LimitToExact, 251, (1000573, 4001), null],
        ["video102.mp4", true, VideoFpsMode.LimitByIntegerDivision, 60, (1000573, 4001), (1000573, 20005)],
        ["video102.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (1000573, 4001), (1000573, 36009)],
        ["video102.mp4", false, VideoFpsMode.LimitByIntegerDivision, 251, (1000573, 4001), null],
        ["video103.mp4", false, VideoFpsMode.LimitByIntegerDivision, 2, (1001000, 1000999), null],
        ["video103.mp4", true, VideoFpsMode.LimitByIntegerDivision, 1, (1001000, 1000999), (500500, 1000999)],
        ["video167.mp4", false, VideoFpsMode.LimitByIntegerDivision, 2, (1000999, 1000998), null],
        ["video167.mp4", true, VideoFpsMode.LimitByIntegerDivision, 1, (1000999, 1000998), (500500, 1000999)], // Note: ffmpeg rounds values above 1001000
    ];

    [TestMethod]
    [DynamicData(nameof(TestFpsLimitHandlingData))]
    public async Task TestFpsLimitHandling(
        string fileName, bool shouldReencode, VideoFpsMode mode, int targetFps, (int Num, int Den) inputFps, (int Num, int Den)? outputFps)
    {
        // Tests FPS limiting with both LimitToExact and LimitByIntegerDivision modes.
        // Verifies correct frame rate reduction for various input FPS values and target limits.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            FpsOptions = new VideoFpsOptions(mode, targetFps),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);
        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        if (!shouldReencode)
        {
            stream.Position = 0;
            await using var stream2 = videoPath.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
            (await AreStreamsEqual(stream, stream2, TestContext.CancellationToken)).ShouldBeTrue();
            return;
        }

        // Validate input FPS expectation:
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"r_frame_rate\": \"{inputFps.Num}/{inputFps.Den}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to validate input FPS with original info: " + originalInfo, ex);
        }

        // Validate output FPS expectation:
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        try
        {
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"r_frame_rate\": \"{outputFps!.Value.Num}/{outputFps.Value.Den}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception(
                $"Failed to validate output FPS (expected {outputFps!.Value.Num}/{outputFps.Value.Den}) with processed info: " + processedInfo,
                ex);
        }
    }

    [TestMethod]
    [DataRow("video1.mp4")]
    [DataRow("video9.avi")]
    [DataRow("video15.mp4")]
    [DataRow("video161.mp4")]
    public async Task TestProgressiveFileNotUnnecessarilyDeinterlaced(string fileName)
    {
        // This test verifies that progressive files are not unnecessarily processed when ForceProgressiveFrames is enabled.
        // The file should remain identical (not re-encoded or modified).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ForceProgressiveFrames = true,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestSquarePixelsFileNotUnnecessarilyReencoded()
    {
        // This test verifies that files with already square pixels are not unnecessarily processed when ForceSquarePixels is enabled.
        // video1.mp4 already has 1:1 SAR (square pixels), so it should remain identical.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ForceSquarePixels = true,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRemapHDRToSDRNotUnnecessarilyReencoded()
    {
        // This test verifies that SDR files are not unnecessarily processed when RemapHDRToSDR is enabled.
        // video1.mp4 is SDR, so it should remain identical.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemapHDRToSDR = true,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestResizeOptionsNotUnnecessarilyReencoded()
    {
        // This test verifies that files that already fit within the resize bounds are not unnecessarily processed.
        // video1.mp4 is 128x72, so specifying a larger max size should not cause re-encoding.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1920, 1080),
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    [DataRow("video10.mp4", false)]
    [DataRow("video164.mp4", true)]
    public async Task TestHEVCTagRemuxing(string fileName, bool isHvc1)
    {
        // Tests HEVC tag remuxing: using a result video codec of HEVC causes hev1-tagged files to be remuxed to hvc1,
        // while hvc1 files remain unchanged. HEVCAnyTag never triggers tag remuxing.

        using var repoCtx = GetRepo(out var repo);

        // When ResultVideoCodecs includes HEVC (hvc1), hev1 files should be remuxed to hvc1.
        // hvc1 files should remain unchanged.

        if (isHvc1)
        {
            // hvc1 file should not be changed:
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    ResultVideoCodecs = [VideoCodec.HEVC],
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: null);
        }
        else
        {
            // hev1 file should be remuxed to hvc1 (streams preserved but container changed):
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    ResultVideoCodecs = [VideoCodec.HEVC],
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: 2, StreamMapping:
                [
                    (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream was minorly changed (tag)
                    (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream unchanged
                ]));

            // Also, validate our exception handling by specifying only HEVC (hvc1) as source codec - should throw:
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    SourceVideoCodecs = [VideoCodec.HEVC],
                },
                fileName,
                exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
                expectedChanges: null);
        }

        // HEVCAnyTag should never remux just for tag purposes:
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultVideoCodecs = [VideoCodec.HEVCAnyTag],
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestNoUpscalingWithoutReencode()
    {
        // Tests that videos are not upscaled when resize options specify dimensions larger than the source.
        // video1.mp4 has dimensions 128x72 - we request 1920x1080 and expect no change.
        // Without re-encoding, the file should remain byte-for-byte identical (stream copy).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1920, 1080),
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestNoUpscalingWithForcedReencode()
    {
        // Tests that videos are not upscaled when resize options specify dimensions larger than the source.
        // video1.mp4 has dimensions 128x72 - we request 1920x1080 and expect no change to dimensions.
        // With forced re-encoding, the file will be re-encoded but should NOT be upscaled.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1920, 1080),
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video re-encoded
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true),  // Audio unchanged
            ]),
            afterFinishedAction: async (newPath) =>
            {
                // Verify dimensions are unchanged (not upscaled)
                string processedInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", newPath, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    processedInfo.Contains("\"width\": 128", StringComparison.Ordinal).ShouldBeTrue();
                    processedInfo.Contains("\"height\": 72", StringComparison.Ordinal).ShouldBeTrue();
                }
                catch (Exception ex)
                {
                    throw new Exception("Video was unexpectedly upscaled. Processed info: " + processedInfo, ex);
                }
            });
    }

    public static IEnumerable<object?[]> TestHEVCScalingNearLimitsData => field ??= [
        ["video176.mp4", (2, 527), (30, 1), (16, 4216), null],
        ["video177.mp4", (16, 4216), (30, 1), (16, 4216), null],
        ["video178.mp4", (16, 4217), (30, 1), (32, 8434), null],
        ["video179.mp4", (16, 4218), (30, 1), (32, 8436), null],
        ["video180.mp4", (8, 2109), (30, 1), (32, 8436), null],
        ["video181.mp4", (32, 64799), (30, 1), (32, 64799), null],
        ["video182.mp4", (63, 64799), (30, 1), (63, 64799), null],
        ["video183.mp4", (32, 64798), (30, 1), (32, 64798), null],
        ["video184.mp4", (64, 64799), (30, 1), (64, 64799), null],
        ["video197.mp4", (16, 4208), (1985, 1), (16, 4208), null],
        ["video198.mp4", (16, 4208), (1986, 1), (32, 8416), null],
        ["video199.mp4", (26, 4208), (992, 1), (26, 4208), null],
        ["video200.mp4", (26, 4208), (993, 1), (32, 5180), null],
        ["video176.mp4", (2, 527), (30, 1), (16, 4216), (16, 4216)],
        ["video177.mp4", (16, 4216), (30, 1), (16, 4216), (16, 4216)],
        ["video178.mp4", (16, 4217), (30, 1), null, (16, 4217)],
        ["video179.mp4", (16, 4218), (30, 1), null, (16, 4218)],
        ["video180.mp4", (8, 2109), (30, 1), null, (16, 4218)],
        ["video181.mp4", (32, 64799), (30, 1), (32, 64799), (32, 64799)],
        ["video182.mp4", (63, 64799), (30, 1), (63, 64799), (63, 64799)],
        ["video183.mp4", (32, 64798), (30, 1), (32, 64798), (32, 64798)],
        ["video184.mp4", (64, 64799), (30, 1), (64, 64799), (64, 64799)],
        ["video197.mp4", (16, 4208), (1985, 1), (16, 4208), (16, 4208)],
        ["video198.mp4", (16, 4208), (1986, 1), null, (16, 4208)],
        ["video199.mp4", (26, 4208), (992, 1), (26, 4208), (26, 4208)],
        ["video200.mp4", (26, 4208), (993, 1), null, (26, 4208)],
    ];

    [TestMethod]
    [DynamicData(nameof(TestHEVCScalingNearLimitsData))]
    public async Task TestHEVCScalingNearLimits(
        string fileName,
        (int W, int H) inputSize,
        (int Num, int Den) inputFps,
        (int W, int H)? expectedOutputSize,
        (int W, int H)? maxSize)
    {
        // Tests HEVC video encoding near the codec's dimension limits, verifying that videos are scaled as expected to meet HEVC minimum dimension
        // requirements when the source dimensions are too small so that re-encoding these videos to HEVC doesn't fail (unless we expect it to do so due to a
        // max size limitation).

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            ResultVideoCodecs = [VideoCodec.HEVC],
            ResizeOptions = maxSize != null ? new VideoResizeOptions(VideoResizeMode.FitDown, maxSize.Value.W, maxSize.Value.H) : null,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        // Verify input dimensions match expected size:
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains($"\"width\": {inputSize.W}", StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains($"\"height\": {inputSize.H}", StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains($"\"r_frame_rate\": \"{inputFps.Num}/{inputFps.Den}\"", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify input video dimensions. Info: " + originalInfo, ex);
        }

        // Run processing
        FileId fileId;
        if (expectedOutputSize is null)
        {
            var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
            {
                await using var txn = await repo.BeginTransactionAsync();
                fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
                await txn.CommitAsync(TestContext.CancellationToken);
            });
            ex.Message.ShouldContain("Cannot re-encode video to fit within specified dimensions.", Case.Sensitive);
            return;
        }
        else
        {
            await using var txn = await repo.BeginTransactionAsync();
            fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Verify output dimensions match expected scaled size
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            processedInfo.Contains($"\"width\": {expectedOutputSize.Value.W}", StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains($"\"height\": {expectedOutputSize.Value.H}", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify output video dimensions. Info: " + processedInfo, ex);
        }
    }

    public static IEnumerable<object?[]> TestChromaSubsamplingResolutionRoundingData => field ??= [
        ["video186.mp4", ChromaSubsampling.Subsampling420, (97, 97), (96, 96)],
        ["video186.mp4", ChromaSubsampling.Subsampling422, (97, 97), (96, 97)],
        ["video186.mp4", ChromaSubsampling.Subsampling444, (97, 97), (97, 97)],
        ["video187.mp4", ChromaSubsampling.Subsampling420, (99, 99), (100, 100)],
        ["video187.mp4", ChromaSubsampling.Subsampling422, (99, 99), (100, 99)],
        ["video187.mp4", ChromaSubsampling.Subsampling444, (99, 99), (99, 99)],
    ];

    [TestMethod]
    [DynamicData(nameof(TestChromaSubsamplingResolutionRoundingData))]
    public async Task TestChromaSubsamplingResolutionRounding(
        string fileName, ChromaSubsampling maxChromaSubsampling, (int W, int H) inputSize, (int W, int H) expectedOutputSize)
    {
        // Tests that video dimensions are correctly rounded to even values as required by chroma subsampling settings.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            MaximumChromaSubsampling = maxChromaSubsampling,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        // Verify input dimensions match expected size
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains($"\"width\": {inputSize.W}", StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains($"\"height\": {inputSize.H}", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify input video dimensions. Info: " + originalInfo, ex);
        }

        // Run processing
        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Verify output dimensions match expected rounded size
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            processedInfo.Contains($"\"width\": {expectedOutputSize.W}", StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains($"\"height\": {expectedOutputSize.H}", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify output video dimensions. Info: " + processedInfo, ex);
        }
    }
}
