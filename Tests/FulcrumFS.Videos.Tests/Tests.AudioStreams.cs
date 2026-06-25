using System.Globalization;
using Shouldly;

#pragma warning disable SA1118 // Parameter should not span multiple lines

namespace FulcrumFS.Videos;

// This file contains the tests directly related to audio stream processing handling.

partial class Tests
{
    [TestMethod]
    public async Task TestUnwantedOutputAudioCodecReencodes()
    {
        // Tests that audio streams with codecs not in ResultAudioCodecs are re-encoded while video is preserved.
        // Uses video14.mp4 (Vorbis audio) with AAC-only result to verify audio re-encoding, video stream copy.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultAudioCodecs = [AudioCodec.AAC],
            },
            "video14.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestWantedOutputAudioCodecDoesntReencodeUnnecessarily()
    {
        // Tests that audio streams already in an allowed ResultAudioCodecs codec are not unnecessarily re-encoded.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultAudioCodecs = [AudioCodec.AAC, AudioCodec.Vorbis],
            },
            "video14.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRemoveAudioStreamAdjustment()
    {
        // Tests that RemoveAudioStreams correctly strips audio streams from video files.
        // video160.mp4 (video-only) is unchanged.

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
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
            ]));
    }

    [TestMethod]
    public async Task TestAlwaysReencodeAudioStreams()
    {
        // Tests that AudioReencodeMode.Always forces audio stream re-encoding even when codec is acceptable.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: audio re-encoded, video unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));

        // video160.mp4: video-only, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video161.mp4: audio-only, re-encoded.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video161.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestMaxChannelsNotUnnecessarilyReencoded()
    {
        // This test verifies that audio files that already have fewer or equal channels are not unnecessarily processed.
        // video1.mp4 has stereo audio, so specifying Stereo or higher max should not cause re-encoding.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaxChannels = AudioChannels.Stereo,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestMaxSampleRateNotUnnecessarilyReencoded()
    {
        // This test verifies that audio files that already have a lower or equal sample rate are not unnecessarily processed.
        // video1.mp4 has 44.1kHz audio, so specifying 44.1kHz or higher max should not cause re-encoding.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaxSampleRate = AudioSampleRate.Hz44100,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    // video1.mp4 is stereo (2 channels), video57.mp4 is mono (1 channel), video60.mp4 is 5.1 surround sound (6 channels)
    [TestMethod]
    [DataRow("video1.mp4", AudioChannels.Mono, true)]
    [DataRow("video1.mp4", AudioChannels.Stereo, false)]
    [DataRow("video57.mp4", AudioChannels.Mono, false)]
    [DataRow("video57.mp4", AudioChannels.Stereo, false)]
    [DataRow("video60.mp4", AudioChannels.Mono, true)]
    [DataRow("video60.mp4", AudioChannels.Stereo, true)]
    public async Task TestMaxChannelsRespected(string fileName, AudioChannels maxChannels, bool shouldReencode)
    {
        // Tests MaxChannels enforcement: audio exceeding the limit is re-encoded/downmixed, compliant files remain unchanged.
        // Covers mono, stereo, and 5.1 surround source files against mono and stereo limits.

        using var repoCtx = GetRepo(out var repo);

        if (!shouldReencode)
        {
            // Should not re-encode - file should remain identical.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxChannels = maxChannels,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: null);
        }
        else
        {
            // Should re-encode audio only.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxChannels = maxChannels,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: 2, StreamMapping:
                [
                    (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream unchanged
                    (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream re-encoded
                ]), afterFinishedAction: async (newPath) =>
                {
                    // Validate that the result now meets the max channels constraint:
                    await CheckProcessing(
                        repo,
                        VideoProcessingOptions.Preserve with
                        {
                            ForceValidateAllStreams = DefaultForceValidateAllStreams,
                            MaxChannels = maxChannels,
                        },
                        newPath,
                        exceptionMessage: null,
                        expectedChanges: null,
                        pathIsAbsolute: true);
                });
        }
    }

    [TestMethod]
    [DataRow("video1.mp4", AudioSampleRate.Preserve, false, false, 44100, 44100)]
    [DataRow("video67.mp4", AudioSampleRate.Preserve, false, false, 8000, 8000)]
    [DataRow("video68.mp4", AudioSampleRate.Preserve, false, false, 11025, 11025)]
    [DataRow("video69.mp4", AudioSampleRate.Preserve, false, false, 12000, 12000)]
    [DataRow("video70.mp4", AudioSampleRate.Preserve, false, false, 16000, 16000)]
    [DataRow("video71.mp4", AudioSampleRate.Preserve, false, false, 22050, 22050)]
    [DataRow("video72.mp4", AudioSampleRate.Preserve, false, false, 24000, 24000)]
    [DataRow("video73.mp4", AudioSampleRate.Preserve, false, false, 32000, 32000)]
    [DataRow("video74.mp4", AudioSampleRate.Preserve, false, false, 48000, 48000)]
    [DataRow("video75.mp4", AudioSampleRate.Preserve, false, false, 64000, 64000)]
    [DataRow("video76.mp4", AudioSampleRate.Preserve, false, false, 88200, 88200)]
    [DataRow("video77.mp4", AudioSampleRate.Preserve, false, false, 96000, 96000)]
    [DataRow("video78.mp4", AudioSampleRate.Preserve, false, false, 192000, 192000)]
    [DataRow("video79.mp4", AudioSampleRate.Preserve, false, false, 200000, 200000)]
    [DataRow("video80.mp4", AudioSampleRate.Preserve, false, false, 4000, 4000)]
    [DataRow("video81.mp4", AudioSampleRate.Preserve, false, false, 22, 22)]
    [DataRow("video82.mp4", AudioSampleRate.Preserve, false, false, 45000, 45000)]
    [DataRow("video1.mp4", AudioSampleRate.Preserve, true, true, 44100, 44100)]
    [DataRow("video67.mp4", AudioSampleRate.Preserve, true, true, 8000, 8000)]
    [DataRow("video68.mp4", AudioSampleRate.Preserve, true, true, 11025, 11025)]
    [DataRow("video69.mp4", AudioSampleRate.Preserve, true, true, 12000, 12000)]
    [DataRow("video70.mp4", AudioSampleRate.Preserve, true, true, 16000, 16000)]
    [DataRow("video71.mp4", AudioSampleRate.Preserve, true, true, 22050, 22050)]
    [DataRow("video72.mp4", AudioSampleRate.Preserve, true, true, 24000, 24000)]
    [DataRow("video73.mp4", AudioSampleRate.Preserve, true, true, 32000, 32000)]
    [DataRow("video74.mp4", AudioSampleRate.Preserve, true, true, 48000, 48000)]
    [DataRow("video75.mp4", AudioSampleRate.Preserve, true, true, 64000, 64000)]
    [DataRow("video76.mp4", AudioSampleRate.Preserve, true, true, 88200, 88200)]
    [DataRow("video77.mp4", AudioSampleRate.Preserve, true, true, 96000, 96000)]
    [DataRow("video78.mp4", AudioSampleRate.Preserve, true, true, 192000, 96000)]
    [DataRow("video80.mp4", AudioSampleRate.Preserve, true, true, 4000, 8000)]
    [DataRow("video82.mp4", AudioSampleRate.Preserve, true, true, 45000, 48000)]
    [DataRow("video77.mp4", AudioSampleRate.Hz48000, true, false, 96000, 48000)]
    [DataRow("video74.mp4", AudioSampleRate.Hz48000, false, false, 48000, 48000)]
    [DataRow("video1.mp4", AudioSampleRate.Hz48000, false, false, 44100, 44100)]
    [DataRow("video82.mp4", AudioSampleRate.Hz48000, false, false, 45000, 45000)]
    [DataRow("video82.mp4", AudioSampleRate.Hz48000, true, true, 45000, 48000)]
    [DataRow("video1.mp4", AudioSampleRate.Hz44100, false, false, 44100, 44100)]
    [DataRow("video74.mp4", AudioSampleRate.Hz44100, true, false, 48000, 44100)]
    [DataRow("video82.mp4", AudioSampleRate.Hz44100, true, false, 45000, 44100)]
    public async Task TestMaxSampleRateRespected(
        string fileName, AudioSampleRate maxSampleRate, bool shouldReencode, bool forceReencode, int inputSampleRate, int expectedOutputSampleRate)
    {
        // Tests MaxSampleRate enforcement and sample rate resampling. Validates that audio sample rates exceeding the limit
        // are resampled, unusual rates are converted to valid AAC rates, and compliant files remain unchanged.

        using var repoCtx = GetRepo(out var repo);

        var origFile = _videoFilesDir.CombineFile(fileName);

        // Validate the input file has the expected sample rate:
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"sample_rate\": \"{inputSampleRate}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception($"Expected input sample rate {inputSampleRate} not found in original file info: " + originalInfo, ex);
        }

        if (!shouldReencode)
        {
            // Should not re-encode - file should remain identical.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxSampleRate = maxSampleRate,
                    AudioReencodeMode = forceReencode ? StreamReencodeMode.Always : StreamReencodeMode.AvoidReencoding,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: null);
        }
        else
        {
            // Should re-encode audio only.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxSampleRate = maxSampleRate,
                    AudioReencodeMode = forceReencode ? StreamReencodeMode.Always : StreamReencodeMode.AvoidReencoding,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: 2, StreamMapping:
                [
                    (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream unchanged
                    (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream re-encoded
                ]), afterFinishedAction: async (newPath) =>
                {
                    // Validate the output file has the expected sample rate:
                    string processedInfo = await RunFFtoolProcessWithErrorHandling(
                        "ffprobe",
                        ["-i", newPath, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                        TestContext.CancellationToken);

                    try
                    {
                        processedInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                            $"\"sample_rate\": \"{expectedOutputSampleRate}\""), StringComparison.Ordinal).ShouldBeTrue();
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Expected output sample rate {expectedOutputSampleRate} not found in processed file info: " + processedInfo, ex);
                    }

                    // Validate that the result now meets the constraints (valid AAC rate and within max):
                    await CheckProcessing(
                        repo,
                        VideoProcessingOptions.Preserve with
                        {
                            ForceValidateAllStreams = DefaultForceValidateAllStreams,
                            MaxSampleRate = maxSampleRate,
                        },
                        newPath,
                        exceptionMessage: null,
                        expectedChanges: null,
                        pathIsAbsolute: true);
                });
        }
    }
}
