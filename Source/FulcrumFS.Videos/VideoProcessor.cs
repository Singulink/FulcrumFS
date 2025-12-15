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

        if (!FFprobeUtils.Configuration.SupportsAACEncoder)
        {
            // Note: we always require the native aac encoder so that we can support > 8 channels in preserve mode.
            throw new NotSupportedException("The required audio encoder for 'aac' is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsMP4Muxing)
        {
            throw new NotSupportedException("The required media container muxer for 'mp4' is not supported by the configured ffmpeg installation.");
        }

        // Check if we have the required filters:

        if (!FFprobeUtils.Configuration.SupportsZscaleFilter)
        {
            throw new NotSupportedException("The required 'zscale' video filter is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsScaleFilter)
        {
            throw new NotSupportedException("The required 'scale' video filter is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsFpsFilter)
        {
            throw new NotSupportedException("The required 'fps' video filter is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsTonemapFilter)
        {
            throw new NotSupportedException("The required 'tonemap' video filter is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsFormatFilter)
        {
            throw new NotSupportedException("The required 'format' video filter is not supported by the configured ffmpeg installation.");
        }

        if (!FFprobeUtils.Configuration.SupportsBwdifFilter)
        {
            throw new NotSupportedException("The required 'bwdif' video filter is not supported by the configured ffmpeg installation.");
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
        bool anyInvalidCodecs = false;
        foreach (var stream in sourceInfo.Streams)
        {
            if (stream is FFprobeUtils.VideoStreamInfo videoStream)
            {
                // Skip thumbnails
                if (videoStream.IsAttachedPic || videoStream.IsTimedThumbnails)
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
                    if (videoStream.Width <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown width, cannot validate.");

                    if (videoStream.Width > Options.VideoSourceValidation.MaxWidth.Value)
                        throw new FileProcessingException($"Video stream {idx} width exceeds maximum allowed.");
                }

                if (Options.VideoSourceValidation.MaxHeight.HasValue)
                {
                    if (videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown height, cannot validate.");

                    if (videoStream.Height > Options.VideoSourceValidation.MaxHeight.Value)
                        throw new FileProcessingException($"Video stream {idx} height exceeds maximum allowed.");
                }

                if (Options.VideoSourceValidation.MaxPixels.HasValue)
                {
                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown dimensions, cannot validate.");

                    if ((long)videoStream.Width * videoStream.Height > Options.VideoSourceValidation.MaxPixels.Value)
                        throw new FileProcessingException($"Video stream {idx} exceeds maximum allowed pixel count.");
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

                // Check if we can resize if needed, which requires known dimensions:
                if (videoStream.Width <= 0 || videoStream.Height <= 0)
                {
                    throw new FileProcessingException($"Video stream {idx} has unknown dimensions, cannot determine resizing.");
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

        if (Options.RemoveAudioStreams && numVideoStreams == 0)
            throw new FileProcessingException("The source video contains no video streams.");

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
                .Where((x) => !x.IsAttachedPic && !x.IsTimedThumbnails)
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
            // Note: we cancel early if we exceed max video/audio lengths (if specified), to avoid unnecessary extra processing on an invalid (and potentially
            // malicious, since our original detected duration is just from metadata, so our actual duration could be substantially longer if malicious) input.
            // Note: while we have options to check duration, we currently have not implemented similar options for related things like ridiculous FPS, etc.
            const double ValidateProgressFraction = 0.20;
            double? maxVideoLength = Options.VideoSourceValidation.MaxLength?.TotalSeconds;
            double? maxAudioLength = Options.AudioSourceValidation.MaxLength?.TotalSeconds;
            using CancellationTokenSource exitEarlyCts = new();
            CancellationToken exitEarlyCt = exitEarlyCts.Token;
            Func<double, ValueTask> validateProgressCallback = (progressTempFile is not null || maxVideoLength is not null || maxAudioLength is not null)
                ? async (durationDone) =>
                {
                    // Avoid going backwards or repeating the same progress - but ensure we can still hit 100% of this section:
                    if (durationDone <= lastDone) return;
                    if (durationDone > maxDuration && lastDone < maxDuration) (durationDone, lastDone) = (maxDuration, durationDone);
                    else lastDone = durationDone;
                    if (maxVideoLength.HasValue && lastDone > maxVideoLength.Value && numVideoStreams > 0) exitEarlyCts.Cancel();
                    if (maxAudioLength.HasValue && lastDone > maxAudioLength.Value && numAudioStreams > 0) exitEarlyCts.Cancel();
                    if (durationDone < 0.0 || durationDone > maxDuration) return;

                    // Clamp / adjust to [0.0, 0.20] range:
                    double clampedProgress = double.Clamp(durationDone / maxDuration * ValidateProgressFraction, 0.0, ValidateProgressFraction);
                    if (Options.ProgressCallback is not null)
                    {
                        await Options.ProgressCallback((context.FileId, context.VariantId), progressUsed + clampedProgress).ConfigureAwait(false);
                    }
                }
                : null;
            if (validateProgressCallback is not null && progressTempFile is null) progressTempFile = context.GetNewWorkFile(".txt");
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, exitEarlyCt);
                await FFmpegUtils.RunRawFFmpegCommandAsync(
                    ["-i", sourceFileWithCorrectExtension.PathExport, "-ignore_unknown", "-xerror", "-hide_banner", "-f", "null", "-"],
                    validateProgressCallback,
                    progressTempFile,
                    ensureAllProgressRead: true,
                    cancellationToken: linkedCts.Token)
                .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken != context.CancellationToken)
            {
                // Keep / re-throw exception if cancellation was requested by the caller only, discard if it was our early exit.
                if (context.CancellationToken.IsCancellationRequested) throw new OperationCanceledException(message: null, ex, context.CancellationToken);
            }

            // Check if duration is longer than max video or audio duration (if specified):
            if ((maxVideoLength.HasValue && lastDone > maxVideoLength.Value && numVideoStreams > 0) ||
                (maxAudioLength.HasValue && lastDone > maxAudioLength.Value && numAudioStreams > 0))
            {
                throw new FileProcessingException("Measured video duration exceeds maximum allowed length.");
            }

            // Update maxDuration with the measured duration, and update progress used:
            maxDuration = lastDone;
            progressUsed += ValidateProgressFraction;
        }

        // Determine if we need to make any changes to the file at all - this involves checking: resizing, re-encoding, removing metadata, etc.
        // Also check if we need to remux ignoring "if smaller" (and similar potentially unnecessary) re-encodings.
        bool remuxRequired =
            !Options.ResultFormats.Contains(sourceFormat) ||
            Options.MetadataStrippingMode == VideoMetadataStrippingMode.Required ||
            Options.ForceProgressiveDownload;
        bool remuxGuaranteedRequired = remuxRequired; // Keep track of if remuxing is definitely required, vs maybe required (e.g., to compare size only).
        bool guaranteedFullyCompatibleWithMP4Container = true; // Keep track of if we know for sure all streams are compatible with mp4 container.
        if (!remuxRequired)
        {
            foreach (var stream in sourceInfo.Streams)
            {
                if (stream is FFprobeUtils.VideoStreamInfo videoStream)
                {
                    bool isThumbnail = videoStream.IsAttachedPic || videoStream.IsTimedThumbnails;
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
                    bool reencodingStream = false;
                    bool mustReencodeStream = false;

                    if (codec is null)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        reencodingStream = true;
                        mustReencodeStream = true;
                    }

                    if (codec?.SupportsMP4Muxing != true)
                    {
                        guaranteedFullyCompatibleWithMP4Container = false;
                    }

                    // Check if we can get the pixel format info:
                    var pixFormatInfo = GetPixelFormatCharacteristics(videoStream.PixelFormat);
                    int bitsPerSample = pixFormatInfo?.BitsPerSample ?? videoStream.BitsPerSample;

                    // If we're not trying to avoid re-encoding, mark re-encoding (we handle select smallest logic later):
                    if (Options.VideoReencodeMode == StreamReencodeMode.Always)
                    {
                        reencodingStream = true;
                        mustReencodeStream = true;
                    }

                    // Check if remuxing is already required (the remaining checks are only if remuxing is not yet known to be required):
                    if (mustReencodeStream)
                    {
                        goto checkResize;
                    }

                    // If resizing is enabled, check if we would resize:
                    if (Options.ResizeOptions is { } resizeOptions)
                    {
                        int minDimension = Options.ResultVideoCodecs[0] == VideoCodec.H264 ? 2 : 16;
                        if ((videoStream.Width > int.Max(resizeOptions.Width, minDimension)) ||
                            (videoStream.Height > int.Max(resizeOptions.Height, minDimension)))
                        {
                            reencodingStream = true;
                            mustReencodeStream = true;
                            goto checkResize;
                        }
                    }

                    // Try to check the bits/channel if necessary:
                    if (Options.MaximumBitsPerChannel != BitsPerChannel.Preserve)
                    {
                        if (bitsPerSample <= 0)
                        {
                            reencodingStream = true;
                            mustReencodeStream = true;
                            goto checkResize;
                        }
                        else if (bitsPerSample > (GetBitsPerChannel(Options.MaximumBitsPerChannel) ?? int.MaxValue))
                        {
                            reencodingStream = true;
                            mustReencodeStream = true;
                            goto checkResize;
                        }
                        else if (videoStream.BitsPerSample > 0 && bitsPerSample != videoStream.BitsPerSample)
                        {
                            reencodingStream = true;
                            mustReencodeStream = true;
                            goto checkResize;
                        }
                    }

                    // Try to check the chroma subsampling if necessary:
                    if (Options.MaximumChromaSubsampling != ChromaSubsampling.Preserve)
                    {
                        if (pixFormatInfo is null)
                        {
                            reencodingStream = true;
                            mustReencodeStream = true;
                            goto checkResize;
                        }
                        else if (
                            !pixFormatInfo.Value.IsStandard ||
                            videoStream.AlphaMode ||
                            pixFormatInfo.Value.ChromaSubsampling > (GetChromaSubsampling(Options.MaximumChromaSubsampling) ?? 444))
                        {
                            reencodingStream = true;
                            mustReencodeStream = true;
                            goto checkResize;
                        }
                    }

                    // Check fps:
                    int? targetFps = Options.FpsOptions?.TargetFps;
                    if (targetFps is not null && (
                        videoStream.FpsNum <= 0 ||
                        videoStream.FpsDen <= 0 ||
                        videoStream.FpsNum > (long)videoStream.FpsDen * targetFps.Value))
                    {
                        reencodingStream = true;
                        mustReencodeStream = true;
                        goto checkResize;
                    }

                    // Check if hdr:
                    if (Options.RemapHDRToSDR && !IsKnownSDRColorProfile(videoStream.ColorTransfer, videoStream.ColorPrimaries, videoStream.ColorSpace))
                    {
                        reencodingStream = true;
                        mustReencodeStream = true;
                        goto checkResize;
                    }

                    // Check interlacing:
                    if (Options.ForceProgressiveFrames && !IsProgressive(videoStream.FieldOrder))
                    {
                        reencodingStream = true;
                        mustReencodeStream = true;
                        goto checkResize;
                    }

                    // Check for non-square pixels:
                    if (Options.ForceSquarePixels && (videoStream.SarNum, videoStream.SarDen) is not ((<= 0, <= 0) or (1, 1)))
                    {
                        reencodingStream = true;
                        mustReencodeStream = true;
                    }

                    // Check for if we can resize - if we have to re-encode the stream, we must be able to potentially resize it:
                    // Note: we also check select smallest logic here.
                    checkResize:
                    if (reencodingStream || Options.VideoReencodeMode == StreamReencodeMode.SelectSmallest)
                    {
                        // Determine the resulting chroma subsampling:
                        int sourceSubsampling = pixFormatInfo switch
                        {
                            { ChromaSubsampling: 440 } => 444,
                            { ChromaSubsampling: var x } => x,
                            _ => 444,
                        };
                        int subsampling = int.Min(sourceSubsampling, GetChromaSubsampling(Options.MaximumChromaSubsampling) ?? 444);

                        // Check if the resizing is possible:
                        var (mode, _, _) = CalculateVideoResize(
                            videoStream.Width,
                            videoStream.Height,
                            videoStream.SarNum,
                            videoStream.SarDen,
                            Options.ResizeOptions is null ? null : (Options.ResizeOptions.Width, Options.ResizeOptions.Height),
                            subsampling is 420 or 422,
                            subsampling is 444,
                            Options.ResultVideoCodecs[0] == VideoCodec.HEVC);

                        // If there's an error with resizing, throw now or mark as not re-encoding:
                        // Also, implement handling for select smallest mode:
                        if (mustReencodeStream)
                        {
                            if (mode == 1)
                            {
                                throw new FileProcessingException("Cannot re-encode video to fit within specified dimensions.");
                            }
                            else if (mode == 2)
                            {
                                throw new FileProcessingException("Cannot re-encode very large video to fit within codec maximum pixel count.");
                            }
                        }
                        else if (mode != 0)
                        {
                            reencodingStream = false;
                        }
                        else if (Options.VideoReencodeMode == StreamReencodeMode.SelectSmallest)
                        {
                            reencodingStream = true;
                            mustReencodeStream = true;
                        }
                    }

                    // Update the global re-encoding flags based on this stream:
                    remuxGuaranteedRequired |= mustReencodeStream;
                    remuxRequired |= reencodingStream;
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

                    // Check if remuxing is already required (the remaining checks are only if remuxing is not yet known to be required):
                    if (remuxGuaranteedRequired)
                    {
                        continue;
                    }

                    // Check channels:
                    if ((Options.MaxChannels != AudioChannels.Preserve && audioStream.Channels <= 0) ||
                        audioStream.Channels > (GetAudioChannelCount(Options.MaxChannels) ?? int.MaxValue))
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        continue;
                    }

                    // Check sample rate:
                    if ((Options.MaxSampleRate != AudioSampleRate.Preserve && audioStream.SampleRate is null or <= 0.0) ||
                        audioStream.SampleRate.GetValueOrDefault() > (GetAudioSampleRate(Options.MaxSampleRate) ?? int.MaxValue))
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        continue;
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
                    bool isThumbnail = unrecognizedStream.IsAttachedPic || unrecognizedStream.IsTimedThumbnails;
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
        // We reserve 3% of the processing percent for this process.
        // Note: we always run this step, since while ffmpeg reads mov and mp4 the same, mp4 does not support every single codec that mov does, unless we
        // detected earlier that every stream we saw is compatible with the mp4 container.
        bool[] isCompatibleStream = new bool[sourceInfo.Streams.Length];
        bool[] isCompatibleSubtitleStreamAfterReencodingToMovText = new bool[sourceInfo.Streams.Length];
        if (guaranteedFullyCompatibleWithMP4Container)
        {
            isCompatibleStream.AsSpan().Fill(true);
            isCompatibleSubtitleStreamAfterReencodingToMovText.AsSpan().Fill(true);
        }
        else
        {
            const double StreamCompatibilityCheckProgressFraction = 0.03;
            for (int i = 0; i < sourceInfo.Streams.Length; i++)
            {
                var stream = sourceInfo.Streams[i];

                // Check if we already know this stream is compatible - if it is, we can skip the test and jump to reporting progress:
                if (stream is FFprobeUtils.VideoStreamInfo videoStream)
                {
                    if (videoStream.CodecName is "mjpeg" or "png" ||
                        MatchVideoCodecByName(Options.SourceVideoCodecs, videoStream.CodecName) is { SupportsMP4Muxing: true })
                    {
                        isCompatibleStream[i] = true;
                        goto reportProgress;
                    }
                }
                else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
                {
                    if (MatchAudioCodecByName(Options.SourceAudioCodecs, audioStream.CodecName, audioStream.ProfileName) is { SupportsMP4Muxing: true } ||
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
                    "-copy_unknown", "-xerror", "-hide_banner", "-y",
                    "-f", "mp4",
                    OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ];

                // Run the test command:
                var (_, _, returnCode) = await ProcessUtils.RunProcessToStringAsync(
                    FFmpegExePath,
                    testCommand,
                    cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);
                isCompatibleStream[i] = returnCode == 0;

                // If it was incompatible, and a subtitle stream, check if re-encoding to mov_text would work:
                if (!isCompatibleStream[i] && stream is FFprobeUtils.SubtitleStreamInfo)
                {
                    // Set up command to test copying this stream:
                    testCommand =
                    [
                        "-i", sourceFileWithCorrectExtension.PathExport,
                        "-map", string.Create(CultureInfo.InvariantCulture, $"0:{i}"),
                        "-c", "mov_text",
                        "-t", "1", // Only need a short segment to test (1 second)
                        "-copy_unknown", "-xerror", "-hide_banner", "-y",
                        "-f", "mp4",
                        OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                    ];

                    // Run the test command:
                    (_, _, returnCode) = await ProcessUtils.RunProcessToStringAsync(
                        FFmpegExePath,
                        testCommand,
                        cancellationToken: context.CancellationToken)
                    .ConfigureAwait(false);
                    isCompatibleSubtitleStreamAfterReencodingToMovText[i] = returnCode == 0;
                }

                // Report progress:
                reportProgress:
                if (Options.ProgressCallback != null)
                {
                    await Options.ProgressCallback(
                        (context.FileId, context.VariantId),
                        progressUsed + (StreamCompatibilityCheckProgressFraction * ((double)(i + 1) / (sourceInfo.Streams.Length + 2))))
                    .ConfigureAwait(false);
                }
            }

            progressUsed += StreamCompatibilityCheckProgressFraction;
        }

        // Set up the main command:

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
                mapToOutput: true));

        perInputStreamOverrides.Add(
            new FFmpegUtils.PerStreamMapMetadataOverride(
                fileIndex: Options.MetadataStrippingMode is VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred ? -1 : 0,
                streamKind: 'g',
                streamIndexWithinKind: -1,
                outputIndex: -1));

        if (Options.RemoveAudioStreams)
        {
            perInputStreamOverrides.Add(
                new FFmpegUtils.PerStreamMapOverride(
                    fileIndex: 0,
                    streamKind: 'a',
                    streamIndexWithinKind: -1,
                    mapToOutput: false));
        }

        if (!Options.TryPreserveUnrecognizedStreams)
        {
            perInputStreamOverrides.Add(
                new FFmpegUtils.PerStreamMapOverride(
                    fileIndex: 0,
                    streamKind: 's',
                    streamIndexWithinKind: -1,
                    mapToOutput: false));
        }

        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: '\0', streamIndexWithinKind: -1, codec: "copy"));

        List<(char Kind, int InputIndex, int OutputIndex, bool MapMetadata, FFmpegUtils.PerStreamMetadataOverride? MetadataOverrides)> streamMapping = [];
        List<(int StreamMappingIndex, string SourceValidFileExtension, bool RequiresReencodeForMP4)> streamsToCheckSize = [];
        foreach (var stream in sourceInfo.Streams)
        {
            inputStreamIndex++;
            if (stream is FFprobeUtils.VideoStreamInfo videoStream)
            {
                inputVideoStreamIndex++;
                int id = outputVideoStreamIndex;

                // Handle thumbnail streams:
                bool isThumbnail = videoStream.IsAttachedPic || videoStream.IsTimedThumbnails;
                if (isThumbnail)
                {
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
                    else
                    {
                        perInputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamMapOverride(
                                fileIndex: 0,
                                streamKind: 'v',
                                streamIndexWithinKind: inputVideoStreamIndex,
                                mapToOutput: false));
                    }

                    continue;
                }

                // Map the stream:
                outputVideoStreamIndex++;
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);
                streamMapping.Add((Kind: 'v', InputIndex: inputVideoStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: null));

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: 'v',
                        streamIndexWithinKind: inputVideoStreamIndex,
                        outputIndex: id));

                // Set up shared variables for this stream:
                VideoCodec? videoCodec = MatchVideoCodecByName(Options.ResultVideoCodecs, videoStream.CodecName);
                bool isRequiredReencode = videoCodec is null;
                bool reencode =
                    isRequiredReencode ||
                    Options.VideoReencodeMode != StreamReencodeMode.AvoidReencoding ||
                    !isCompatibleStream[inputStreamIndex];
                FFmpegUtils.PerStreamFilterOverride? filterOverride = null;
                int outputStreamOverridesInitialCount = perOutputStreamOverrides.Count;

                // Check for resizing:
                if (Options.ResizeOptions is { } resizeOptions &&
                    ((videoStream.Width > resizeOptions.Width) || (videoStream.Height > resizeOptions.Height)))
                {
                    // Note: we already checked for invalid dimensions earlier, so no need to worry about 0 or -1.
                    // Note: we also checked for impossible to resize earlier.
                    reencode = true;
                    isRequiredReencode = true;
                }

                // Check for square pixels:
                if (Options.ForceSquarePixels && (videoStream.SarNum, videoStream.SarDen) is not ((<= 0, <= 0) or (1, 1)))
                {
                    reencode = true;
                    isRequiredReencode = true;
                    filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                    perOutputStreamOverrides.Add(filterOverride);
                    filterOverride.MakePixelsSquareMode = videoStream.SarNum > videoStream.SarDen ? 2 : 3;
                }

                // Check for interlacing:
                if (Options.ForceProgressiveFrames && !IsProgressive(videoStream.FieldOrder))
                {
                    reencode = true;
                    isRequiredReencode = true;
                    if (filterOverride is null)
                    {
                        filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                        perOutputStreamOverrides.Add(filterOverride);
                    }

                    filterOverride.Deinterlace = true;
                }

                // Check for fps:
                FFmpegUtils.PerStreamFPSOverride? fpsOverride = null;
                if (Options.FpsOptions is { } fpsOptions)
                {
                    // Note: technically the max fps num & den possible is uint.MaxValue for H.264 and HEVC, but we don't expect to find anything that high or
                    // which would lead even close to that even in integer division mode, so we just use int for the input & long for the output, and let any
                    // exceptions that would occur as a result happen.
                    int maxFpsNum = fpsOptions.TargetFps;

                    if (videoStream.FpsNum <= 0 || videoStream.FpsDen <= 0 || videoStream.FpsNum > (long)videoStream.FpsDen * maxFpsNum)
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
                            newFpsDen = 1;
                        }
                        else
                        {
                            Debug.Assert(
                                fpsOptions.Mode == VideoFpsMode.LimitByIntegerDivision,
                                "Unimplemented FpsOptions.LimitMode value.");

                            // Find division factor: ceil(currentFps / maxFps)
                            int lhs = videoStream.FpsNum;
                            long rhs = (long)videoStream.FpsDen * maxFpsNum;
                            long divideBy = (lhs + rhs - 1) / rhs;

                            // Apply division
                            int gcd = (int)BigInteger.GreatestCommonDivisor(videoStream.FpsNum, divideBy);
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
                        perOutputStreamOverrides.Add(fpsOverride =
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
                if ((Options.MaximumBitsPerChannel != BitsPerChannel.Preserve && bitsPerSample <= 0) ||
                    bitsPerSample > (GetBitsPerChannel(Options.MaximumBitsPerChannel) ?? int.MaxValue) ||
                    (videoStream.BitsPerSample > 0 && bitsPerSample != videoStream.BitsPerSample))
                {
                    reencode = true;
                    isRequiredReencode = true;
                }
                else if (Options.MaximumChromaSubsampling != ChromaSubsampling.Preserve && (
                    pixFormatInfo is null ||
                    !pixFormatInfo.Value.IsStandard ||
                    videoStream.AlphaMode ||
                    pixFormatInfo.Value.ChromaSubsampling > GetChromaSubsampling(Options.MaximumChromaSubsampling)))
                {
                    reencode = true;
                    isRequiredReencode = true;
                }

                // If we're re-encoding, we want to specify the pixel format always:
                // Note: we also check for what HDR remapping causing re-encoding would give here, since it also requires us to always specify something.
                if (reencode || (isHdr && Options.RemapHDRToSDR))
                {
                    int videoSubsampling = pixFormatInfo switch
                    {
                        { ChromaSubsampling: 440 } => 444,
                        { ChromaSubsampling: var x } => x,
                        _ => 444,
                    };
                    int maxSubsampling = GetChromaSubsampling(Options.MaximumChromaSubsampling) ?? 444;
                    int videoBitsPerSample = bitsPerSample switch
                    {
                        <= 8 => 8,
                        <= 10 => 10,
                        <= 12 => 12,
                        _ => 12,
                    };
                    int maxBitsPerSample = GetBitsPerChannel(Options.MaximumBitsPerChannel) ?? 12;
                    if (Options.ResultVideoCodecs[0] != VideoCodec.HEVC) maxBitsPerSample = int.Min(maxBitsPerSample, 10);
                    finalBitsPerChannel = int.Min(videoBitsPerSample, maxBitsPerSample);
                    finalChromaSubsampling = int.Min(videoSubsampling, maxSubsampling);
                    string pixFormat = (finalChromaSubsampling, finalBitsPerChannel) switch
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
                    filterOverride.PixelFormat = pixFormat;
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

                    // Just use BT.709 for everything as our standardized SDR profile:
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorTransferOverride(streamKind: 'v', streamIndexWithinKind: id, colorTransfer: "bt709"));
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorPrimariesOverride(streamKind: 'v', streamIndexWithinKind: id, colorPrimaries: "bt709"));
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorSpaceOverride(streamKind: 'v', streamIndexWithinKind: id, colorSpace: "bt709"));
                }

                // Determine round to even info:
                int chromaSubsampling = GetPixelFormatCharacteristics(filterOverride?.PixelFormat)?.ChromaSubsampling
                    ?? pixFormatInfo?.ChromaSubsampling
                    ?? 444;
                var (roundW, roundH) = chromaSubsampling switch
                {
                    420 => (true, true),
                    422 => (true, false),
                    444 => (false, false),
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                };

                // Check that we haven't exceeded the maximum possible video size / frame rate combo for the codec, and we have a valid size:
                if (reencode)
                {
                    // Determine the resulting size of the video:
                    var (mode, resultWidth, resultHeight) = CalculateVideoResize(
                        videoStream.Width,
                        videoStream.Height,
                        videoStream.SarNum,
                        videoStream.SarDen,
                        Options.ResizeOptions is null ? null : (Options.ResizeOptions.Width, Options.ResizeOptions.Height),
                        roundW,
                        roundH,
                        Options.ResultVideoCodecs[0] == VideoCodec.HEVC);

                    // If it's impossible to resize to this, we should be in the "if smaller" mode & we just bail out of re-encoding for this stream:
                    if (mode != 0 && !isRequiredReencode && Options.VideoReencodeMode == StreamReencodeMode.SelectSmallest)
                    {
                        reencode = false;
                        perOutputStreamOverrides.RemoveRange(
                            outputStreamOverridesInitialCount,
                            perOutputStreamOverrides.Count - outputStreamOverridesInitialCount);
                    }

                    // Continue only if we're still re-encoding:
                    if (reencode)
                    {
                        Debug.Assert(mode == 0, "Video resize mode should be valid when we reach this point.");

                        // If we're meant to resize, then set up our filter:
                        if (resultWidth != videoStream.Width || resultHeight != videoStream.Height)
                        {
                            if (filterOverride is null)
                            {
                                filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                                perOutputStreamOverrides.Add(filterOverride);
                            }

                            filterOverride.ResizeTo = (resultWidth, resultHeight);
                        }

                        // If we have a size & fps, continue checking, otherwise we just have to assume it'll be fine:
                        long resultFpsNum = fpsOverride?.FPSNum ?? videoStream.FpsNum;
                        long resultFpsDen = fpsOverride?.FPSDen ?? videoStream.FpsDen;
                        if (resultWidth > 0 && resultHeight > 0 && resultFpsNum > 0 && resultFpsDen > 0)
                        {
                            // Determine the number of samples or blocks based on the codec:
                            int numSamplesOrBlocks = Options.ResultVideoCodecs[0] == VideoCodec.H264
                                ? (resultWidth + 15) / 16 * ((resultHeight + 15) / 16)
                                : resultWidth * resultHeight;

                            // Get the maximum samples or blocks per second for the codec (this corresponds to level 6.2 for H.264 and level 6.2 for HEVC):
                            // Note: most devices won't accelerate level 6+ of H.264 at all, and HEVC level 6 is also rare, but if we re-encode to something
                            // lower, we would want an option to opt-in/out of that (otherwise preserve wouldn't be possible), so for now we just always use
                            // the absolute maximum. Level 5.2 for H.264 still supports 1080p@172fps and 4k@60fps, so reasonable FPS and/or size limits can be
                            // set to ensure it's never hit anyway.
                            long maxSamplesOrBlocksPerSecond = Options.ResultVideoCodecs[0] == VideoCodec.H264 ? 16711680 : 4278190080;

                            // Determine the maximum fps:
                            int gcd = (int)BigInteger.GreatestCommonDivisor(numSamplesOrBlocks, maxSamplesOrBlocksPerSecond);
                            long maxFpsNum = maxSamplesOrBlocksPerSecond / gcd;
                            long maxFpsDen = numSamplesOrBlocks / gcd;

                            // Check if we exceed the maximum fps:
                            if (checked(resultFpsNum * maxFpsDen > resultFpsDen * maxFpsNum))
                            {
                                if (filterOverride is null)
                                {
                                    filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                                    perOutputStreamOverrides.Add(filterOverride);
                                }

                                if (fpsOverride is null)
                                {
                                    fpsOverride = new FFmpegUtils.PerStreamFPSOverride(streamKind: 'v', streamIndexWithinKind: id, 0, 0);
                                    perOutputStreamOverrides.Add(fpsOverride);
                                }

                                // Set to maximum fps - in this edge case, we ignore the fps mode and just set to the maximum possible to simplify the logic:
                                filterOverride.FPS = (maxFpsNum, maxFpsDen);
                                perOutputStreamOverrides.Add(fpsOverride =
                                    new FFmpegUtils.PerStreamFPSOverride(streamKind: 'v', streamIndexWithinKind: id, fpsNum: maxFpsNum, fpsDen: maxFpsDen));
                                resultFpsNum = maxFpsNum;
                                resultFpsDen = maxFpsDen;
                                fpsOverride.FPSNum = resultFpsNum;
                                fpsOverride.FPSDen = resultFpsDen;
                            }

                            // If FPS numerator or denominator are too large, adjust manually to ensure they get rounded in a way that doesn't exceed the max:
                            // FFmpeg reduces any FPS values above 1001000, so we use the same limit here:
                            if (resultFpsNum > 1001000 || resultFpsDen > 1001000)
                            {
                                if (filterOverride is null)
                                {
                                    filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                                    perOutputStreamOverrides.Add(filterOverride);
                                }

                                if (fpsOverride is null)
                                {
                                    fpsOverride = new FFmpegUtils.PerStreamFPSOverride(streamKind: 'v', streamIndexWithinKind: id, 0, 0);
                                    perOutputStreamOverrides.Add(fpsOverride);
                                }

                                // Round such that both end up <= 1001000, and such that num/den does not increase slightly in the edge case:
                                const int MaxValue = 1001000;
                                int divisionFactor = checked((int)long.Max((resultFpsNum + (MaxValue - 1))
                                    / MaxValue, (maxFpsDen + (MaxValue - 2)) / (MaxValue - 1)));
                                resultFpsNum /= divisionFactor;
                                resultFpsDen = (resultFpsDen + divisionFactor - 1) / divisionFactor;
                                Debug.Assert(resultFpsNum <= MaxValue && resultFpsDen <= MaxValue, "Failed to reduce max fps num and den to <= 1001000.");
                                filterOverride.FPS = (resultFpsNum, resultFpsDen);
                                fpsOverride.FPSNum = resultFpsNum;
                                fpsOverride.FPSDen = resultFpsDen;
                            }
                        }
                    }
                }

                // Keep track if we want to check the size later:
                if (!isRequiredReencode && reencode && Options.VideoReencodeMode == StreamReencodeMode.SelectSmallest)
                {
                    streamsToCheckSize.Add((
                        StreamMappingIndex: streamMapping.Count - 1,
                        SourceValidFileExtension: videoCodec!.WritableFileExtension,
                        RequiresReencodeForMP4: !isCompatibleStream[inputStreamIndex]));
                }

                // If we're re-encoding, and it's interlaced, ensure we de-interlace it always to simplify things:
                if (reencode && !IsProgressive(videoStream.FieldOrder))
                {
                    if (filterOverride is null)
                    {
                        filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                        perOutputStreamOverrides.Add(filterOverride);
                    }

                    filterOverride.Deinterlace = true;
                }

                // Set up codec to use
                if (reencode)
                {
                    if (Options.ResultVideoCodecs[0] == VideoCodec.H264 && FFprobeUtils.Configuration.SupportsLibX264Encoder)
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
                        // Note: we use Debug.Fail here since we should have already validated this earlier, so this just safeguards so we catch issues testing
                        Debug.Fail("The requested video codec did not have a supported encoder available in the configured FFmpeg build (unexpected).");
                    }
                }
            }
            else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
            {
                // If we're removing audio streams, exclude it now:
                inputAudioStreamIndex++;
                if (Options.RemoveAudioStreams)
                    continue;

                // Map the stream:
                int id = outputAudioStreamIndex;
                outputAudioStreamIndex++;
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);
                streamMapping.Add((Kind: 'a', InputIndex: inputAudioStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: null));

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: 'a',
                        streamIndexWithinKind: inputAudioStreamIndex,
                        outputIndex: id));

                // Set up shared variables for this stream:
                AudioCodec? audioCodec = MatchAudioCodecByName(Options.ResultAudioCodecs, audioStream.CodecName, audioStream.ProfileName);
                bool isRequiredReencode = audioCodec is null;
                bool reencode =
                    isRequiredReencode ||
                    Options.AudioReencodeMode != StreamReencodeMode.AvoidReencoding ||
                    !isCompatibleStream[inputStreamIndex];

                // Determine channel adjustment:
                int? targetChannels = GetAudioChannelCount(Options.MaxChannels);
                int numChannels = audioStream.Channels;
                if (targetChannels.HasValue && (audioStream.Channels > targetChannels.Value || audioStream.Channels <= 0))
                {
                    reencode = true;
                    isRequiredReencode = true;
                    numChannels = targetChannels.Value;

                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamChannelsOverride(streamKind: 'a', streamIndexWithinKind: id, channels: targetChannels.Value));
                }

                // Determine sample rate adjustment:
                int? targetSampleRate = GetAudioSampleRate(Options.MaxSampleRate);
                FFmpegUtils.PerStreamSampleRateOverride? sampleRateOverride = null;
                if (targetSampleRate.HasValue && (
                        audioStream.SampleRate.GetValueOrDefault() > targetSampleRate.Value ||
                        audioStream.SampleRate is null or <= 0))
                {
                    reencode = true;
                    isRequiredReencode = true;
                    perOutputStreamOverrides.Add(sampleRateOverride =
                        new FFmpegUtils.PerStreamSampleRateOverride(streamKind: 'a', streamIndexWithinKind: id, sampleRate: targetSampleRate.Value));
                }

                // Keep track if we want to check the size later:
                if (!isRequiredReencode && Options.AudioReencodeMode == StreamReencodeMode.SelectSmallest)
                {
                    streamsToCheckSize.Add((
                        StreamMappingIndex: streamMapping.Count - 1,
                        SourceValidFileExtension: audioCodec!.WritableFileExtension,
                        RequiresReencodeForMP4: !isCompatibleStream[inputStreamIndex]));
                }

                // Set up codec to use (note - currently the only supported codec is AAC-LC, so we aren't checking which one the user selected here currently):
                if (reencode)
                {
                    if (FFprobeUtils.Configuration.SupportsLibFDKAACEncoder && (audioStream.Channels <= 8 || Options.MaxChannels != AudioChannels.Preserve))
                    {
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "libfdk_aac"));

                        int quality = GetLibFDKAACEncoderQuality(Options.AudioQuality);
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'a', streamIndexWithinKind: id, profile: "lc"));
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamVBROverride(streamKind: 'a', streamIndexWithinKind: id, vbr: quality));

                        // For highest & high quality, ensure we use the maximum supported cutoff frequency (20kHz):
                        if (quality >= 4)
                            perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCutoffOverride(streamKind: 'a', streamIndexWithinKind: id, cutoff: 20000));
                    }
                    else if (FFprobeUtils.Configuration.SupportsAACEncoder)
                    {
                        // Note: we can end up using the native aac encoder even if libfdk_aac is available if there are more than 8 channels & we're trying to
                        // preserve them. We do not try to handle additionally here, as we might have to specify a channel layout, etc., if we have more than
                        // the maximum number of channels than is supported by aac (which is 16 + 16 + 16), and we should get an exception if we try to do
                        // something unsupported in this edge-edge case.

                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "aac"));

                        int bitrate = (int)long.Min((long)numChannels * GetNativeAACEncoderBitratePerChannel(Options.AudioQuality), int.MaxValue);
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamBitrateOverride(streamKind: 'a', streamIndexWithinKind: id, bitrate: bitrate));
                    }
                    else
                    {
                        // Note: we use Debug.Fail here since we should have already validated this earlier, so this just safeguards so we catch issues testing
                        Debug.Fail("The requested audio codec did not have a supported encoder available in the configured FFmpeg build (unexpected).");
                    }
                }

                // Check that the sample rate is a valid value for AAC if we're re-encoding:
                if (reencode && (sampleRateOverride?.SampleRate ?? audioStream.SampleRate ?? -1)
                    is not (<= 0.0 or 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 64000 or 88200 or 96000))
                {
                    if (sampleRateOverride is null)
                    {
                        sampleRateOverride =
                            new FFmpegUtils.PerStreamSampleRateOverride(streamKind: 'a', streamIndexWithinKind: id, sampleRate: 0);
                        perOutputStreamOverrides.Add(sampleRateOverride);
                    }

                    // Find the next highest supported sample rate:
                    sampleRateOverride.SampleRate = (sampleRateOverride?.SampleRate ?? audioStream.SampleRate)!.Value switch
                    {
                        > 88200 => 96000,
                        > 64000 => 88200,
                        > 48000 => 64000,
                        > 44000 => 48000,
                        > 32000 => 44100,
                        > 24000 => 32000,
                        > 22050 => 24000,
                        > 16000 => 22050,
                        > 12000 => 16000,
                        > 11025 => 12000,
                        > 8000 => 11025,
                        _ => 8000,
                    };
                }
            }
            else if (stream is FFprobeUtils.SubtitleStreamInfo subtitleStream)
            {
                // If we're not preserving unrecognized streams, skip it (we already marked as excluded by default):
                if (!Options.TryPreserveUnrecognizedStreams)
                {
                    continue;
                }

                // If it's impossible to preserve the subtitle stream in MP4, skip it:
                if (!isCompatibleStream[inputStreamIndex] && !isCompatibleSubtitleStreamAfterReencodingToMovText[inputStreamIndex])
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
                if (!isCompatibleStream[inputStreamIndex])
                {
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamCodecOverride(
                            streamKind: '\0',
                            streamIndexWithinKind: id,
                            codec: "mov_text"));
                }

                // If we're not copying metadata, we want to manually add the metadata we want to preserve back in after normalizing it:
                // Note: mp4 does not support title metadata for the subtitle stream (it does support language though, so we preserve that).
                FFmpegUtils.PerStreamMetadataOverride? metadataOverrides = null;
                if (!mapMetadata)
                {
                    metadataOverrides = new FFmpegUtils.PerStreamMetadataOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: id)
                    {
                        Language = (subtitleStream.Language != "und" && IsValidLanguage(subtitleStream.Language)) ? subtitleStream.Language : null,
                    };
                    perOutputStreamOverrides.Add(metadataOverrides);
                }

                // Final part of mapping the stream:
                streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: metadataOverrides));
            }
            else if (stream is FFprobeUtils.UnrecognizedStreamInfo unrecognizedStream)
            {
                // Handle thumbnail streams:
                bool isThumbnail = unrecognizedStream.IsAttachedPic || unrecognizedStream.IsTimedThumbnails;
                int id = outputStreamIndex;
                if (isThumbnail)
                {
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
                    }
                    else
                    {
                        perInputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamMapOverride(
                                fileIndex: 0,
                                streamKind: '\0',
                                streamIndexWithinKind: inputStreamIndex,
                                mapToOutput: false));
                    }

                    continue;
                }

                // If we're not preserving unrecognized streams, or if the unrecognized stream is not compatible with the MP4 container, we cannot preserve it:
                if (!Options.TryPreserveUnrecognizedStreams || !isCompatibleStream[inputStreamIndex])
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

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: '\0',
                        streamIndexWithinKind: inputStreamIndex,
                        outputIndex: id));
            }
        }

        var resultTempFile = context.GetNewWorkFile(".mp4"); // currently this is our only supported output format when remuxing, so just use it
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
        Func<double, ValueTask>? localProgressCallback = (Options.ProgressCallback != null && maxDuration != 0.0) ? async (durationDone) =>
        {
            // Avoid going backwards or repeating the same progress:
            if (durationDone <= lastDone) return;
            if (durationDone > maxDuration && lastDone < maxDuration) (durationDone, lastDone) = (maxDuration, durationDone);
            else lastDone = durationDone;
            if (durationDone < 0.0 || durationDone > maxDuration) return;

            // Clamp / adjust to [progressUsed, 0.95] range:
            double clampedProgress = double.Clamp(
                progressUsed + (durationDone / maxDuration * (1.0 - ReservedProgress - progressUsed)),
                progressUsed,
                1.0 - ReservedProgress);
            lastDone = durationDone;
            await Options.ProgressCallback!((context.FileId, context.VariantId), clampedProgress).ConfigureAwait(false);
        } : null;
        await FFmpegUtils.RunFFmpegCommandAsync(
            command,
            localProgressCallback,
            localProgressCallback != null ? progressTempFile : null,
            context.CancellationToken)
        .ConfigureAwait(false);

        // For any streams that are in a "if smaller than" mode, we want to do additional passes to see if it ended up smaller:
        if (streamsToCheckSize.Count > 0)
        {
            // Determine which are actually smaller & which are actually smaller, but would still need to be re-encoded if remuxed:
            List<(int Index, bool NeedsReencode, bool StrictlySmaller)> wasSmaller = [];
            var reencodedTempFile = context.GetNewWorkFile(".mp4");
            var fromOriginalTempFileMp4 = context.GetNewWorkFile(".mp4");
            bool mustRemux = remuxGuaranteedRequired;
            for (int i = 0; i < streamsToCheckSize.Count; i++)
            {
                var streamToCheck = streamsToCheckSize[i];
                var mappedStream = streamMapping[streamToCheck.StreamMappingIndex];

                // If we have to remux anyway, skip this check if we need to re-encode this stream regardless, since it won't matter:
                if (mustRemux && streamToCheck.RequiresReencodeForMP4)
                {
                    goto updateProgress;
                }

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
                            streamKind: mappedStream.Kind,
                            streamIndexWithinKind: mappedStream.OutputIndex,
                            mapToOutput: true),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: 'g',
                            streamIndexWithinKind: -1,
                            outputIndex: -1),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: mappedStream.Kind,
                            streamIndexWithinKind: mappedStream.OutputIndex,
                            outputIndex: 0),
                    ],
                    perOutputStreamOverrides:
                    [
                        new FFmpegUtils.PerStreamCodecOverride(streamKind: '\0', streamIndexWithinKind: -1, codec: "copy")
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
                            streamKind: mappedStream.Kind,
                            streamIndexWithinKind: mappedStream.InputIndex,
                            mapToOutput: true),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: 'g',
                            streamIndexWithinKind: -1,
                            outputIndex: -1),
                        new FFmpegUtils.PerStreamMapMetadataOverride(
                            fileIndex: -1,
                            streamKind: mappedStream.Kind,
                            streamIndexWithinKind: mappedStream.InputIndex,
                            outputIndex: 0),
                    ],
                    perOutputStreamOverrides:
                    [
                        new FFmpegUtils.PerStreamCodecOverride(streamKind: '\0', streamIndexWithinKind: -1, codec: "copy")
                    ],
                    mapChaptersFrom: -1,
                    forceProgressiveDownloadSupport: false);
                await FFmpegUtils.RunFFmpegCommandAsync(extractCommandOriginal, null, null, context.CancellationToken).ConfigureAwait(false);
                long originalApproxSize = fromOriginalTempFile.Length;
                fromOriginalTempFile.Delete();

                // Add to applicable lists:
                if (originalApproxSize <= reencodedApproxSize)
                {
                    wasSmaller.Add((i, streamToCheck.RequiresReencodeForMP4, originalApproxSize < reencodedApproxSize));
                }
                else
                {
                    mustRemux = true;
                }

                // We use half of our headroom for this first pass, so update progress:
                updateProgress:
                if (localProgressCallback != null)
                {
                    double progressPortion = ReservedProgress * 0.5 / (streamsToCheckSize.Count + 2);
                    double progressValue = 1.0 - ReservedProgress + (progressPortion * (i + 1));
                    await Options.ProgressCallback!((context.FileId, context.VariantId), progressValue).ConfigureAwait(false);
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
                var nextWasSmaller = (Value: streamsToCheckSize[wasSmaller[0].Index], wasSmaller[0].NeedsReencode, wasSmaller[0].StrictlySmaller);
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
                    if (nextWasSmaller.Value.StreamMappingIndex >= 0)
                    {
                        var mappedStream = streamMapping[nextWasSmaller.Value.StreamMappingIndex];
                        if (mappedStream.InputIndex == inputIndex && mappedStream.Kind == kind)
                        {
                            useOriginal = !nextWasSmaller.NeedsReencode && nextWasSmaller.StrictlySmaller;
                            wasSmallerIdx++;
                            if (wasSmallerIdx < wasSmaller.Count)
                            {
                                nextWasSmaller = (
                                    streamsToCheckSize[wasSmaller[wasSmallerIdx].Index],
                                    wasSmaller[wasSmallerIdx].NeedsReencode,
                                    wasSmaller[wasSmallerIdx].StrictlySmaller);
                            }
                            else
                            {
                                nextWasSmaller = default;
                                nextWasSmaller.Value.StreamMappingIndex = -1;
                            }
                        }
                    }

                    // Map the stream:
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: useOriginal ? 0 : 1,
                            streamKind: kind,
                            streamIndexWithinKind: useOriginal ? inputIndex : outputIndex,
                            mapToOutput: true));

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
                Func<double, ValueTask>? localProgressCallbackInner = progressTempFile != null ? async (durationDone) =>
                {
                    // Avoid going backwards or repeating the same progress:
                    if (durationDone <= lastDone) return;
                    if (durationDone > maxDuration && lastDone < maxDuration) (durationDone, lastDone) = (maxDuration, durationDone);
                    else lastDone = durationDone;
                    if (durationDone < 0.0 || durationDone > maxDuration) return;

                    // Clamp to [0.0, 0.98] range of our remaining reserved portion:
                    double clampedProgress = double.Clamp(durationDone / maxDuration * (1.0 - ReservedProgressInner), 0.0, 1.0 - ReservedProgressInner);
                    lastDone = durationDone;
                    await Options.ProgressCallback!(
                        (context.FileId, context.VariantId),
                        1.0 - (ReservedProgress / 2.0) + (clampedProgress * (ReservedProgress / 2.0)))
                    .ConfigureAwait(false);
                } : null;
                await FFmpegUtils.RunFFmpegCommandAsync(
                    mixCommand,
                    localProgressCallbackInner,
                    localProgressCallback != null ? progressTempFile : null,
                    context.CancellationToken)
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
        (colorTransfer is null or "bt709" or "bt601" or "bt470" or "bt470bg" or "smpte170m" or "smpte240m" or "iec61966-2-1") &&
        (colorPrimaries is null or "bt709" or "bt470m" or "bt470bg" or "smpte170m" or "smpte240m") &&
        (colorSpace is null or "bt709" or "bt470m" or "bt470bg" or "smpte170m" or "smpte240m" or "srgb" or "iec61966-2-1" or "gbr");

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
        - 420: 4:2:0, 2x2 blocks
        - 422: 4:2:2, 2x1 blocks
        - 440: 4:4:0, 1x2 blocks
        - 444: 4:4:4, 1x1 blocks
    */
    private static (int BitsPerSample, bool IsStandard, int ChromaSubsampling)? GetPixelFormatCharacteristics(string? pixelFormat)
        => pixelFormat switch
        {
            "yuv420p" => (8, true, 420),
            "yuvj420p" => (8, true, 420),
            "yuv422p" => (8, true, 422),
            "yuvj422p" => (8, true, 422),
            "yuv444p" => (8, true, 444),
            "yuvj444p" => (8, true, 444),
            "yuv420p10le" => (10, true, 420),
            "yuv422p10le" => (10, true, 422),
            "yuv444p10le" => (10, true, 444),
            "gbrp" => (8, false, 444),
            "gbrp10le" => (10, false, 444),
            "yuv420p12le" => (12, true, 420),
            "yuv422p12le" => (12, true, 422),
            "yuv444p12le" => (12, true, 444),
            "gbrp12le" => (12, false, 444),
            "yuv440p" => (8, false, 440),
            "yuv440p10le" => (10, false, 440),
            "yuv440p12le" => (12, false, 440),
            _ => null,
        };

    private static bool IsValidLanguage(string? language)
    {
        // Currently we just check for potentially valid 3-letter lowercase code, rather than checking against the precise ISO 639-2/T list:
        if (language is not { Length: 3 }) return false;
        return !language.AsSpan().ContainsAnyExceptInRange('a', 'z');
    }

    private static readonly SearchValues<char> _invalidTitleCharsAndSurrogates = SearchValues.Create([
        .. Enumerable.Range(0, 32).Select((x) => (char)x), // Control characters
        .. Enumerable.Range(0xD800, 0x0800).Select((x) => (char)x) // Surrogate range
    ]);

    // Helper to share the logic for resizing a video.
    // Returned mode is one of 0 (success), 1 (cannot preserve both min & max), or 2 (cannot reduce pixel count w/o violating minimum dimensions).
    private static (int Mode, int ResultWidth, int ResultHeight) CalculateVideoResize(
        int streamWidth, int streamHeight, int streamSarNum, int streamSarDen, (int Width, int Height)? maxDimensions, bool roundW, bool roundH, bool isH265)
    {
        // Determine the maximum dimensions allowed, and the minimum dimension allowed, based on codec and requested max dimensions:
        int maxW = int.Min(maxDimensions?.Width ?? int.MaxValue, isH265 ? 65535 : 16384);
        int maxH = int.Min(maxDimensions?.Height ?? int.MaxValue, isH265 ? 65535 : 16384);
        int minDimension = isH265 ? 16 : 2;

        // Determine the resulting size of the video:
        int resultWidth, resultHeight;
        while (true)
        {
            resultWidth = streamWidth;
            resultHeight = streamHeight;

            // Resize to square pixels if wanted:

            if (streamSarNum <= 0 || streamSarDen <= 0)
            {
                streamSarNum = 1;
                streamSarDen = 1;
            }

            if (streamSarNum != 1 || streamSarDen != 1)
            {
                if (streamSarNum > streamSarDen)
                {
                    resultWidth = int.Max(1, (int)double.Truncate(0.5 + (resultWidth * ((double)streamSarNum / streamSarDen))));
                }
                else
                {
                    resultHeight = int.Max(1, (int)double.Truncate(0.5 + (resultHeight / ((double)streamSarNum / streamSarDen))));
                }
            }

            // Apply min & max scaling if any:
            {
                // Apply max:
                double w1 = double.Min(maxW, (double)resultWidth / resultHeight * maxH);
                double h1 = double.Min(maxH, (double)resultHeight / resultWidth * maxW);

                // Apply min:
                double w2 = double.Max(minDimension, w1 / h1 * minDimension);
                double h2 = double.Max(minDimension, h1 / w1 * minDimension);

                // Round as required:
                double w3 = double.Round(w2 / (roundW ? 2 : 1)) * (roundW ? 2 : 1);
                double h3 = double.Round(h2 / (roundH ? 2 : 1)) * (roundH ? 2 : 1);
                if (w3 > maxW || h3 > maxH)
                {
                    w3 = double.Floor(w2 / (roundW ? 2 : 1)) * (roundW ? 2 : 1);
                    h3 = double.Floor(h2 / (roundH ? 2 : 1)) * (roundH ? 2 : 1);
                }

                // Store final size:
                resultWidth = (int)w3;
                resultHeight = (int)h3;
            }

            // If we couldn't achieve both the min & max dimensions, throw an error:
            if ((resultWidth < minDimension || resultHeight < minDimension) && (resultWidth > maxW || resultHeight > maxH))
            {
                return (1, -1, -1);
            }

            // HEVC supports a minimum size of 16 usually, as it can usually set the CTU size to 16x16, but for suitably large videos,
            // it needs to use 32x32 CTUs (when one dimension is at least 4217) or 64x64 CTUs (when one dimension is at least 64799).
            bool newMin = false;
            if ((resultWidth >= 64799 || resultHeight >= 64799) && (minDimension != 64))
            {
                minDimension = int.Max(minDimension, 64);
                newMin = true;
            }
            else if ((resultWidth >= 4217 || resultHeight >= 4217) && (minDimension != 32))
            {
                minDimension = int.Max(minDimension, 32);
                newMin = true;
            }

            // If our video is smaller than the new minimum size, try again:
            if (newMin && (resultWidth < minDimension || resultHeight < minDimension))
            {
                continue;
            }

            // The absolute maximum number of pixels is limited by ffmpeg, which sets a limit of (width + 128) * (height + 128) * 8 <= 2^31 - 1.
            // Note: currently all supported pixel formats used for output use 8 for the bytes per pixel value; if this changes we'd need to update this.
            // Note: if the limit changed, here is what would happen: either videos would be being resized down more than necessary, or ffmpeg would fail to
            // apply the resize filter (which will cause an exception to occur), so we consider this to be safe to rely on, as if it changes we will notice &
            // there is no meaningful malicious usage possible still.
            if ((resultWidth + 128L) * (resultHeight + 128L) > int.MaxValue / 8)
            {
                // If one dimension is equal to the min, we can't reduce further, so throw an error:
                if (maxW == minDimension || maxH == minDimension)
                {
                    return (2, -1, -1);
                }

                // Set max dimensions to current size first
                maxW = resultWidth;
                maxH = resultHeight;

                // Now, reduce the max dimensions to what should be correct approximately, and ensure at least 1 dimension is reduced:
                double scaleFactor = double.Sqrt(int.MaxValue / 8.0 / ((resultWidth + 128L) * (resultHeight + 128L)));
                int newMaxW = int.Min((int)double.Ceiling(resultWidth * scaleFactor), maxW);
                int newMaxH = int.Min((int)double.Ceiling(resultHeight * scaleFactor), maxH);
                if (newMaxW == maxW && newMaxH == maxH)
                {
                    if (newMaxW > newMaxH) newMaxW--;
                    else newMaxH--;
                }

                // Update our max dimensions and try again:
                maxW = newMaxW;
                maxH = newMaxH;
                continue;
            }

            break;
        }

        return (0, resultWidth, resultHeight);
    }

    private static bool IsProgressive(string? fieldOrder) => fieldOrder is null or "progressive";
}
