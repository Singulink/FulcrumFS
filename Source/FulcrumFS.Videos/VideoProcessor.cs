using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Singulink.IO;
using Singulink.Threading;

namespace FulcrumFS.Videos;

/// <summary>
/// Provides functionality to process video files with specified options.
/// </summary>
public sealed class VideoProcessor : FileProcessor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoProcessor"/> class with the specified options.
    /// Note: you must configure the ffmpeg executable paths by calling <see cref="ConfigureWithFFmpegExecutables"/> before creating an instance of this class.
    /// </summary>
    public VideoProcessor(VideoProcessingOptions options)
    {
        Options = options;

        // Check if ffmpeg is configured:
        _ = FFmpegExePath;

        // Check if the required decoders / demuxers are available:

        foreach (var codec in Options.SourceVideoCodecs)
        {
            if (!codec.HasSupportedDecoder)
                throw new NotSupportedException($"The required video codec '{codec.Name}' is not supported by the configured ffmpeg installation.");
        }

        foreach (var codec in Options.SourceAudioCodecs)
        {
            if (!codec.HasSupportedDecoder)
                throw new NotSupportedException($"The required audio codec '{codec.Name}' is not supported by the configured ffmpeg installation.");
        }

        foreach (var format in Options.SourceFormats)
        {
            string formatName = format.Name;
            if (!format.HasSupportedDemuxer)
                throw new NotSupportedException($"The required media container format '{formatName}' is not supported by the configured ffmpeg installation.");
        }

        // Check if the required encoders / muxers are available (note: we only check for the first result codec / format, as that is what will be used):

        if (!(Options.ResultVideoCodecs[0] == VideoCodec.H264
            ? FFprobeUtils.Configuration.SupportsLibX264Encoder
            : FFprobeUtils.Configuration.SupportsLibX265Encoder))
        {
            string codecName = Options.ResultVideoCodecs[0].Name;
            throw new NotSupportedException($"The required video encoder for '{codecName}' is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsLibFDKAACEncoder && !FFprobeUtils.Configuration.SupportsAACEncoder)
        {
            throw new NotSupportedException("The required audio encoder for 'aac' is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsMP4Muxing)
        {
            throw new NotSupportedException("The required media container muxer for 'mp4' is not supported by the configured ffmpeg installation.");
        }
    }

    /// <summary>
    /// Gets the options used to configure this <see cref="VideoProcessor" />.
    /// </summary>
    public VideoProcessingOptions Options { get; }

    /// <summary>
    /// Configures the directory containing ffmpeg binaries to use for processing.
    /// On Windows: should contain ffmpeg.exe and ffprobe.exe.
    /// On Linux/macOS: should contain ffmpeg and ffprobe executables with appropriate execute permissions.
    /// </summary>
    /// <param name="dirPath">The directory path containing the ffmpeg executables.</param>
    /// <param name="maxConcurrentProcesses">The maximum number of concurrent ffmpeg processes to allow. Default is currently 32.</param>
    public static void ConfigureWithFFmpegExecutables(IAbsoluteDirectoryPath dirPath, int maxConcurrentProcesses = 32)
    {
        var (ffmpeg, ffprobe) = OperatingSystem.IsWindows()
            ? (dirPath.CombineFile("ffmpeg.exe"), dirPath.CombineFile("ffprobe.exe"))
            : (dirPath.CombineFile("ffmpeg"), dirPath.CombineFile("ffprobe"));

        if (!ffmpeg.Exists)
            throw new FileNotFoundException("FFmpeg executable not found in specified directory.", ffmpeg.ToString());

        if (!ffprobe.Exists)
            throw new FileNotFoundException("FFprobe executable not found in specified directory.", ffprobe.ToString());

        if (maxConcurrentProcesses < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentProcesses), "Maximum concurrent processes must be at least 1.");

        if (!_ffmpegPathInitialized.TrySet())
            throw new InvalidOperationException("FFmpeg executable paths have already been initialized.");

        FFmpegExePath = ffmpeg;
        FFprobeExePath = ffprobe;
        MaxConcurrentProcesses = maxConcurrentProcesses;
    }

    private static InterlockedFlag _ffmpegPathInitialized;

    internal static IAbsoluteFilePath FFmpegExePath
    {
        get => field ?? throw new InvalidOperationException(
            "Cannot access ffmpeg executable path before it has been initialized. Call ConfigureWithFFmpegExecutables first.");
        private set;
    }

    internal static IAbsoluteFilePath FFprobeExePath
    {
        get => field ?? throw new InvalidOperationException(
            "Cannot access ffprobe executable path before it has been initialized. Call ConfigureWithFFmpegExecutables first.");
        private set;
    }

    internal static int MaxConcurrentProcesses
    {
        get => field switch
        {
            0 => throw new InvalidOperationException(
                "Cannot access MaxConcurrentProcesses before it has been initialized. Call ConfigureWithFFmpegExecutables first."),
            var v => v,
        };
        private set;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<string> AllowedFileExtensions => field ??= [.. Options.SourceFormats.SelectMany(f => f.CommonExtensions).Distinct()];

    /// <inheritdoc/>
    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        // Get temp file for video:
        var tempOutputFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
        var sourceFileWithCorrectExtension = tempOutputFile;
        bool hasChangedExtension = false;

        // Read info of source video:
        var sourceInfo = await FFprobeUtils.GetVideoFileAsync(tempOutputFile, context.CancellationToken).ConfigureAwait(false);

        // Figure out which source format it supposedly is & check if we support it & that it matches the file extension:
        var sourceFormat = Options.SourceFormats
            .FirstOrDefault((x) => x.NameMatches(sourceInfo.FormatName))
                ?? throw new FileProcessingException($"The source video format '{sourceInfo.FormatName}' is not supported by this processor.");

        // If file extension does not match the source format, ensure we still detect the same format after copying to the correct extension:
        if (!sourceFormat.CommonExtensions.Contains(context.Extension, StringComparer.OrdinalIgnoreCase))
        {
            hasChangedExtension = true;
            var tempOutputFile2 = context.GetNewWorkFile(sourceFormat.PrimaryExtension);
            sourceFileWithCorrectExtension = tempOutputFile2;
            var sourceStream = tempOutputFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read);
            await using (sourceStream.ConfigureAwait(false))
            {
                var destStream = tempOutputFile2.OpenAsyncStream(FileMode.Create, FileAccess.Write);
                await using (destStream.ConfigureAwait(false))
                {
                    await sourceStream.CopyToAsync(destStream, context.CancellationToken).ConfigureAwait(false);
                }
            }

            var sourceInfo2 = await FFprobeUtils.GetVideoFileAsync(tempOutputFile2, context.CancellationToken).ConfigureAwait(false);

            if (sourceInfo2.FormatName != sourceInfo.FormatName)
                throw new FileProcessingException("The video format is inconsistent with its file extension in a potentially malicious way.");
        }

        // Validate source streams:

        int numVideoStreams = 0;
        int numAudioStreams = 0;
        bool anyWouldReencodeForReencodeOptionalPurposes = false;
        bool anyInvalidCodecs = false;
        foreach (var stream in sourceInfo.Streams)
        {
            if (stream is FFprobeUtils.VideoStreamInfo videoStream)
            {
                // Skip thumbnails
                if (videoStream.IsAttachedPic || videoStream.IsTimedThumbnail)
                {
                    continue;
                }

                int idx = numVideoStreams++;

                double? duration = videoStream.Duration ?? sourceInfo.Duration;
                if (duration is not null)
                {
                    if (Options.VideoSourceValidation.MaxLength.HasValue && duration > Options.VideoSourceValidation.MaxLength.Value.TotalSeconds)
                        throw new FileProcessingException($"Video stream {idx} is longer than maximum allowed duration.");

                    if (Options.VideoSourceValidation.MinLength.HasValue && duration < Options.VideoSourceValidation.MinLength.Value.TotalSeconds)
                        throw new FileProcessingException($"Video stream {idx} is shorter than minimum allowed duration.");
                }
                else if (Options.VideoSourceValidation.MaxLength.HasValue || Options.VideoSourceValidation.MinLength.HasValue)
                {
                    throw new FileProcessingException($"Video stream {idx} has unknown duration, cannot validate.");
                }

                if (Options.VideoSourceValidation.MaxWidth.HasValue)
                {
                    if (videoStream.Width > Options.VideoSourceValidation.MaxWidth.Value)
                        throw new FileProcessingException($"Video stream {idx} width exceeds maximum allowed.");

                    if (videoStream.Width <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown width, cannot validate.");
                }

                if (Options.VideoSourceValidation.MaxHeight.HasValue)
                {
                    if (videoStream.Height > Options.VideoSourceValidation.MaxHeight.Value)
                        throw new FileProcessingException($"Video stream {idx} height exceeds maximum allowed.");

                    if (videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown height, cannot validate.");
                }

                if (Options.VideoSourceValidation.MaxPixels.HasValue)
                {
                    if ((long)videoStream.Width * videoStream.Height > Options.VideoSourceValidation.MaxPixels.Value)
                        throw new FileProcessingException($"Video stream {idx} exceeds maximum allowed pixel count.");

                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown dimensions, cannot validate.");
                }

                if (Options.VideoSourceValidation.MinWidth.HasValue)
                {
                    if (videoStream.Width <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown width, cannot validate.");

                    if (videoStream.Width < Options.VideoSourceValidation.MinWidth.Value)
                        throw new FileProcessingException($"Video stream {idx} width is less than minimum required.");
                }

                if (Options.VideoSourceValidation.MinHeight.HasValue)
                {
                    if (videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown height, cannot validate.");

                    if (videoStream.Height < Options.VideoSourceValidation.MinHeight.Value)
                        throw new FileProcessingException($"Video stream {idx} height is less than minimum required.");
                }

                if (Options.VideoSourceValidation.MinPixels.HasValue)
                {
                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown dimensions, cannot validate.");

                    if ((long)videoStream.Width * videoStream.Height < Options.VideoSourceValidation.MinPixels.Value)
                        throw new FileProcessingException($"Video stream {idx} is less than minimum required pixel count.");
                }

                // Check codec:
                if (MatchVideoCodecByName(Options.SourceVideoCodecs, videoStream.CodecName) is null)
                {
                    anyInvalidCodecs = true;
                    continue;
                }

                // If resizing is enabled, check if we would resize:
                if (Options.ResizeOptions is { } resizeOptions)
                {
                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                    {
                        throw new FileProcessingException($"Video stream {idx} has unknown dimensions, cannot determine resizing.");
                    }

                    if ((videoStream.Width > resizeOptions.Width) || (videoStream.Height > resizeOptions.Height))
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                }

                // Check if we can get the pixel format info:
                var pixFormatInfo = GetPixelFormatCharacteristics(videoStream.PixelFormat);
                int bitsPerSample = pixFormatInfo?.BitsPerSample ?? videoStream.BitsPerSample;

                // Try to check the bits/channel if necessary:
                if (Options.MaximumBitsPerChannel != BitsPerChannel.Preserve)
                {
                    if (bitsPerSample <= 0)
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                    else if (bitsPerSample > (GetBitsPerChannel(Options.MaximumBitsPerChannel) ?? int.MaxValue))
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                    else if (pixFormatInfo is not null && bitsPerSample != pixFormatInfo.Value.BitsPerSample)
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                }

                // Try to check the chroma subsampling if necessary:
                if (Options.MaximumChromaSubsampling != ChromaSubsampling.Preserve)
                {
                    if (pixFormatInfo is null)
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                    else if (
                        !pixFormatInfo.Value.IsStandard ||
                        !pixFormatInfo.Value.IsYUV ||
                        pixFormatInfo.Value.ChromaSubsampling > (GetChromaSubsampling(Options.MaximumChromaSubsampling) ?? 444))
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                }

                // Check fps:
                int? targetFps = Options.FpsOptions?.TargetFps;
                if (targetFps is not null && (
                    videoStream.FpsNum <= 0 ||
                    videoStream.FpsDen <= 0 ||
                    (long)videoStream.FpsNum > (long)videoStream.FpsDen * targetFps.Value))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                    continue;
                }

                // Check if hdr:
                if (Options.RemapHDRToSDR && !IsKnownSDRColorProfile(videoStream.ColorTransfer, videoStream.ColorPrimaries, videoStream.ColorSpace))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                }
            }
            else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
            {
                int idx = numAudioStreams++;

                double? duration = audioStream.Duration ?? sourceInfo.Duration;
                if (duration is not null)
                {
                    if (Options.AudioSourceValidation.MaxLength.HasValue && duration > Options.AudioSourceValidation.MaxLength.Value.TotalSeconds)
                        throw new FileProcessingException($"Audio stream {idx} is longer than maximum allowed duration.");

                    if (Options.AudioSourceValidation.MinLength.HasValue && duration < Options.AudioSourceValidation.MinLength.Value.TotalSeconds)
                        throw new FileProcessingException($"Audio stream {idx} is shorter than minimum required duration.");
                }
                else if (Options.AudioSourceValidation.MaxLength.HasValue || Options.AudioSourceValidation.MinLength.HasValue)
                {
                    throw new FileProcessingException($"Audio stream {idx} has unknown duration, cannot validate.");
                }

                // Check codec:
                if (MatchAudioCodecByName(Options.SourceAudioCodecs, audioStream.CodecName, audioStream.ProfileName) is null)
                {
                    anyInvalidCodecs = true;
                    continue;
                }

                // Check channels:
                if ((Options.MaxChannels != AudioChannels.Preserve && audioStream.Channels <= 0) ||
                    audioStream.Channels > (GetAudioChannelCount(Options.MaxChannels) ?? int.MaxValue))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                    continue;
                }

                // Check sample rate:
                if ((Options.MaxSampleRate != AudioSampleRate.Preserve && audioStream.SampleRate <= 0) ||
                    audioStream.SampleRate > (GetAudioSampleRate(Options.MaxSampleRate) ?? int.MaxValue))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                    continue;
                }
            }
        }

        if (Options.VideoSourceValidation.MaxStreams.HasValue && numVideoStreams > Options.VideoSourceValidation.MaxStreams.Value)
            throw new FileProcessingException("The number of video streams exceeds the maximum allowed.");

        if (Options.VideoSourceValidation.MinStreams.HasValue && numVideoStreams < Options.VideoSourceValidation.MinStreams.Value)
            throw new FileProcessingException("The number of video streams is less than the minimum required.");

        if (Options.AudioSourceValidation.MaxStreams.HasValue && numAudioStreams > Options.AudioSourceValidation.MaxStreams.Value)
            throw new FileProcessingException("The number of audio streams exceeds the maximum allowed.");

        if (Options.AudioSourceValidation.MinStreams.HasValue && numAudioStreams < Options.AudioSourceValidation.MinStreams.Value)
            throw new FileProcessingException("The number of audio streams is less than the minimum required.");

        if (numAudioStreams == 0 && numVideoStreams == 0)
            throw new FileProcessingException("The source video contains no audio or video streams.");

        if (anyInvalidCodecs)
            throw new FileProcessingException("One or more streams use a codec that is not supported by this processor.");

        // Validate input by doing a decode-only ffmpeg run to ensure no decoding errors.
        // Note: when active, we set aside the first 20% of progress for this.
        // Note: we compare duration to 0.0, as that's the value meaning "unknown".
        double maxDuration = ((IEnumerable<double>)[
            0.0,
            sourceInfo.Duration ?? 0.0,
            .. sourceInfo.Streams
                .OfType<FFprobeUtils.VideoStreamInfo>()
                .Where((x) => !x.IsAttachedPic && !x.IsTimedThumbnail)
                .Select((x) => x.Duration ?? 0.0),
            .. sourceInfo.Streams
                .OfType<FFprobeUtils.AudioStreamInfo>()
                .Select((x) => x.Duration ?? 0.0),
        ]).Max();
        var progressTempFile = (Options.ProgressCallback != null && Options.ForceValidateAllStreams && maxDuration != 0.0)
            ? context.GetNewWorkFile(".txt")
            : null;
        double progressUsed = 0.0;
        double lastDone = 0.0;
        if (Options.ForceValidateAllStreams)
        {
            // Run the validation pass:
            const double ValidateProgressFraction = 0.20;
            Action<double> validateProgressCallback = progressTempFile is not null ? (durationDone) =>
            {
                // Avoid going backwards or repeating the same progress:
                if (durationDone <= lastDone || durationDone < 0.0 || durationDone > maxDuration) return;

                // Clamp / adjust to [0.0, 0.20] range:
                double clampedProgress = double.Clamp(durationDone / maxDuration * ValidateProgressFraction, 0.0, ValidateProgressFraction);
                lastDone = durationDone;
                Options.ProgressCallback!((context.FileId, context.VariantId), progressUsed + clampedProgress);
            } : null;
            await FFmpegUtils.RunRawFFmpegCommandAsync(
                ["-i", sourceFileWithCorrectExtension.PathExport, "-ignore_unknown", "-xerror", "-hide_banner", "-f", "null", "-"],
                validateProgressCallback,
                progressTempFile,
                ensureAllProgressRead: true,
                cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
            progressUsed += ValidateProgressFraction;

            // Check if duration is longer than max video or audio duration (if specified):
            double? maxVideoLength = Options.VideoSourceValidation.MaxLength?.TotalSeconds;
            double? maxAudioLength = Options.AudioSourceValidation.MaxLength?.TotalSeconds;
            if ((maxVideoLength.HasValue && lastDone > maxVideoLength.Value && numVideoStreams > 0) ||
                (maxAudioLength.HasValue && lastDone > maxAudioLength.Value && numAudioStreams > 0))
            {
                throw new FileProcessingException("Measured video duration exceeds maximum allowed length.");
            }

            // Update maxDuration with the measured duration:
            maxDuration = lastDone;
        }

        // Determine if we need to make any changes to the file at all - this involves checking: resizing, re-encoding, removing metadata, etc. - note: some
        // are already checked above with anyWouldReencodeForReencodeOptionalPurposes.
        // Also check if we need to remux ignoring "if smaller" (and similar potentially unnecessary) re-encodings.
        bool remuxRequired =
            anyWouldReencodeForReencodeOptionalPurposes ||
            !Options.ResultFormats.Contains(sourceFormat) ||
            Options.MetadataStrippingMode == VideoMetadataStrippingMode.Required ||
            Options.ForceProgressiveDownload;
        bool remuxGuaranteedRequired = remuxRequired;
        bool guaranteedFullyCompatibleWithMP4Container = true;
        if (!remuxRequired)
        {
            foreach (var stream in sourceInfo.Streams)
            {
                if (stream is FFprobeUtils.VideoStreamInfo videoStream)
                {
                    bool isThumbnail = videoStream.IsAttachedPic || videoStream.IsTimedThumbnail;
                    if (Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.None or VideoMetadataStrippingMode.Preferred) && isThumbnail)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                    }
                    else if (isThumbnail && videoStream.CodecName is not ("mjpeg" or "png")) // Common thumbnail codecs which do happen to be mp4 compatible.
                    {
                        guaranteedFullyCompatibleWithMP4Container = false;
                    }

                    if (isThumbnail)
                    {
                        continue;
                    }

                    VideoCodec? codec = MatchVideoCodecByName(Options.ResultVideoCodecs, videoStream.CodecName);

                    if (codec is null)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                    }

                    if (codec?.SupportsMP4Muxing != true)
                    {
                        guaranteedFullyCompatibleWithMP4Container = false;
                    }

                    if (Options.VideoReencodeMode != StreamReencodeMode.AvoidReencoding)
                    {
                        remuxRequired = true;
                    }
                }
                else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
                {
                    if (Options.RemoveAudioStreams)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        continue;
                    }

                    AudioCodec? codec = MatchAudioCodecByName(Options.ResultAudioCodecs, audioStream.CodecName, audioStream.ProfileName);

                    if (codec is null)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                    }

                    if (codec?.SupportsMP4Muxing != true)
                    {
                        guaranteedFullyCompatibleWithMP4Container = false;
                    }

                    if (Options.AudioReencodeMode != StreamReencodeMode.AvoidReencoding)
                    {
                        remuxRequired = true;
                    }
                }
                else if (stream is FFprobeUtils.SubtitleStreamInfo subtitleStream)
                {
                    if (!Options.TryPreserveUnrecognizedStreams)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                    }
                    else if (subtitleStream.CodecName != "mov_text") // The only subtitle codec compatible with mp4 container.
                    {
                        guaranteedFullyCompatibleWithMP4Container = false;
                    }
                }
                else if (stream is FFprobeUtils.UnrecognizedStreamInfo unrecognizedStream)
                {
                    bool isThumbnail = unrecognizedStream.IsAttachedPic || unrecognizedStream.IsTimedThumbnail;
                    if (isThumbnail
                        ? (Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.None or VideoMetadataStrippingMode.Preferred))
                        : !Options.TryPreserveUnrecognizedStreams)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                    }
                    else
                    {
                        guaranteedFullyCompatibleWithMP4Container = false;
                    }
                }
            }
        }

        // If remuxing is not required, we can just return the original file:
        if (!remuxRequired) return FileProcessingResult.File(sourceFileWithCorrectExtension, hasChanges: hasChangedExtension);

        // We want to figure out which streams cannot be copied to the output container directly first - including unrecognised streams.
        // We do this by attempting to copy the streams to the output container one at a time and check for errors - any streams that cause errors, we mark as
        // requiring re-encoding or being uncopyable.
        // We reserve 10% of the processing percent for this process.
        // Note: we always run this step, since while ffmpeg reads mov and mp4 the same, mp4 does not support every single codec that mov does, unless we
        // detected earlier that every stream we saw is compatible with the mp4 container.
        bool[] isCompatibleStream = new bool[sourceInfo.Streams.Length];
        if (guaranteedFullyCompatibleWithMP4Container)
        {
            isCompatibleStream.AsSpan().Fill(true);
        }
        else
        {
            const double StreamCompatibilityCheckProgressFraction = 0.10;
            var tempMP4File = context.GetNewWorkFile(".mp4");
            for (int i = 0; i < sourceInfo.Streams.Length; i++)
            {
                var stream = sourceInfo.Streams[i];

                // Check if we already know this stream is compatible - if it is, we can skip the test and jump to reporting progress:
                if (stream is FFprobeUtils.VideoStreamInfo videoStream)
                {
                    if (videoStream.CodecName is "mjpeg" or "png" ||
                        MatchVideoCodecByName(Options.ResultVideoCodecs, videoStream.CodecName) is { SupportsMP4Muxing: true })
                    {
                        isCompatibleStream[i] = true;
                        goto reportProgress;
                    }
                }
                else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
                {
                    if (MatchAudioCodecByName(Options.ResultAudioCodecs, audioStream.CodecName, audioStream.ProfileName) is { SupportsMP4Muxing: true } ||
                        Options.RemoveAudioStreams)
                    {
                        isCompatibleStream[i] = true;
                        goto reportProgress;
                    }
                }
                else if (stream is FFprobeUtils.SubtitleStreamInfo subtitleStream)
                {
                    if (subtitleStream.CodecName == "mov_text" || !Options.TryPreserveUnrecognizedStreams)
                    {
                        isCompatibleStream[i] = true;
                        goto reportProgress;
                    }
                }
                else if (stream is FFprobeUtils.UnrecognizedStreamInfo)
                {
                    if (!Options.TryPreserveUnrecognizedStreams)
                    {
                        isCompatibleStream[i] = true;
                        goto reportProgress;
                    }
                }

                // Set up command to test copying this stream:
                IEnumerable<string> testCommand =
                [
                    "-i", sourceFileWithCorrectExtension.PathExport,
                    "-map", string.Create(CultureInfo.InvariantCulture, $"0:{i}"),
                    "-c", "copy",
                    "-ignore_unknown", "-xerror", "-hide_banner", "-y",
                    "-f", "mp4",
                    tempMP4File.PathExport,
                ];

                // Run the test command:
                var (_, _, returnCode) = await ProcessUtils.RunProcessToStringAsync(
                    FFmpegExePath,
                    testCommand,
                    cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);
                isCompatibleStream[i] = returnCode == 0;

                // Report progress:
                reportProgress:
                Options.ProgressCallback?.Invoke(
                    (context.FileId, context.VariantId),
                    progressUsed + (StreamCompatibilityCheckProgressFraction * ((double)(i + 1) / (sourceInfo.Streams.Length + 2))));
            }

            tempMP4File.Delete();
            progressUsed += StreamCompatibilityCheckProgressFraction;
        }

        // Set up the main command:

        bool preserveUnrecognizedStreams = Options.TryPreserveUnrecognizedStreams;
        List<FFmpegUtils.PerInputStreamOverride> perInputStreamOverrides = [];
        List<FFmpegUtils.PerOutputStreamOverride> perOutputStreamOverrides = [];
        int outputVideoStreamIndex = 0;
        int outputAudioStreamIndex = 0;
        int outputStreamIndex = 0;
        int inputVideoStreamIndex = -1;
        int inputAudioStreamIndex = -1;
        int inputStreamIndex = -1;

        perInputStreamOverrides.Add(
            new FFmpegUtils.PerStreamMapOverride(
                fileIndex: 0,
                streamKind: '\0',
                streamIndexWithinKind: -1,
                mapToOutput: preserveUnrecognizedStreams));

        perInputStreamOverrides.Add(
            new FFmpegUtils.PerStreamMapMetadataOverride(
                fileIndex: Options.MetadataStrippingMode is VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred ? -1 : 0,
                streamKind: 'g',
                streamIndexWithinKind: -1,
                outputIndex: -1));

        List<(int InputIndex, int OutputIndex, string SourceValidFileExtension, bool RequiresReencodeForMP4, char Kind)> streamsToCheckSize = [];
        List<(char Kind, int InputIndex, int OutputIndex, bool MapMetadata, FFmpegUtils.PerStreamMetadataOverride? MetadataOverrides)> streamMapping = [];
        foreach (var stream in sourceInfo.Streams)
        {
            inputStreamIndex++;
            if (stream is FFprobeUtils.VideoStreamInfo videoStream)
            {
                inputVideoStreamIndex++;
                int id = outputVideoStreamIndex;

                // Handle thumbnail streams:
                bool isThumbnail = videoStream.IsAttachedPic || videoStream.IsTimedThumbnail;
                if (isThumbnail)
                {
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: 'v',
                            streamIndexWithinKind: inputVideoStreamIndex,
                            mapToOutput: Options.MetadataStrippingMode == VideoMetadataStrippingMode.None));

                    if (Options.MetadataStrippingMode == VideoMetadataStrippingMode.None)
                    {
                        outputVideoStreamIndex++;
                        outputStreamIndex++;
                        streamMapping.Add((Kind: 'v', InputIndex: inputVideoStreamIndex, OutputIndex: id, MapMetadata: true, MetadataOverrides: null));

                        perInputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamMapMetadataOverride(
                                fileIndex: 0,
                                streamKind: 'v',
                                streamIndexWithinKind: inputVideoStreamIndex,
                                outputIndex: id));
                    }

                    continue;
                }

                // Map the stream:
                outputVideoStreamIndex++;
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);
                streamMapping.Add((Kind: 'v', InputIndex: inputVideoStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: null));
                if (!preserveUnrecognizedStreams)
                {
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(fileIndex: 0, streamKind: 'v', streamIndexWithinKind: inputVideoStreamIndex, mapToOutput: true));
                }

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: 'v',
                        streamIndexWithinKind: inputVideoStreamIndex,
                        outputIndex: id));

                // Check for resizing:
                VideoCodec? videoCodec = MatchVideoCodecByName(Options.ResultVideoCodecs, videoStream.CodecName);
                bool isRequiredReencode = videoCodec is null;
                bool reencode =
                    isRequiredReencode ||
                    videoCodec is { SupportsMP4Muxing: false } ||
                    Options.VideoReencodeMode != StreamReencodeMode.AvoidReencoding ||
                    !isCompatibleStream[inputStreamIndex];
                FFmpegUtils.PerStreamFilterOverride? filterOverride = null;
                if (Options.ResizeOptions is { } resizeOptions &&
                    ((videoStream.Width > resizeOptions.Width) || (videoStream.Height > resizeOptions.Height)))
                {
                    // Note: we already checked for invalid dimensions earlier, so no need to worry about 0 or -1.
                    reencode = true;
                    isRequiredReencode = true;
                    filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                    perOutputStreamOverrides.Add(filterOverride);
                    double potentialWidth1 = resizeOptions.Width;
                    double potentialHeight1 = (double)videoStream.Height / videoStream.Width * potentialWidth1;
                    double potentialHeight2 = resizeOptions.Height;
                    double potentialWidth2 = (double)videoStream.Width / videoStream.Height * potentialHeight2;
                    var (newWidth, newHeight) = potentialWidth1 < potentialWidth2
                        ? ((int)Math.Round(potentialWidth1), (int)Math.Round(potentialHeight1))
                        : ((int)Math.Round(potentialWidth2), (int)Math.Round(potentialHeight2));
                    filterOverride.Scale = (newWidth, newHeight);
                }

                // Check for fps:
                if (Options.FpsOptions is { } fpsOptions)
                {
                    int maxFpsNum = fpsOptions.TargetFps;
                    int maxFpsDen = 1;

                    if (videoStream.FpsNum <= 0 || videoStream.FpsDen <= 0 || (long)videoStream.FpsNum * maxFpsDen > (long)videoStream.FpsDen * maxFpsNum)
                    {
                        reencode = true;
                        isRequiredReencode = true;
                        if (filterOverride is null)
                        {
                            filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                            perOutputStreamOverrides.Add(filterOverride);
                        }

                        long newFpsNum, newFpsDen;
                        if (fpsOptions.Mode == VideoFpsMode.LimitToExact || videoStream.FpsNum <= 0 || videoStream.FpsDen <= 0)
                        {
                            newFpsNum = maxFpsNum;
                            newFpsDen = maxFpsDen;
                        }
                        else
                        {
                            Debug.Assert(
                                fpsOptions.Mode == VideoFpsMode.LimitByIntegerDivision,
                                "Unimplemented FpsOptions.LimitMode value.");

                            // Find division factor: ceil(currentFps / maxFps)
                            long lhs = (long)videoStream.FpsNum * maxFpsDen;
                            long rhs = (long)videoStream.FpsDen * maxFpsNum;
                            long divideBy = (lhs + rhs - 1) / rhs;

                            // Apply division
                            long gcd = (long)BigInteger.GreatestCommonDivisor(videoStream.FpsNum, divideBy);
                            if (gcd != 1)
                            {
                                newFpsNum = videoStream.FpsNum / gcd;
                                divideBy /= gcd;
                            }
                            else
                            {
                                newFpsNum = videoStream.FpsNum;
                            }

                            newFpsDen = videoStream.FpsDen * divideBy;
                        }

                        filterOverride.FPS = (newFpsNum, newFpsDen);
                        perOutputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamFPSOverride(streamKind: 'v', streamIndexWithinKind: id, fpsNum: newFpsNum, fpsDen: newFpsDen));
                    }
                }

                // Check if hdr (which will require re-encode if RemapHDRToSDR is set):
                bool isHdr = !IsKnownSDRColorProfile(videoStream.ColorTransfer, videoStream.ColorPrimaries, videoStream.ColorSpace);

                // Check if we have to select a pixel format:
                // Note: if we're re-encoding, we don't try to preserve the original pixel format.
                int finalBitsPerChannel = -1, finalChromaSubsampling = -1;
                var pixFormatInfo = GetPixelFormatCharacteristics(videoStream.PixelFormat);
                int bitsPerSample = pixFormatInfo?.BitsPerSample ?? videoStream.BitsPerSample;
                if (bitsPerSample > (GetBitsPerChannel(Options.MaximumBitsPerChannel) ?? int.MaxValue) ||
                    (pixFormatInfo is not null && bitsPerSample != pixFormatInfo.Value.BitsPerSample))
                {
                    reencode = true;
                    isRequiredReencode = true;
                }
                else if (Options.MaximumChromaSubsampling != ChromaSubsampling.Preserve && (
                    pixFormatInfo is null ||
                    !pixFormatInfo.Value.IsStandard ||
                    !pixFormatInfo.Value.IsYUV ||
                    pixFormatInfo.Value.ChromaSubsampling > GetChromaSubsampling(Options.MaximumChromaSubsampling)))
                {
                    reencode = true;
                    isRequiredReencode = true;
                }

                // If we're re-encoding, we want to specify the pixel format always:
                // Note: we also check for what HDR remapping causing re-encoding would give here, since it also requires us to always specify something.
                string pixFormat = string.Empty;
                if (reencode || (isHdr && Options.RemapHDRToSDR))
                {
                    int videoSubsampling = pixFormatInfo switch
                    {
                        { ChromaSubsampling: 400 } => 420,
                        { ChromaSubsampling: 440 } => 444,
                        { ChromaSubsampling: var x } => x,
                        _ => 444,
                    };
                    int maxSubsampling = GetChromaSubsampling(Options.MaximumChromaSubsampling) ?? 444;
                    int videoBitsPerSample = bitsPerSample >= 0 ? bitsPerSample : 12;
                    int maxBitsPerSample = GetBitsPerChannel(Options.MaximumBitsPerChannel) ?? 12;
                    if (Options.ResultVideoCodecs[0] != VideoCodec.HEVC) maxBitsPerSample = int.Min(maxBitsPerSample, 10);
                    finalBitsPerChannel = int.Min(videoBitsPerSample, maxBitsPerSample);
                    finalChromaSubsampling = int.Min(videoSubsampling, maxSubsampling);
                    pixFormat = (finalChromaSubsampling, finalBitsPerChannel) switch
                    {
                        (420, 8) => "yuv420p",
                        (420, 10) => "yuv420p10le",
                        (420, 12) => "yuv420p12le",
                        (422, 8) => "yuv422p",
                        (422, 10) => "yuv422p10le",
                        (422, 12) => "yuv422p12le",
                        (444, 8) => "yuv444p",
                        (444, 10) => "yuv444p10le",
                        (444, 12) => "yuv444p12le",
                        _ => throw new UnreachableException("Unimplemented pixel format for video encoding."),
                    };

                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamPixelFormatOverride(streamKind: 'v', streamIndexWithinKind: id, pixelFormat: pixFormat));
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorRangeOverride(streamKind: 'v', streamIndexWithinKind: id, colorRange: "pc"));

                    if (filterOverride is null)
                    {
                        filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                        perOutputStreamOverrides.Add(filterOverride);
                    }

                    filterOverride.NewVideoRange = "pc";
                }

                // Deal with HDR:
                if (isHdr && (Options.RemapHDRToSDR || reencode))
                {
                    reencode = true;
                    isRequiredReencode = true;

                    if (filterOverride is null)
                    {
                        filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                        perOutputStreamOverrides.Add(filterOverride);
                    }

                    filterOverride.HDRToSDR = true;
                    filterOverride.SDRPixelFormat = pixFormat;

                    // Just use BT.709 for everything as our standardized SDR profile:
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorTransferOverride(streamKind: 'v', streamIndexWithinKind: id, colorTransfer: "bt709"));
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorPrimariesOverride(streamKind: 'v', streamIndexWithinKind: id, colorPrimaries: "bt709"));
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorSpaceOverride(streamKind: 'v', streamIndexWithinKind: id, colorSpace: "bt709"));
                }

                // Keep track if we want to check the size later:
                if (!isRequiredReencode && Options.VideoReencodeMode == StreamReencodeMode.SelectSmallest)
                {
                    var codec = MatchVideoCodecByName(Options.ResultVideoCodecs, videoStream.CodecName)!;
                    streamsToCheckSize.Add((
                        InputIndex: inputVideoStreamIndex,
                        OutputIndex: id,
                        SourceValidFileExtension: codec.WritableFileExtension,
                        RequiresReencodeForMP4: !isCompatibleStream[inputStreamIndex] || !codec.SupportsMP4Muxing,
                        Kind: 'v'));
                }

                // Set up codec to use
                if (!reencode)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: id, codec: "copy"));
                }
                else if (Options.ResultVideoCodecs[0] == VideoCodec.H264 && FFprobeUtils.Configuration.SupportsLibX264Encoder)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: id, codec: "libx264"));

                    int crf = GetH264CRF(Options.VideoQuality);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCRFOverride(streamKind: 'v', streamIndexWithinKind: id, crf: crf));

                    string preset = GetVideoCompressionPreset(Options.VideoCompressionLevel);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamPresetOverride(streamKind: 'v', streamIndexWithinKind: id, preset: preset));

                    string profile = (finalBitsPerChannel, finalChromaSubsampling) switch
                    {
                        (8, 420) => "high",
                        (8, 422) => "high422",
                        (8, 444) => "high444",
                        (10, 420) => "high10",
                        (10, 422) => "high422",
                        (10, 444) => "high444",
                        _ => throw new UnreachableException("Unimplemented H.264 profile for video encoding."),
                    };
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'v', streamIndexWithinKind: id, profile: profile));
                }
                else if (Options.ResultVideoCodecs[0] == VideoCodec.HEVC && FFprobeUtils.Configuration.SupportsLibX265Encoder)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: id, codec: "libx265"));

                    int crf = GetH265CRF(Options.VideoQuality);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCRFOverride(streamKind: 'v', streamIndexWithinKind: id, crf: crf));

                    string preset = GetVideoCompressionPreset(Options.VideoCompressionLevel);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamPresetOverride(streamKind: 'v', streamIndexWithinKind: id, preset: preset));

                    string profile = (finalBitsPerChannel, finalChromaSubsampling) switch
                    {
                        (8, 420) => "main",
                        (8, 422 or 444) => "rext",
                        (10, 420) => "main10",
                        (10, 422 or 444) => "rext",
                        (12, 420 or 422 or 444) => "rext",
                        _ => throw new UnreachableException("Unimplemented HEVC profile for video encoding."),
                    };
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'v', streamIndexWithinKind: id, profile: profile));
                }
                else
                {
                    Debug.Fail("The requested video codec did not have a supported encoder available in the configured FFmpeg build (unexpected).");
                }
            }
            else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
            {
                // If we're removing audio streams, exclude it now:
                inputAudioStreamIndex++;
                if (Options.RemoveAudioStreams)
                {
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: 'a',
                            streamIndexWithinKind: inputAudioStreamIndex,
                            mapToOutput: false));
                }

                // Map the stream:
                int id = outputAudioStreamIndex;
                outputAudioStreamIndex++;
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);
                streamMapping.Add((Kind: 'a', InputIndex: inputAudioStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: null));
                if (!preserveUnrecognizedStreams)
                {
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(fileIndex: 0, streamKind: 'a', streamIndexWithinKind: inputAudioStreamIndex, mapToOutput: true));
                }

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: 'a',
                        streamIndexWithinKind: inputAudioStreamIndex,
                        outputIndex: id));

                // Determine channel adjustment:
                AudioCodec? audioCodec = MatchAudioCodecByName(Options.ResultAudioCodecs, audioStream.CodecName, audioStream.ProfileName);
                bool isRequiredReencode = audioCodec is null;
                bool reencode =
                    isRequiredReencode ||
                    audioCodec is { SupportsMP4Muxing: false } ||
                    Options.AudioReencodeMode != StreamReencodeMode.AvoidReencoding ||
                    !isCompatibleStream[inputStreamIndex];
                int? targetChannels = GetAudioChannelCount(Options.MaxChannels);
                int numChannels = audioStream.Channels;
                if (targetChannels.HasValue && audioStream.Channels > targetChannels.Value)
                {
                    reencode = true;
                    isRequiredReencode = true;
                    numChannels = targetChannels.Value;

                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamChannelsOverride(streamKind: 'a', streamIndexWithinKind: id, channels: targetChannels.Value));
                }

                // Determine sample rate adjustment:
                int? targetSampleRate = GetAudioSampleRate(Options.MaxSampleRate);
                if (targetSampleRate.HasValue && audioStream.SampleRate > targetSampleRate.Value)
                {
                    reencode = true;
                    isRequiredReencode = true;
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamSampleRateOverride(streamKind: 'a', streamIndexWithinKind: id, sampleRate: targetSampleRate.Value));
                }

                // Keep track if we want to check the size later:
                if (!isRequiredReencode && Options.AudioReencodeMode == StreamReencodeMode.SelectSmallest)
                {
                    var codec = MatchAudioCodecByName(Options.ResultAudioCodecs, audioStream.CodecName, audioStream.ProfileName)!;
                    streamsToCheckSize.Add((
                        InputIndex: inputAudioStreamIndex,
                        OutputIndex: id,
                        SourceValidFileExtension: codec.WritableFileExtension,
                        RequiresReencodeForMP4: !isCompatibleStream[inputStreamIndex] || !codec.SupportsMP4Muxing,
                        Kind: 'a'));
                }

                // Set up codec to use (note - currently the only supported codec is AAC-LC, so we aren't checking which one the user selected here currently):
                if (!reencode)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "copy"));
                }
                else if (FFprobeUtils.Configuration.SupportsLibFDKAACEncoder)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "libfdk_aac"));

                    int quality = GetLibFDKAACEncoderQuality(Options.AudioQuality);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'a', streamIndexWithinKind: id, profile: "lc"));
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamVBROverride(streamKind: 'a', streamIndexWithinKind: id, vbr: quality));

                    // For best & high quality, ensure we use the maximum supported cutoff frequency (20kHz):
                    if (quality >= 4)
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCutoffOverride(streamKind: 'a', streamIndexWithinKind: id, cutoff: 20000));
                }
                else if (FFprobeUtils.Configuration.SupportsAACEncoder)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "aac"));

                    int bitrate = (int)long.Min((long)numChannels * GetNativeAACEncoderBitratePerChannel(Options.AudioQuality), int.MaxValue);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamBitrateOverride(streamKind: 'a', streamIndexWithinKind: id, bitrate: bitrate));
                }
                else
                {
                    Debug.Fail("The requested audio codec did not have a supported encoder available in the configured FFmpeg build (unexpected).");
                }
            }
            else if (stream is FFprobeUtils.SubtitleStreamInfo subtitleStream)
            {
                // If we're not preserving unrecognized streams, skip it (we already marked as excluded by default):
                if (!preserveUnrecognizedStreams)
                {
                    continue;
                }

                // Map the stream:
                int id = outputStreamIndex;
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: '\0',
                        streamIndexWithinKind: inputStreamIndex,
                        outputIndex: id));

                // Specify our codec:
                perOutputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamCodecOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: id,
                        codec: isCompatibleStream[inputStreamIndex] ? "copy" : "mov_text"));

                // If we're not copying metadata, we want to manually add the metadata we want to preserve back in after normalizing it:
                FFmpegUtils.PerStreamMetadataOverride? metadataOverrides = null;
                if (!mapMetadata)
                {
                    metadataOverrides = new FFmpegUtils.PerStreamMetadataOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: id)
                    {
                        TitleOrHandlerName = NormalizeTitleOrHandlerName(subtitleStream.TitleOrHandlerName),
                        Language = IsValidLanguage(subtitleStream.Language) ? subtitleStream.Language : null,
                    };
                    perOutputStreamOverrides.Add(metadataOverrides);
                }

                // Final part of mapping the stream:
                streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: metadataOverrides));
            }
            else if (stream is FFprobeUtils.UnrecognizedStreamInfo unrecognizedStream)
            {
                // Handle thumbnail streams:
                bool isThumbnail = unrecognizedStream.IsAttachedPic || unrecognizedStream.IsTimedThumbnail;
                int id = outputStreamIndex;
                if (isThumbnail)
                {
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: '\0',
                            streamIndexWithinKind: inputStreamIndex,
                            mapToOutput: Options.MetadataStrippingMode == VideoMetadataStrippingMode.None && isCompatibleStream[inputStreamIndex]));

                    if (Options.MetadataStrippingMode == VideoMetadataStrippingMode.None && isCompatibleStream[inputStreamIndex])
                    {
                        outputStreamIndex++;
                        streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: true, MetadataOverrides: null));

                        perInputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamMapMetadataOverride(
                                fileIndex: 0,
                                streamKind: '\0',
                                streamIndexWithinKind: inputStreamIndex,
                                outputIndex: id));

                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(
                            streamKind: '\0',
                            streamIndexWithinKind: inputStreamIndex,
                            codec: "copy"));
                    }

                    continue;
                }

                // If we're not preserving unrecognized streams, skip it (we already marked as excluded by default):
                if (!preserveUnrecognizedStreams)
                {
                    continue;
                }

                // If the unrecognized stream is not compatible with the MP4 container, we cannot preserve it:
                if (!isCompatibleStream[inputStreamIndex])
                {
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: '\0',
                            streamIndexWithinKind: inputStreamIndex,
                            mapToOutput: false));

                    continue;
                }

                // Map the stream:
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);
                streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: null));
                perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: '\0', streamIndexWithinKind: inputStreamIndex, codec: "copy"));

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: '\0',
                        streamIndexWithinKind: inputStreamIndex,
                        outputIndex: id));
            }
        }

        var resultTempFile = context.GetNewWorkFile(".mp4"); // currently this is the only supported output format when remuxing, so just use it
        FFmpegUtils.FFmpegCommand command = new(
            inputFiles: [sourceFileWithCorrectExtension],
            outputFile: resultTempFile,
            perInputStreamOverrides: [.. perInputStreamOverrides],
            perOutputStreamOverrides: [.. perOutputStreamOverrides],
            mapChaptersFrom: Options.MetadataStrippingMode is VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred ? -1 : 0,
            forceProgressiveDownloadSupport: Options.ForceProgressiveDownload);

        // Run the command
        // Note: The last 5% of progress is reserved for the "checking if smaller" pass & since the progress reported is the highest timestamp completed of any
        // stream, so we want to leave some headroom.
        if (Options.ProgressCallback != null && maxDuration != 0.0) progressTempFile ??= context.GetNewWorkFile(".txt");
        lastDone = 0.0;
        const double ReservedProgress = 0.05;
        Action<double>? localProgressCallback = progressTempFile != null ? (durationDone) =>
        {
            // Avoid going backwards or repeating the same progress:
            if (durationDone <= lastDone || durationDone < 0.0 || durationDone > maxDuration) return;

            // Clamp / adjust to [progressUsed, 0.95] range:
            double clampedProgress = double.Clamp(progressUsed + (durationDone / maxDuration * (1.0 - ReservedProgress - progressUsed)), progressUsed, 1.0 - ReservedProgress);
            lastDone = durationDone;
            Options.ProgressCallback!((context.FileId, context.VariantId), clampedProgress);
        } : null;
        await FFmpegUtils.RunFFmpegCommandAsync(command, localProgressCallback, progressTempFile, context.CancellationToken).ConfigureAwait(false);

        // For any streams that are in a "if smaller than" mode, we want to do additional passes to see if it ended up smaller:
        if (streamsToCheckSize.Count > 0)
        {
            // Determine which are actually smaller & which are actually smaller, but would still need to be re-encoded if remuxed:
            List<(int Index, bool NeedsReencode)> wasSmaller = [];
            var reencodedTempFile = context.GetNewWorkFile(".mp4");
            var fromOriginalTempFileMp4 = context.GetNewWorkFile(".mp4");
            for (int i = 0; i < streamsToCheckSize.Count; i++)
            {
                var streamToCheck = streamsToCheckSize[i];

                // We approximate the size of the stream by using the copy codec for the 1 stream on the original file vs output file, and seeing how big that
                // output is:
                FFmpegUtils.FFmpegCommand extractCommandReencoded = new(
                    inputFiles: [resultTempFile],
                    outputFile: reencodedTempFile,
                    perInputStreamOverrides:
                    [
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: '\0',
                            streamIndexWithinKind: -1,
                            mapToOutput: false),
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: streamToCheck.Kind,
                            streamIndexWithinKind: streamToCheck.OutputIndex,
                            mapToOutput: true),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: 'g',
                            streamIndexWithinKind: -1,
                            outputIndex: -1),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: streamToCheck.Kind,
                            streamIndexWithinKind: streamToCheck.InputIndex,
                            outputIndex: 0),
                    ],
                    perOutputStreamOverrides:
                    [
                        new FFmpegUtils.PerStreamCodecOverride(streamKind: streamToCheck.Kind, streamIndexWithinKind: 0, codec: "copy")
                    ],
                    mapChaptersFrom: -1,
                    forceProgressiveDownloadSupport: false);
                await FFmpegUtils.RunFFmpegCommandAsync(extractCommandReencoded, null, null, context.CancellationToken).ConfigureAwait(false);
                long reencodedApproxSize = reencodedTempFile.Length;
                reencodedTempFile.Delete();

                // Now do the same for the original file:
                var fromOriginalTempFile = streamToCheck.SourceValidFileExtension != ".mp4"
                    ? context.GetNewWorkFile(streamToCheck.SourceValidFileExtension)
                    : fromOriginalTempFileMp4;
                FFmpegUtils.FFmpegCommand extractCommandOriginal = new(
                    inputFiles: [sourceFileWithCorrectExtension],
                    outputFile: fromOriginalTempFile,
                    perInputStreamOverrides:
                    [
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: '\0',
                            streamIndexWithinKind: -1,
                            mapToOutput: false),
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: streamToCheck.Kind,
                            streamIndexWithinKind: streamToCheck.InputIndex,
                            mapToOutput: true),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: 'g',
                            streamIndexWithinKind: -1,
                            outputIndex: -1),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: streamToCheck.Kind,
                            streamIndexWithinKind: streamToCheck.InputIndex,
                            outputIndex: 0),
                    ],
                    perOutputStreamOverrides:
                    [
                        new FFmpegUtils.PerStreamCodecOverride(streamKind: streamToCheck.Kind, streamIndexWithinKind: 0, codec: "copy")
                    ],
                    mapChaptersFrom: -1,
                    forceProgressiveDownloadSupport: false);
                await FFmpegUtils.RunFFmpegCommandAsync(extractCommandOriginal, null, null, context.CancellationToken).ConfigureAwait(false);
                long originalApproxSize = fromOriginalTempFile.Length;
                fromOriginalTempFile.Delete();

                // Add to applicable lists:
                if (originalApproxSize < reencodedApproxSize)
                {
                    wasSmaller.Add((i, streamToCheck.RequiresReencodeForMP4));
                }

                // We use half of our headroom for this first pass, so update progress:
                if (localProgressCallback != null)
                {
                    double progressPortion = ReservedProgress * 0.5 / (streamsToCheckSize.Count + 2);
                    double progressValue = 1.0 - ReservedProgress + (progressPortion * (i + 1));
                    Options.ProgressCallback!((context.FileId, context.VariantId), progressValue);
                }
            }

            // Determine if all were smaller & if there's no other reason to stop us just using the original file:
            if (wasSmaller.Count == streamsToCheckSize.Count && !remuxGuaranteedRequired)
            {
                // All streams were smaller, and we do not otherwise require remuxing, so just return the original file:
                resultTempFile.Delete();
                return FileProcessingResult.File(sourceFileWithCorrectExtension, hasChanges: hasChangedExtension);
            }

            // Check if any were better in the original file:
            if (wasSmaller.Count > 0)
            {
                // Get a new temp file to write the mixed result to (note: we only support .mp4 currently, so just use that):
                var newResultTempFile = context.GetNewWorkFile(".mp4");

                // Prepare the command:
                perInputStreamOverrides.Clear();
                perOutputStreamOverrides.Clear();
                int wasSmallerIdx = 0;
                var nextWasSmaller = (Value: streamsToCheckSize[wasSmaller[0].Index], wasSmaller[0].NeedsReencode);
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapOverride(
                        fileIndex: 0,
                        streamKind: '\0',
                        streamIndexWithinKind: -1,
                        mapToOutput: false));
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapOverride(
                        fileIndex: 1,
                        streamKind: '\0',
                        streamIndexWithinKind: -1,
                        mapToOutput: false));
                perOutputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamCodecOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: -1,
                        codec: "copy"));
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: Options.MetadataStrippingMode is VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred ? -1 : 0,
                        streamKind: 'g',
                        streamIndexWithinKind: -1,
                        outputIndex: -1));
                foreach (var (kind, inputIndex, outputIndex, mapMetadata, metadataOverrides) in streamMapping)
                {
                    // Determine if we are using the original or re-encoded stream:
                    bool useOriginal = false;
                    if (nextWasSmaller.Value.InputIndex == inputIndex && nextWasSmaller.Value.Kind == kind)
                    {
                        useOriginal = !nextWasSmaller.NeedsReencode;
                        wasSmallerIdx++;
                        if (wasSmallerIdx < wasSmaller.Count)
                        {
                            nextWasSmaller = (Value: streamsToCheckSize[wasSmaller[wasSmallerIdx].Index], wasSmaller[wasSmallerIdx].NeedsReencode);
                        }
                        else
                        {
                            nextWasSmaller = default;
                            nextWasSmaller.Value.InputIndex = -1;
                        }
                    }

                    // Map the stream:
                    if (useOriginal)
                    {
                        perInputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamMapOverride(fileIndex: 0, streamKind: kind, streamIndexWithinKind: inputIndex, mapToOutput: true));
                    }
                    else
                    {
                        perInputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamMapOverride(fileIndex: 1, streamKind: kind, streamIndexWithinKind: outputIndex, mapToOutput: true));
                    }

                    // Specify whether we want to map the metadata:
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: mapMetadata ? 0 : -1,
                            streamKind: kind,
                            streamIndexWithinKind: inputIndex,
                            outputIndex: outputIndex));

                    // If we have metadata overrides, apply them now:
                    if (metadataOverrides != null)
                    {
                        // Note: the index is the same here, since we're applying to the output stream index, and selecting the same set of streams;
                        // if this were not the case, we would need to re-create the metadata overrides.
                        perOutputStreamOverrides.Add(metadataOverrides);
                    }
                }

                FFmpegUtils.FFmpegCommand mixCommand = new(
                    inputFiles: [sourceFileWithCorrectExtension, resultTempFile],
                    outputFile: newResultTempFile,
                    perInputStreamOverrides: [.. perInputStreamOverrides],
                    perOutputStreamOverrides: [.. perOutputStreamOverrides],
                    mapChaptersFrom: Options.MetadataStrippingMode is VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred ? -1 : 0,
                    forceProgressiveDownloadSupport: Options.ForceProgressiveDownload);

                // Run the command
                // Note: The last 2% of remaining available progress is reserved since the progress reported is the highest timestamp completed of any stream,
                // so we want to leave some headroom.
                const double ReservedProgressInner = 0.02;
                lastDone = 0.0;
                Action<double>? localProgressCallbackInner = progressTempFile != null ? (durationDone) =>
                {
                    // Avoid going backwards or repeating the same progress:
                    if (durationDone <= lastDone || durationDone < 0.0 || durationDone > maxDuration) return;

                    // Clamp to [0.0, 0.98] range of our remaining reserved portion:
                    double clampedProgress = double.Clamp(durationDone / maxDuration * (1.0 - ReservedProgressInner), 0.0, 1.0 - ReservedProgressInner);
                    lastDone = durationDone;
                    Options.ProgressCallback!((context.FileId, context.VariantId), 1.0 - (ReservedProgress / 2.0) + (clampedProgress * (ReservedProgress / 2.0)));
                } : null;
                await FFmpegUtils.RunFFmpegCommandAsync(mixCommand, localProgressCallbackInner, progressTempFile, context.CancellationToken)
                    .ConfigureAwait(false);

                // Update the result file:
                resultTempFile.Delete();
                resultTempFile = newResultTempFile;
            }
        }

        // Return the result file:
        return FileProcessingResult.File(resultTempFile, hasChanges: true);
    }

    private static int? GetBitsPerChannel(BitsPerChannel bitsPerChannel) => bitsPerChannel switch
    {
        BitsPerChannel.Bits8 => 8,
        BitsPerChannel.Bits10 => 10,
        BitsPerChannel.Bits12 => 12,
        BitsPerChannel.Preserve => null,
        _ => throw new UnreachableException("Unrecognized BitsPerChannel value."),
    };

    private static int? GetChromaSubsampling(ChromaSubsampling chromaSubsampling) => chromaSubsampling switch
    {
        ChromaSubsampling.Subsampling420 => 420,
        ChromaSubsampling.Subsampling422 => 422,
        ChromaSubsampling.Subsampling444 => 444,
        ChromaSubsampling.Preserve => null,
        _ => throw new UnreachableException("Unrecognized ChromaSubsampling value."),
    };

    // Non-exhaustive list of common / standard / well-known SDR profiles configs:
    private static bool IsKnownSDRColorProfile(string? colorTransfer, string? colorPrimaries, string? colorSpace) =>
        (colorTransfer is null or "bt709" or "bt601" or "bt470" or "bt470bg" or "smpte170m" or "smpte240m") &&
        (colorPrimaries is null or "bt709" or "bt470m" or "bt470bg" or "smpte170m" or "smpte240m") &&
        (colorSpace is null or "bt709" or "bt470m" or "bt470bg" or "smpte170m" or "smpte240m" or "srgb" or "iec61966-2-1");

    private static int? GetAudioChannelCount(AudioChannels audioChannels) => audioChannels switch
    {
        AudioChannels.Mono => 1,
        AudioChannels.Stereo => 2,
        AudioChannels.Preserve => null,
        _ => throw new UnreachableException("Unrecognized AudioChannels value."),
    };

    private static int? GetAudioSampleRate(AudioSampleRate audioSampleRate) => audioSampleRate switch
    {
        AudioSampleRate.Hz44100 => 44100,
        AudioSampleRate.Hz48000 => 48000,
        AudioSampleRate.Hz96000 => 96000,
        AudioSampleRate.Hz192000 => 192000,
        AudioSampleRate.Preserve => null,
        _ => throw new UnreachableException("Unrecognized AudioSampleRate value."),
    };

    private static int GetH264CRF(VideoQuality quality) => quality switch
    {
        VideoQuality.Highest => 17,
        VideoQuality.High => 20,
        VideoQuality.Medium => 23,
        VideoQuality.Low => 26,
        VideoQuality.Lowest => 29,
        _ => throw new UnreachableException("Unrecognized VideoQuality value."),
    };

    private static int GetH265CRF(VideoQuality quality) => quality switch
    {
        VideoQuality.Highest => 19,
        VideoQuality.High => 23,
        VideoQuality.Medium => 28,
        VideoQuality.Low => 31,
        VideoQuality.Lowest => 34,
        _ => throw new UnreachableException("Unrecognized VideoQuality value."),
    };

    private static string GetVideoCompressionPreset(VideoCompressionLevel compressionLevel) => compressionLevel switch
    {
        VideoCompressionLevel.Highest => "slower",
        VideoCompressionLevel.High => "slow",
        VideoCompressionLevel.Medium => "medium",
        VideoCompressionLevel.Low => "faster",
        VideoCompressionLevel.Lowest => "superfast",
        _ => throw new UnreachableException("Unrecognized VideoCompressionLevel value."),
    };

    private static int GetLibFDKAACEncoderQuality(AudioQuality quality) => quality switch
    {
        AudioQuality.Highest => 5,
        AudioQuality.High => 4,
        AudioQuality.Medium => 3,
        AudioQuality.Low => 2,
        AudioQuality.Lowest => 1,
        _ => throw new UnreachableException("Unrecognized AudioQuality value."),
    };

    private static int GetNativeAACEncoderBitratePerChannel(AudioQuality quality) => quality switch
    {
        AudioQuality.Highest => 192_000,
        AudioQuality.High => 160_000,
        AudioQuality.Medium => 128_000,
        AudioQuality.Low => 80_000,
        AudioQuality.Lowest => 64_000,
        _ => throw new UnreachableException("Unrecognized AudioQuality value."),
    };

    private static VideoCodec? MatchVideoCodecByName(IEnumerable<VideoCodec> options, string? codecName) => options.FirstOrDefault((x) => x.Name == codecName);
    private static AudioCodec? MatchAudioCodecByName(IEnumerable<AudioCodec> options, string? codecName, string? profileName)
        => options.FirstOrDefault((x) => x.Name == codecName && (x.Profile == null || x.Profile == profileName));

    // Note: this list is likely not exhaustive, but should cover a lot of files.
    /*
        Chroma subsampling values possible (each samples the luma at every pixel, block sizes are w * h):
        - 400: monochrome / grayscale
        - 420: 4:2:0, 2x2 blocks
        - 422: 4:2:2, 2x1 blocks
        - 440: 4:4:0, 1x2 blocks
        - 444: 4:4:4, 1x1 blocks
    */
    private static (int BitsPerSample, bool IsYUV, bool HasAlpha, bool IsStandard, int ChromaSubsampling)? GetPixelFormatCharacteristics(string? pixelFormat)
        => pixelFormat switch
        {
            "yuv420p" => (8, true, false, true, 420),
            "yuvj420p" => (8, true, false, true, 420),
            "yuv422p" => (8, true, false, true, 422),
            "yuvj422p" => (8, true, false, true, 422),
            "yuv444p" => (8, true, false, true, 444),
            "yuvj444p" => (8, true, false, true, 444),
            "nv12" => (8, true, false, false, 420),
            "nv16" => (8, true, false, false, 422),
            "nv21" => (8, true, false, false, 420),
            "yuv420p10le" => (10, true, false, true, 420),
            "yuv422p10le" => (10, true, false, true, 422),
            "yuv444p10le" => (10, true, false, true, 444),
            "nv20le" => (10, true, false, false, 420),
            "gray" => (8, true, false, false, 400),
            "gray10le" => (10, true, false, false, 400),
            "gbrp" => (8, false, false, false, 444),
            "gbrp10le" => (10, false, false, false, 444),
            "yuv420p12le" => (12, true, false, true, 420),
            "yuv422p12le" => (12, true, false, true, 422),
            "yuv444p12le" => (12, true, false, true, 444),
            "gbrp12le" => (12, false, false, false, 444),
            "gray12le" => (12, true, false, false, 400),
            "yuva420p" => (8, true, true, false, 420),
            "yuva420p10le" => (10, true, true, false, 420),
            "bgra" => (8, false, true, false, 444),
            "ayuv" => (8, true, true, false, 444),
            "p010le" => (10, true, false, false, 420),
            "p210le" => (10, true, false, false, 422),
            "yuv440p" => (8, true, false, false, 440),
            "yuv440p10le" => (10, true, false, false, 440),
            "yuv440p12le" => (12, true, false, false, 440),
            _ => null,
        };

    private static bool IsValidLanguage(string? language)
    {
        // Currently we just check for potentially valid 3-letter lowercase code, rather than checking against the precise ISO 639-2/T list:
        if (language is not { Length: 3 }) return false;
        return !language.AsSpan().ContainsAnyExceptInRange('a', 'z');
    }

    private static readonly SearchValues<char> _invalidTitleCharsAndSurrogates = SearchValues.Create([
        .. Enumerable.Range(0, 32).Select((x) => (char)x),
        .. Enumerable.Range(0xD800, 0x0800).Select((x) => (char)x)
    ]);

    private static string? NormalizeTitleOrHandlerName(string? value)
    {
        // Remove any null or control characters, limit length to 24 characters, trim whitespace, and remove any unpaired surrogates:
        if (string.IsNullOrWhiteSpace(value)) return null;
        Span<char> buffer = stackalloc char[24];
        var sp = value.AsSpan().Trim();
        int length = 0;
        int offset = 0;
        while (offset < sp.Length && length < buffer.Length)
        {
            int nextInteresting = sp[offset..].IndexOfAny(_invalidTitleCharsAndSurrogates);
            if (nextInteresting == -1) nextInteresting = sp.Length - offset;
            if (nextInteresting + length > buffer.Length) nextInteresting = buffer.Length - length;
            sp.Slice(offset, nextInteresting).CopyTo(buffer.Slice(length, nextInteresting));
            length += nextInteresting;
            offset += nextInteresting;

            // Handle the interesting characters specially:
            if (nextInteresting == 0)
            {
                if (sp[offset] < 0x20)
                {
                    // Control character - skip it:
                    offset++;
                }
                else if (sp[offset] >= 0xD800 && sp[offset] <= 0xDFFF)
                {
                    // Check if paired:
                    if (sp[offset..] is [>= (char)0xD800 and <= (char)0xDBFF, >= (char)0xDC00 and <= (char)0xDFFF, ..])
                    {
                        // Paired surrogate - keep both if enough space:
                        if (length + 2 > buffer.Length) break;
                        sp.Slice(offset, 2).CopyTo(buffer.Slice(length, 2));
                        length += 2;
                        offset += 2;
                    }
                    else
                    {
                        // Unpaired surrogate - skip it:
                        offset++;
                    }
                }
            }
        }

        // Return the result (and re-use the original string if possible), or null if nothing valid:
        if (buffer[..length].SequenceEqual(value)) return value;
        else return length > 0 ? buffer[..length].ToString() : null;
    }
}
