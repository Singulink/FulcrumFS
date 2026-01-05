using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Singulink.IO;
using Singulink.Threading;

#pragma warning disable SA1203 // Constants should appear before fields

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

        // Note: we need these subtitle encoders only if we're trying to preserve unrecognized streams:
        if (Options.TryPreserveUnrecognizedStreams)
        {
            if (!FFprobeUtils.Configuration.SupportsMovTextEncoder)
                throw new NotSupportedException("The required subtitle encoder for 'mov_text' is not supported by the configured ffmpeg installation.");

            if (!FFprobeUtils.Configuration.SupportsDvdSubEncoder)
                throw new NotSupportedException("The required subtitle encoder for 'dvdsub' is not supported by the configured ffmpeg installation.");
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

        if (!FFprobeUtils.Configuration.SupportsSetsarFilter)
        {
            throw new NotSupportedException("The required 'setsar' video filter is not supported by the configured ffmpeg installation.");
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
    /// <param name="maxConcurrentProcesses">The maximum number of concurrent ffmpeg processes to allow. Default is currently
    /// <see cref="Environment.ProcessorCount" />. Note: there is an additional process added used for short-lived processes.</param>
    public static void ConfigureWithFFmpegExecutables(IAbsoluteDirectoryPath dirPath, int maxConcurrentProcesses = -1)
    {
        var (ffmpeg, ffprobe) = OperatingSystem.IsWindows()
            ? (dirPath.CombineFile("ffmpeg.exe"), dirPath.CombineFile("ffprobe.exe"))
            : (dirPath.CombineFile("ffmpeg"), dirPath.CombineFile("ffprobe"));

        if (!ffmpeg.Exists)
            throw new FileNotFoundException("FFmpeg executable not found in specified directory.", ffmpeg.ToString());

        if (!ffprobe.Exists)
            throw new FileNotFoundException("FFprobe executable not found in specified directory.", ffprobe.ToString());

        if (maxConcurrentProcesses == -1)
            maxConcurrentProcesses = Environment.ProcessorCount;

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

        // Check file name is not going to be potentially problematic for ffmpeg (e.g., contains special characters):
        // Note: we assume that paths given by 'GetNewWorkFile' are safe if the original is, so we only check the original source file here.
        FilePath.ParseAbsolute(tempOutputFile.PathExport, PathOptions.NoUnfriendlyNames);

        // Read info of source video:
        FFprobeUtils.VideoFileInfo sourceInfo;
        try
        {
            sourceInfo = await FFprobeUtils.GetVideoFileAsync(tempOutputFile, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileProcessingException("Failed to read source video file information.", ex);
        }

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
                tempOutputFile2.ParentDirectory.Create();
                var destStream = tempOutputFile2.OpenAsyncStream(FileMode.Create, FileAccess.Write);
                await using (destStream.ConfigureAwait(false))
                {
                    await sourceStream.CopyToAsync(destStream, context.CancellationToken).ConfigureAwait(false);
                }
            }

            FFprobeUtils.VideoFileInfo sourceInfo2;
            try
            {
                sourceInfo2 = await FFprobeUtils.GetVideoFileAsync(tempOutputFile2, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FileProcessingException("Failed to read source video file information after correcting the file extension.", ex);
            }

            if (sourceInfo2.FormatName != sourceInfo.FormatName)
                throw new FileProcessingException("The video format is inconsistent with its file extension in a potentially malicious way.");
        }

        // Validate source streams:

        int numVideoStreams = 0;
        int numAudioStreams = 0;
        bool anyInvalidCodecs = false;
        foreach (var stream in sourceInfo.Streams)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (stream is FFprobeUtils.VideoStreamInfo videoStream)
            {
                // Skip thumbnails
                if (IsThumbnailStream(videoStream))
                {
                    continue;
                }

                int idx = numVideoStreams++;

                double? duration = videoStream.Duration ?? sourceInfo.Duration;
                if (duration is not null)
                {
                    if (Options.VideoSourceValidation.MaxLength.HasValue && duration > Options.VideoSourceValidation.MaxLength.Value.TotalSeconds)
                        throw new FileProcessingException($"Video stream {idx} is longer than the maximum allowed duration.");

                    if (Options.VideoSourceValidation.MinLength.HasValue && duration < Options.VideoSourceValidation.MinLength.Value.TotalSeconds)
                        throw new FileProcessingException($"Video stream {idx} is shorter than the minimum required duration.");
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
                        throw new FileProcessingException($"Video stream {idx} width exceeds the maximum allowed width.");
                }

                if (Options.VideoSourceValidation.MaxHeight.HasValue)
                {
                    if (videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown height, cannot validate.");

                    if (videoStream.Height > Options.VideoSourceValidation.MaxHeight.Value)
                        throw new FileProcessingException($"Video stream {idx} height exceeds the maximum allowed height.");
                }

                if (Options.VideoSourceValidation.MaxPixels.HasValue)
                {
                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown dimensions, cannot validate.");

                    if ((long)videoStream.Width * videoStream.Height > Options.VideoSourceValidation.MaxPixels.Value)
                        throw new FileProcessingException($"Video stream {idx} exceeds the maximum allowed pixel count.");
                }

                if (Options.VideoSourceValidation.MinWidth.HasValue)
                {
                    if (videoStream.Width <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown width, cannot validate.");

                    if (videoStream.Width < Options.VideoSourceValidation.MinWidth.Value)
                        throw new FileProcessingException($"Video stream {idx} width is less than the minimum required width.");
                }

                if (Options.VideoSourceValidation.MinHeight.HasValue)
                {
                    if (videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown height, cannot validate.");

                    if (videoStream.Height < Options.VideoSourceValidation.MinHeight.Value)
                        throw new FileProcessingException($"Video stream {idx} height is less than the minimum required height.");
                }

                if (Options.VideoSourceValidation.MinPixels.HasValue)
                {
                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                        throw new FileProcessingException($"Video stream {idx} has unknown dimensions, cannot validate.");

                    if ((long)videoStream.Width * videoStream.Height < Options.VideoSourceValidation.MinPixels.Value)
                        throw new FileProcessingException($"Video stream {idx} is less than the minimum required pixel count.");
                }

                // Check codec:
                if (MatchVideoCodecByName(Options.SourceVideoCodecs, videoStream.CodecName, videoStream.CodecTagString) is null)
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
                        throw new FileProcessingException($"Audio stream {idx} is longer than the maximum allowed duration.");

                    if (Options.AudioSourceValidation.MinLength.HasValue && duration < Options.AudioSourceValidation.MinLength.Value.TotalSeconds)
                        throw new FileProcessingException($"Audio stream {idx} is shorter than the minimum required duration.");
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

        // Values used for validation:
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
        double? maxMeasuredDuration = null;
        var progressTempFile = (Options.ProgressCallback != null && Options.ForceValidateAllStreams && maxDuration != 0.0)
            ? context.GetNewWorkFile(".txt")
            : null;
        int streamsValidated = 0;
        bool[] validatedStreams = new bool[sourceInfo.Streams.Length];
        double progressUsed = 0.0;
        bool finishedValidateStreams = false;

        // Helper to make stream validation easier - returns a bool indicating if we exceeded max length:
        async ValueTask<(bool Exceeded, double? MeasuredDuration)> ValidateStreamsAsync(
            double? maxLength,
            double expectedMaxDuration,
            int[] mappedInputIndicesOrdered,
            IEnumerable<FFmpegUtils.PerStreamMapOverride>? mappedStreams,
            bool countDuration)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            Debug.Assert(!mappedInputIndicesOrdered.Any((x) => validatedStreams[x]), "None of the streams to validate should have already been validated.");
            Debug.Assert(mappedInputIndicesOrdered.Order().SequenceEqual(mappedInputIndicesOrdered), "The mapped input indices should be ordered.");
            Debug.Assert(!finishedValidateStreams, "Cannot call ValidateStreamsAsync after FinishedValidation.");

            mappedStreams ??=
                mappedInputIndicesOrdered.Select((x) => new FFmpegUtils.PerStreamMapOverride(
                    fileIndex: 0,
                    streamKind: '\0',
                    streamIndexWithinKind: x,
                    mapToOutput: true));

            var (exceededMaxLength, actualMaxDuration, progressTempFile2, progressUsed2) = await FullyValidateStreamsAsync(
                context,
                sourceFileWithCorrectExtension,
                sourceInfo,
                Options.ProgressCallback,
                maxLength,
                expectedMaxDuration,
                (i) => mappedInputIndicesOrdered.BinarySearch(i) switch { < 0 => null, var x => x },
                mappedStreams,
                mappedInputIndicesOrdered.Length,
                progressUsed,
                sourceInfo.Streams.Length,
                progressTempFile,
                countDuration,
                context.CancellationToken).ConfigureAwait(false);

            streamsValidated += mappedInputIndicesOrdered.Length;

            if (countDuration && !exceededMaxLength && actualMaxDuration is { })
            {
                maxMeasuredDuration = maxMeasuredDuration switch
                {
                    { } x => double.Max(x, actualMaxDuration.Value),
                    null => actualMaxDuration,
                };
            }

            foreach (int idx in mappedInputIndicesOrdered)
            {
                validatedStreams[idx] = true;
            }

            progressTempFile = progressTempFile2;
            progressUsed = progressUsed2;
            return (exceededMaxLength, actualMaxDuration);
        }

        void FinishedValidation()
        {
            if (!Options.ForceValidateAllStreams) return;
            Debug.Assert(streamsValidated == sourceInfo.Streams.Length, "All streams should have been validated.");
            Debug.Assert(!validatedStreams.Contains(false), "All streams should be marked as validated.");
            Debug.Assert(!finishedValidateStreams, "FinishedValidation should only be called once.");
            if (maxMeasuredDuration is { }) maxDuration = maxMeasuredDuration.Value;
#if DEBUG
            finishedValidateStreams = true;
#endif
        }

        // Validate the actual video streams duration if we want to check their min/max lengths & we're forcing full validation:
        if (Options.ForceValidateAllStreams && Options.VideoSourceValidation is { MinLength: not null } or { MaxLength: not null })
        {
            // Get the actual duration:
            var (exceeded, measuredDuration) = await ValidateStreamsAsync(
                Options.VideoSourceValidation.MaxLength?.TotalSeconds,
                maxDuration,
                [
                    .. sourceInfo.Streams
                        .Select((x, i) => (Value: x, Index: i))
                        .Where((t) => t.Value is FFprobeUtils.VideoStreamInfo vs && !IsThumbnailStream(vs))
                        .Select((t) => t.Index)
                ],
                [
                    new FFmpegUtils.PerStreamMapOverride(fileIndex: 0, streamKind: 'v', streamIndexWithinKind: -1, mapToOutput: true),
                    .. sourceInfo.Streams
                        .Select((x, i) => (Value: x, Index: i))
                        .Where((t) => t.Value is FFprobeUtils.VideoStreamInfo vs && IsThumbnailStream(vs))
                        .Select((t) => new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: '\0',
                            streamIndexWithinKind: t.Index,
                            mapToOutput: false)),
                ],
                countDuration: true)
            .ConfigureAwait(false);

            // Check if duration is outside of allowed range:
            measuredDuration ??= 0.0;
            var measuredDurationTimeSpan = TimeSpan.FromSeconds(measuredDuration.Value);
            if (exceeded ||
                (Options.VideoSourceValidation.MaxLength is { } && measuredDurationTimeSpan > Options.VideoSourceValidation.MaxLength.Value))
            {
                throw new FileProcessingException("Measured video stream duration exceeds the maximum allowed duration.");
            }
            else if (Options.VideoSourceValidation.MinLength is { } && measuredDurationTimeSpan < Options.VideoSourceValidation.MinLength.Value)
            {
                throw new FileProcessingException("Measured video stream duration is less than the minimum required duration.");
            }
        }

        // Validate the actual audio streams duration if we want to check their min/max lengths & we're forcing full validation:
        if (Options.ForceValidateAllStreams && Options.AudioSourceValidation is { MinLength: not null } or { MaxLength: not null })
        {
            // Get the actual duration:
            var (exceeded, measuredDuration) = await ValidateStreamsAsync(
                Options.AudioSourceValidation.MaxLength?.TotalSeconds,
                maxDuration,
                [.. sourceInfo.Streams
                    .Select((x, i) => (Value: x, Index: i))
                    .Where((t) => t.Value is FFprobeUtils.AudioStreamInfo)
                    .Select((t) => t.Index)],
                [new FFmpegUtils.PerStreamMapOverride(fileIndex: 0, streamKind: 'a', streamIndexWithinKind: -1, mapToOutput: true)],
                countDuration: !Options.RemoveAudioStreams)
            .ConfigureAwait(false);

            // Check if duration is outside of allowed range:
            measuredDuration ??= 0.0;
            var measuredDurationTimeSpan = TimeSpan.FromSeconds(measuredDuration.Value);
            if (exceeded ||
                (Options.AudioSourceValidation.MaxLength is { } && measuredDurationTimeSpan > Options.AudioSourceValidation.MaxLength.Value))
            {
                throw new FileProcessingException("Measured audio stream duration exceeds the maximum allowed duration.");
            }
            else if (Options.AudioSourceValidation.MinLength is { } && measuredDurationTimeSpan < Options.AudioSourceValidation.MinLength.Value)
            {
                throw new FileProcessingException("Measured audio stream duration is less than the minimum required duration.");
            }
        }

        // Determine if we need to make any changes to the file at all - this involves checking: resizing, re-encoding, removing metadata, etc.
        // Also check if we need to remux ignoring "if smaller" (and similar potentially unnecessary) re-encodings.
        bool remuxRequired =
            !Options.ResultFormats.Contains(sourceFormat) ||
            Options.MetadataStrippingMode == VideoMetadataStrippingMode.Required ||
            Options.ForceProgressiveDownload;
        bool remuxGuaranteedRequired = remuxRequired; // Keep track of if remuxing is definitely required, vs maybe required (e.g., to compare size only).
        bool guaranteedFullyCompatibleWithMP4Container = true; // Keep track of if we know for sure all streams are compatible with mp4 container.
        foreach (var stream in sourceInfo.Streams)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (stream is FFprobeUtils.VideoStreamInfo videoStream)
            {
                bool isThumbnail = IsThumbnailStream(videoStream);
                if (Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.None or VideoMetadataStrippingMode.Preferred) && isThumbnail)
                {
                    remuxRequired = true;
                    remuxGuaranteedRequired = true;
                }
                else if (isThumbnail && videoStream.CodecName is not ("mjpeg" or "png"))
                {
                    // "mjpeg" and "png" are common thumbnail codecs which do happen to be mp4 compatible.
                    guaranteedFullyCompatibleWithMP4Container = false;
                }

                if (isThumbnail)
                {
                    continue;
                }

                VideoCodec? codec = MatchVideoCodecByName(Options.ResultVideoCodecs, videoStream.CodecName, videoStream.CodecTagString);
                bool reencodingStream = false;
                bool mustReencodeStream = false;

                if (codec is null)
                {
                    remuxRequired = true;
                    remuxGuaranteedRequired = true;
                    reencodingStream = true;
                    mustReencodeStream = true;
                }

                if ((codec ?? MatchVideoCodecByName(Options.SourceVideoCodecs, videoStream.CodecName, videoStream.CodecTagString))?.SupportsMP4Muxing != true)
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

                // Check fps:
                int? targetFps = Options.FpsOptions?.TargetFps;
                bool changingFps = false;
                if (targetFps is not null && (
                    videoStream.FpsNum <= 0 ||
                    videoStream.FpsDen <= 0 ||
                    videoStream.FpsNum > (long)videoStream.FpsDen * targetFps.Value))
                {
                    reencodingStream = true;
                    mustReencodeStream = true;
                    changingFps = true;
                }

                // Check if remuxing is already required (the remaining checks are only if remuxing is not yet known to be required):
                if (mustReencodeStream)
                {
                    goto checkResize;
                }

                // If resizing is enabled, check if we would resize:
                if (Options.ResizeOptions is { } resizeOptions)
                {
                    if ((videoStream.Width > resizeOptions.Width) ||
                        (videoStream.Height > resizeOptions.Height))
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
                if (Options.ForceSquarePixels && HasNonSquarePixels(videoStream.SarNum, videoStream.SarDen))
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
                    int sourceSubsampling = NormalizeChromaSubsampling(pixFormatInfo?.ChromaSubsampling);
                    int subsampling = int.Min(sourceSubsampling, GetChromaSubsampling(Options.MaximumChromaSubsampling) ?? 444);
                    var (roundW, roundH) = GetChromaSubsamplingRounding(subsampling);

                    // Check if the resizing is possible:
                    // Note: we only provide fps if we know the exact value we'll end up with right now.
                    var (mode, _, _) = CalculateVideoResize(
                        videoStream.Width,
                        videoStream.Height,
                        Options.ForceSquarePixels ? videoStream.SarNum : -1,
                        Options.ForceSquarePixels ? videoStream.SarDen : -1,
                        Options.ResizeOptions is null ? null : (Options.ResizeOptions.Width, Options.ResizeOptions.Height),
                        !changingFps && videoStream.FpsNum > 0 && videoStream.FpsDen > 0 ? (videoStream.FpsNum, videoStream.FpsDen) : null,
                        roundW,
                        roundH,
                        Options.ResultVideoCodecs[0] != VideoCodec.H264,
                        context.CancellationToken);

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

                if (Options.AudioReencodeMode == StreamReencodeMode.Always)
                {
                    remuxGuaranteedRequired = true;
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
                if ((Options.MaxSampleRate != AudioSampleRate.Preserve && audioStream.SampleRate is null or <= 0) ||
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
                else if (subtitleStream.CodecName is not ("mov_text" or "dvd_subtitle"))
                {
                    // "mov_text" and "dvd_subtitle" are common subtitle codecs which do happen to be mp4 compatible.
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

        // If remuxing is not required, we can just return the original file, after potentially validating its streams fully if required:
        returnOriginal:
        if (!remuxRequired)
        {
            if (Options.ForceValidateAllStreams)
            {
                int[] mappedStreams = [.. Enumerable.Range(0, sourceInfo.Streams.Length).Where((i) => !validatedStreams[i])];
                await ValidateStreamsAsync(
                    maxLength: null,
                    expectedMaxDuration: maxDuration,
                    mappedInputIndicesOrdered: mappedStreams,
                    mappedStreams: null,
                    countDuration: false).ConfigureAwait(false);
                FinishedValidation();
            }

            return FileProcessingResult.File(sourceFileWithCorrectExtension, hasChanges: hasChangedExtension);
        }

        // We want to figure out which streams cannot be copied to the output container directly first - including unrecognised streams.
        // We do this by attempting to copy the streams to the output container one at a time and check for errors - any streams that cause errors, we mark as
        // requiring re-encoding or being uncopyable.
        // We reserve 3% of the processing percent for this process.
        // Note: we always run this step, since while ffmpeg reads mov and mp4 the same, mp4 does not support every single codec that mov does, unless we
        // detected earlier that every stream we saw is compatible with the mp4 container.
        bool[] isCompatibleStream = new bool[sourceInfo.Streams.Length];
        bool[] isCompatibleSubtitleStreamAfterReencodingToMovText = new bool[sourceInfo.Streams.Length];
        bool[] isCompatibleSubtitleStreamAfterReencodingToDvdSubtitle = new bool[sourceInfo.Streams.Length];
        if (guaranteedFullyCompatibleWithMP4Container)
        {
            isCompatibleStream.AsSpan().Fill(true);
            isCompatibleSubtitleStreamAfterReencodingToMovText.AsSpan().Fill(true);
            isCompatibleSubtitleStreamAfterReencodingToDvdSubtitle.AsSpan().Fill(true);
        }
        else
        {
            const double StreamCompatibilityCheckProgressFraction = 0.03;
            for (int i = 0; i < sourceInfo.Streams.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var stream = sourceInfo.Streams[i];

                // Check if we already know this stream is compatible - if it is, we can skip the test and jump to reporting progress:
                if (stream is FFprobeUtils.VideoStreamInfo videoStream)
                {
                    if (videoStream.CodecName is "mjpeg" or "png" ||
                        MatchVideoCodecByName(Options.SourceVideoCodecs, videoStream.CodecName, videoStream.CodecTagString) is { SupportsMP4Muxing: true })
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
                    if (subtitleStream.CodecName is "mov_text" or "dvd_subtitle" || !Options.TryPreserveUnrecognizedStreams)
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
                    "-copy_unknown", "-xerror", "-hide_banner", "-y", "-nostdin",
                    "-f", "mp4",
                    NullDevicePath,
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
                        "-copy_unknown", "-xerror", "-hide_banner", "-y", "-nostdin",
                        "-f", "mp4",
                        NullDevicePath,
                    ];

                    // Run the test command:
                    (_, _, returnCode) = await ProcessUtils.RunProcessToStringAsync(
                        FFmpegExePath,
                        testCommand,
                        cancellationToken: context.CancellationToken)
                    .ConfigureAwait(false);
                    isCompatibleSubtitleStreamAfterReencodingToMovText[i] = returnCode == 0;

                    // If it was still incompatible, try dvd_subtitle instead:
                    if (!isCompatibleSubtitleStreamAfterReencodingToMovText[i])
                    {
                        // Set up command to test copying this stream:
                        testCommand =
                        [
                            "-i", sourceFileWithCorrectExtension.PathExport,
                            "-map", string.Create(CultureInfo.InvariantCulture, $"0:{i}"),
                            "-c", "dvd_subtitle",
                            "-t", "1", // Only need a short segment to test (1 second)
                            "-copy_unknown", "-xerror", "-hide_banner", "-y", "-nostdin",
                            "-f", "mp4",
                            NullDevicePath,
                        ];

                        // Run the test command:
                        (_, _, returnCode) = await ProcessUtils.RunProcessToStringAsync(
                            FFmpegExePath,
                            testCommand,
                            cancellationToken: context.CancellationToken)
                        .ConfigureAwait(false);
                        isCompatibleSubtitleStreamAfterReencodingToDvdSubtitle[i] = returnCode == 0;
                    }
                }

                // If still incompatible, it is possible it's actually caused by an invalid stream - so check that now:
                if (Options.ForceValidateAllStreams &&
                    !isCompatibleStream[i] &&
                    !isCompatibleSubtitleStreamAfterReencodingToMovText[i] &&
                    !isCompatibleSubtitleStreamAfterReencodingToDvdSubtitle[i] &&
                    !validatedStreams[i])
                {
                    await ValidateStreamsAsync(
                        maxLength: null,
                        expectedMaxDuration: maxDuration,
                        mappedInputIndicesOrdered: [i],
                        mappedStreams: null,
                        countDuration: false).ConfigureAwait(false);
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
        List<int> streamsToValidateSeparatelyCounted = [];
        List<int> streamsToValidateSeparatelyUncounted = [];
        List<int> streamsValidatedImplicitly = [];
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
            context.CancellationToken.ThrowIfCancellationRequested();
            inputStreamIndex++;
            if (stream is FFprobeUtils.VideoStreamInfo videoStream)
            {
                inputVideoStreamIndex++;
                int id = outputVideoStreamIndex;
                int overallId = outputStreamIndex;

                // Handle thumbnail streams:
                bool isThumbnail = videoStream.IsAttachedPic || videoStream.IsTimedThumbnails;
                if (isThumbnail)
                {
                    bool map = Options.MetadataStrippingMode == VideoMetadataStrippingMode.None;
                    if (map)
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

                    if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                        streamsToValidateSeparatelyUncounted.Add(inputStreamIndex);

                    continue;
                }

                // Map the stream:
                outputVideoStreamIndex++;
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: 'v',
                        streamIndexWithinKind: inputVideoStreamIndex,
                        outputIndex: id));

                // Set up shared variables for this stream:
                VideoCodec? videoCodec = MatchVideoCodecByName(Options.ResultVideoCodecs, videoStream.CodecName, videoStream.CodecTagString);
                if (videoCodec is null && videoStream.CodecName == "hevc" && Options.ResultVideoCodecs.Contains(VideoCodec.H265))
                    videoCodec = VideoCodec.H265;
                bool isRequiredReencode = videoCodec is null;
                bool requiresReencodeForMP4FromSize = false;
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

                // Note: MP4 supports only up to 65535x65535, so check if we exceed that here as it will require re-encoding with resizing, but doesn't cause
                // it for the purposes of SelectSmallest.
                if (videoStream.Width > 65535 || videoStream.Height > 65535)
                {
                    reencode = true;
                    requiresReencodeForMP4FromSize = true;
                }

                // Check for square pixels:
                if (Options.ForceSquarePixels && HasNonSquarePixels(videoStream.SarNum, videoStream.SarDen))
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
                    int videoSubsampling = NormalizeChromaSubsampling(pixFormatInfo?.ChromaSubsampling);
                    int maxSubsampling = GetChromaSubsampling(Options.MaximumChromaSubsampling) ?? 444;
                    int videoBitsPerSample = NormalizeBitsPerSample(bitsPerSample);
                    int maxBitsPerSample = GetBitsPerChannel(Options.MaximumBitsPerChannel) ?? 12;
                    if (Options.ResultVideoCodecs[0] == VideoCodec.H264) maxBitsPerSample = int.Min(maxBitsPerSample, 10);
                    finalBitsPerChannel = int.Min(videoBitsPerSample, maxBitsPerSample);
                    finalChromaSubsampling = int.Min(videoSubsampling, maxSubsampling);
                    string pixFormat = GetPixelFormatString(finalChromaSubsampling, finalBitsPerChannel);

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
                    if (isHdr && Options.RemapHDRToSDR) isRequiredReencode = true;

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
                var (roundW, roundH) = GetChromaSubsamplingRounding(chromaSubsampling);

                // Check that we have a valid size & fps:
                if (reencode)
                {
                    // Check that our fps is parseable (i.e., not above int.MaxValue):
                    if (fpsOverride is { FPSNum: > int.MaxValue } or { FPSDen: > int.MaxValue })
                    {
                        long resultFpsNum = fpsOverride.FPSNum;
                        long resultFpsDen = fpsOverride.FPSDen;
                        int divisionFactor = checked((int)long.Max((resultFpsNum + (int.MaxValue - 1))
                            / int.MaxValue, (resultFpsDen + (int.MaxValue - 2)) / (int.MaxValue - 1)));
                        resultFpsNum = long.Max(resultFpsNum / divisionFactor, 1);
                        resultFpsDen = long.Max(resultFpsDen / divisionFactor, 1);
                        Debug.Assert(
                            resultFpsNum <= int.MaxValue && resultFpsDen <= int.MaxValue,
                            $"Failed to reduce max fps num and den to <= int.MaxValue ({resultFpsNum}, {resultFpsDen}, {divisionFactor}).");
                        int gcd = (int)BigInteger.GreatestCommonDivisor(resultFpsNum, resultFpsDen);
                        resultFpsNum /= gcd;
                        resultFpsDen /= gcd;
                        filterOverride!.FPS = (resultFpsNum, resultFpsDen);
                        fpsOverride.FPSNum = resultFpsNum;
                        fpsOverride.FPSDen = resultFpsDen;
                    }

                    // Determine the resulting size of the video:
                    var fpsValue = fpsOverride is null
                        ? (videoStream.FpsNum > 0 && videoStream.FpsDen > 0 ? (videoStream.FpsNum, videoStream.FpsDen) : null)
                        : ((int, int)?)((int)fpsOverride.FPSNum, (int)fpsOverride.FPSDen);
                    var (mode, resultWidth, resultHeight) = CalculateVideoResize(
                        videoStream.Width,
                        videoStream.Height,
                        Options.ForceSquarePixels ? videoStream.SarNum : -1,
                        Options.ForceSquarePixels ? videoStream.SarDen : -1,
                        Options.ResizeOptions is null ? null : (Options.ResizeOptions.Width, Options.ResizeOptions.Height),
                        fpsValue,
                        roundW,
                        roundH,
                        Options.ResultVideoCodecs[0] != VideoCodec.H264,
                        context.CancellationToken);

                    // If this file is 'requiresReencodeForMP4FromSize' and we got an exception from resizing, then our only remaining option is to return the
                    // file as-is if possible:
                    if (requiresReencodeForMP4FromSize && mode != 0 && !remuxGuaranteedRequired)
                    {
                        remuxRequired = false;
                        goto returnOriginal;
                    }

                    // If it's impossible to resize to this, and we're in the "if smaller" mode & we just bail out of re-encoding for this stream:
                    if (mode != 0 && !isRequiredReencode && !requiresReencodeForMP4FromSize && Options.VideoReencodeMode == StreamReencodeMode.SelectSmallest)
                    {
                        reencode = false;
                        perOutputStreamOverrides.RemoveRange(
                            outputStreamOverridesInitialCount,
                            perOutputStreamOverrides.Count - outputStreamOverridesInitialCount);
                    }

                    // Continue only if we're still re-encoding:
                    if (reencode)
                    {
                        if (mode == 1)
                        {
                            throw new FileProcessingException("Cannot re-encode video to fit within specified dimensions.");
                        }
                        else if (mode == 2)
                        {
                            throw new FileProcessingException("Cannot re-encode very large video to fit within codec maximum pixel count.");
                        }

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
                    }
                }

                // Keep track if we want to check the size later:
                if (!isRequiredReencode && reencode && Options.VideoReencodeMode == StreamReencodeMode.SelectSmallest)
                {
                    streamsToCheckSize.Add((
                        StreamMappingIndex: streamMapping.Count,
                        SourceValidFileExtension: requiresReencodeForMP4FromSize
                            ? sourceFileWithCorrectExtension.Extension
                            : videoCodec!.WritableFileExtension,
                        RequiresReencodeForMP4: requiresReencodeForMP4FromSize || !isCompatibleStream[inputStreamIndex]));
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

                // If we have a filter, ensure our pixel shape mode matches our intent:
                if (filterOverride is { MakePixelsSquareMode: < 2 })
                {
                    // Mode 0 means "set to 1:1", which we want to do if we're potentially resizing & aren't setting elsewhere; and mode 1 means "automatically
                    // adjust", which we want to do if we're not converting to square pixels.
                    filterOverride.MakePixelsSquareMode = Options.ForceSquarePixels ? 0 : 1;
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

                        string profile = GetH264Profile(finalBitsPerChannel, finalChromaSubsampling);
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'v', streamIndexWithinKind: id, profile: profile));
                    }
                    else if ((Options.ResultVideoCodecs[0] == VideoCodec.HEVC || Options.ResultVideoCodecs[0] == VideoCodec.HEVCAnyTag)
                        && FFprobeUtils.Configuration.SupportsLibX265Encoder)
                    {
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: id, codec: "libx265"));

                        int crf = GetH265CRF(Options.VideoQuality);
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCRFOverride(streamKind: 'v', streamIndexWithinKind: id, crf: crf));

                        string preset = GetVideoCompressionPreset(Options.VideoCompressionLevel);
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamPresetOverride(streamKind: 'v', streamIndexWithinKind: id, preset: preset));

                        string profile = GetH265Profile(finalBitsPerChannel, finalChromaSubsampling);
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'v', streamIndexWithinKind: id, profile: profile));

                        // Make the file more compatible by using 'hvc1' tag:
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamTagOverride(streamKind: 'v', streamIndexWithinKind: id, tag: "hvc1"));
                    }
                    else
                    {
                        // Note: we use Debug.Fail here since we should have already validated this earlier, so this just safeguards so we catch issues testing
                        Debug.Fail("The requested video codec did not have a supported encoder available in the configured FFmpeg build (unexpected).");
                    }
                }
                else if (videoCodec == VideoCodec.H265 && videoStream.CodecTagString != "hvc1")
                {
                    // Even if we're copying, if it's HEVC and not 'hvc1' tagged, we want to change the tag to 'hvc1' for better compatibility:
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamTagOverride(streamKind: 'v', streamIndexWithinKind: id, tag: "hvc1"));
                }

                // If we're not copying metadata, we want to manually add the metadata we want to preserve back in after normalizing it:
                FFmpegUtils.PerStreamMetadataOverride? metadataOverrides = null;
                if (!mapMetadata)
                {
                    metadataOverrides = new FFmpegUtils.PerStreamMetadataOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: overallId)
                    {
                        Language = GetNormalizedLanguage(videoStream.Language),
                    };
                    perOutputStreamOverrides.Add(metadataOverrides);
                }

                // Final part of mapping the stream:
                streamMapping.Add((
                    Kind: 'v',
                    InputIndex: inputVideoStreamIndex,
                    OutputIndex: id,
                    MapMetadata: mapMetadata,
                    MetadataOverrides: metadataOverrides));
                if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                {
                    if (reencode) streamsValidatedImplicitly.Add(inputStreamIndex);
                    else streamsToValidateSeparatelyCounted.Add(inputStreamIndex);
                }
            }
            else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
            {
                // If we're removing audio streams, exclude it now:
                inputAudioStreamIndex++;
                if (Options.RemoveAudioStreams)
                {
                    if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                        streamsToValidateSeparatelyUncounted.Add(inputStreamIndex);

                    continue;
                }

                // Map the stream:
                int id = outputAudioStreamIndex;
                int overallId = outputStreamIndex;
                outputAudioStreamIndex++;
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);

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
                        StreamMappingIndex: streamMapping.Count,
                        SourceValidFileExtension: audioCodec!.WritableFileExtension,
                        RequiresReencodeForMP4: !isCompatibleStream[inputStreamIndex]));
                }

                // Set up codec to use (note - currently the only supported codec is AAC-LC, so we aren't checking which one the user selected here currently):
                if (reencode)
                {
                    if (
#if DEBUG
                        Options.ForceLibFDKAACUsage ??
#else
#pragma warning disable SA1119 // Statement should not use unnecessary parenthesis
#endif
                        (FFprobeUtils.Configuration.SupportsLibFDKAACEncoder && (audioStream.Channels <= 8 || Options.MaxChannels != AudioChannels.Preserve)))
#if !DEBUG
#pragma warning restore SA1119 // Statement should not use unnecessary parenthesis
#endif
                    {
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "libfdk_aac"));

                        int quality = GetLibFDKAACEncoderQuality(Options.AudioQuality);
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'a', streamIndexWithinKind: id, profile: "aac_low"));
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamVBROverride(streamKind: 'a', streamIndexWithinKind: id, vbr: quality));

                        // For highest & high quality, ensure we use the maximum supported cutoff frequency (20kHz):
                        if (quality >= 4)
                            perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCutoffOverride(streamKind: 'a', streamIndexWithinKind: id, cutoff: 20000));
                    }
                    else if (
#if DEBUG
#pragma warning disable SA1003 // Symbols should be spaced correctly
                        !Options.ForceLibFDKAACUsage ??
#pragma warning restore SA1003 // Symbols should be spaced correctly
#endif
                        FFprobeUtils.Configuration.SupportsAACEncoder)
                    {
                        // Note: we can end up using the native aac encoder even if libfdk_aac is available if there are more than 8 channels & we're trying to
                        // preserve them. We do not try to handle additionally here, as we might have to specify a channel layout, etc., if we have more than
                        // the maximum number of channels than is supported by aac (which is 16 + 16 + 16), and we should get an exception if we try to do
                        // something unsupported in this edge-edge case. Also, if the layout is not specified in the input, it will likely still fail here, but
                        // we know it will for sure fail with libfdk_aac anyway, so this is still an improvement in some cases.

                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "aac"));
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'a', streamIndexWithinKind: id, profile: "aac_low"));

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
                int currentSampleRate = sampleRateOverride?.SampleRate ?? audioStream.SampleRate ?? -1;
                if (reencode && currentSampleRate > 0 && !IsValidAACSampleRate(currentSampleRate))
                {
                    if (sampleRateOverride is null)
                    {
                        sampleRateOverride =
                            new FFmpegUtils.PerStreamSampleRateOverride(streamKind: 'a', streamIndexWithinKind: id, sampleRate: 0);
                        perOutputStreamOverrides.Add(sampleRateOverride);
                    }

                    // Find the next highest supported sample rate:
                    sampleRateOverride.SampleRate = NormalizeToValidAACSampleRate(currentSampleRate);
                }

                // If we're not copying metadata, we want to manually add the metadata we want to preserve back in after normalizing it:
                FFmpegUtils.PerStreamMetadataOverride? metadataOverrides = null;
                if (!mapMetadata)
                {
                    metadataOverrides = new FFmpegUtils.PerStreamMetadataOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: overallId)
                    {
                        Language = GetNormalizedLanguage(audioStream.Language),
                    };
                    perOutputStreamOverrides.Add(metadataOverrides);
                }

                // Final part of mapping the stream:
                streamMapping.Add((
                    Kind: 'a',
                    InputIndex: inputAudioStreamIndex,
                    OutputIndex: id,
                    MapMetadata: mapMetadata,
                    MetadataOverrides: metadataOverrides));
                if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                {
                    if (reencode) streamsValidatedImplicitly.Add(inputStreamIndex);
                    else streamsToValidateSeparatelyCounted.Add(inputStreamIndex);
                }
            }
            else if (stream is FFprobeUtils.SubtitleStreamInfo subtitleStream)
            {
                // If we're not preserving unrecognized streams, skip it (we already marked as excluded by default):
                if (!Options.TryPreserveUnrecognizedStreams)
                {
                    if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                        streamsToValidateSeparatelyUncounted.Add(inputStreamIndex);

                    continue;
                }

                // If it's impossible to preserve the subtitle stream in MP4, skip it:
                if (!isCompatibleStream[inputStreamIndex] &&
                    !isCompatibleSubtitleStreamAfterReencodingToMovText[inputStreamIndex] &&
                    !isCompatibleSubtitleStreamAfterReencodingToDvdSubtitle[inputStreamIndex])
                {
                    perInputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamMapOverride(
                            fileIndex: 0,
                            streamKind: '\0',
                            streamIndexWithinKind: inputStreamIndex,
                            mapToOutput: false));

                    if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                        streamsToValidateSeparatelyUncounted.Add(inputStreamIndex);

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
                string codec = !isCompatibleStream[inputStreamIndex]
                    ? (isCompatibleSubtitleStreamAfterReencodingToMovText[inputStreamIndex] ? "mov_text" : "dvd_subtitle")
                    : "copy";
                perOutputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamCodecOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: id,
                        codec: codec));

                // If we're not copying metadata, we want to manually add the metadata we want to preserve back in after normalizing it:
                // Note: mp4 does not support title metadata for the subtitle stream (it does support language though, so we preserve that).
                FFmpegUtils.PerStreamMetadataOverride? metadataOverrides = null;
                if (!mapMetadata)
                {
                    metadataOverrides = new FFmpegUtils.PerStreamMetadataOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: id)
                    {
                        Language = GetNormalizedLanguage(subtitleStream.Language),
                    };
                    perOutputStreamOverrides.Add(metadataOverrides);
                }

                // Final part of mapping the stream:
                streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: metadataOverrides));
                if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                {
                    if (codec != "copy") streamsValidatedImplicitly.Add(inputStreamIndex);
                    else streamsToValidateSeparatelyCounted.Add(inputStreamIndex);
                }
            }
            else if (stream is FFprobeUtils.UnrecognizedStreamInfo unrecognizedStream)
            {
                // Handle thumbnail streams:
                bool isThumbnail = IsThumbnailStream(unrecognizedStream);
                int id = outputStreamIndex;
                if (isThumbnail)
                {
                    bool map = Options.MetadataStrippingMode == VideoMetadataStrippingMode.None && isCompatibleStream[inputStreamIndex];
                    if (map)
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

                    if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                        streamsToValidateSeparatelyUncounted.Add(inputStreamIndex);

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

                    if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                        streamsToValidateSeparatelyUncounted.Add(inputStreamIndex);

                    continue;
                }

                // Map the stream:
                outputStreamIndex++;
                bool mapMetadata = Options.MetadataStrippingMode is not (VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred);
                if (Options.ForceValidateAllStreams && !validatedStreams[inputStreamIndex])
                    streamsToValidateSeparatelyUncounted.Add(inputStreamIndex);

                // Specify whether we want to map the metadata:
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapMetadataOverride(
                        fileIndex: mapMetadata ? 0 : -1,
                        streamKind: '\0',
                        streamIndexWithinKind: inputStreamIndex,
                        outputIndex: id));

                // If we're not copying metadata, we want to manually add the metadata we want to preserve back in after normalizing it:
                FFmpegUtils.PerStreamMetadataOverride? metadataOverrides = null;
                if (!mapMetadata)
                {
                    metadataOverrides = new FFmpegUtils.PerStreamMetadataOverride(
                        streamKind: '\0',
                        streamIndexWithinKind: id)
                    {
                        Language = GetNormalizedLanguage(unrecognizedStream.Language),
                    };
                    perOutputStreamOverrides.Add(metadataOverrides);
                }

                // Final part of mapping the stream:
                streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: mapMetadata, MetadataOverrides: metadataOverrides));
            }
        }

        // If there's validation to perform, do it now:

        if (streamsToValidateSeparatelyCounted.Count > 0)
        {
            await ValidateStreamsAsync(
                maxLength: null,
                maxDuration,
                [.. streamsToValidateSeparatelyCounted],
                mappedStreams: null,
                countDuration: true)
            .ConfigureAwait(false);
        }

        if (streamsToValidateSeparatelyUncounted.Count > 0)
        {
            await ValidateStreamsAsync(
                maxLength: null,
                maxDuration,
                [.. streamsToValidateSeparatelyUncounted],
                mappedStreams: null,
                countDuration: false)
            .ConfigureAwait(false);
        }

        streamsValidated += streamsValidatedImplicitly.Count;
        foreach (int idx in streamsValidatedImplicitly)
        {
            validatedStreams[idx] = true;
            var stream = sourceInfo.Streams[idx];
            double? duration = (stream as FFprobeUtils.VideoStreamInfo)?.Duration ?? (stream as FFprobeUtils.AudioStreamInfo)?.Duration ?? sourceInfo.Duration;
            if (duration is { })
            {
                maxMeasuredDuration = maxMeasuredDuration is { } ? double.Max(maxMeasuredDuration.Value, duration.Value) : duration.Value;
            }
        }

        FinishedValidation();

        // Finish setting up & running the main FFmpeg command:
        var resultTempFile = context.GetNewWorkFile(".mp4"); // currently this is our only supported output format when remuxing, so just use it
        resultTempFile.ParentDirectory.Create();
        FFmpegUtils.FFmpegCommand command = new(
            inputFiles: [(File: sourceFileWithCorrectExtension, Seek: null)],
            outputFile: resultTempFile,
            perInputStreamOverrides: [.. perInputStreamOverrides],
            perOutputStreamOverrides: [.. perOutputStreamOverrides],
            mapChaptersFrom: Options.MetadataStrippingMode is VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred ? -1 : 0,
            forceProgressiveDownloadSupport: Options.ForceProgressiveDownload,
            isToMov: true);

        // Run the command
        // Note: The last 5% of progress is reserved for the "checking if smaller" pass & since the progress reported is the highest timestamp completed of any
        // stream, so we want to leave some headroom.
        if (Options.ProgressCallback != null && maxDuration != 0.0) progressTempFile ??= context.GetNewWorkFile(".txt");
        double lastDone = 0.0;
        double mostRecentClampedProgress = 0.0;
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
            mostRecentClampedProgress = clampedProgress;
            await Options.ProgressCallback!((context.FileId, context.VariantId), clampedProgress).ConfigureAwait(false);
        } : null;
        try
        {
            try
            {
                await FFmpegUtils.RunFFmpegCommandAsync(
                    command,
                    localProgressCallback,
                    localProgressCallback != null ? progressTempFile : null,
                    context.CancellationToken)
                .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && Options.ForceValidateAllStreams && streamsValidatedImplicitly.Count > 0)
            {
                // If we got an exception & we were meant to validate streams, we need to quickly check if this should have been a validation failure instead.
                // This way we optimise the common case of no validation errors, while still executing as if we checked beforehand.
                context.CancellationToken.ThrowIfCancellationRequested();
                Func<(FileId FileId, string? VariantId), double, ValueTask>? progressCallback = Options.ProgressCallback is not null
                    ? async ((FileId FileId, string? VariantId) file, double fraction) =>
                        await Options.ProgressCallback(file, mostRecentClampedProgress + (fraction * (1.0 - mostRecentClampedProgress))).ConfigureAwait(false)
                    : null;
                await FullyValidateStreamsAsync(
                    context,
                    sourceFileWithCorrectExtension,
                    sourceInfo,
                    progressCallback,
                    maxLength: null,
                    maxDuration,
                    (i) => streamsValidatedImplicitly.BinarySearch(i) switch { < 0 => null, { } idx => idx },
                    [.. streamsValidatedImplicitly.Select((x) => new FFmpegUtils.PerStreamMapOverride(
                        fileIndex: 0,
                        streamKind: '\0',
                        streamIndexWithinKind: x,
                        mapToOutput: true))],
                    mappedStreamCount: streamsValidatedImplicitly.Count,
                    progressUsed: progressUsed,
                    totalStreamCount: streamsValidatedImplicitly.Count,
                    progressTempFile,
                    reportDuration: false,
                    context.CancellationToken).ConfigureAwait(false);

                // If we got here, it was not a validation failure, re-throw the original exception:
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileProcessingException("Failed to perform the main video reencoding / remuxing pass.", ex);
        }

        // For any streams that are in a "if smaller than" mode, we want to do additional passes to see if it ended up smaller:
        if (streamsToCheckSize.Count > 0)
        {
            // Determine which are actually smaller & which are actually smaller, but would still need to be re-encoded if remuxed:
            List<(int Index, bool NeedsReencode, bool StrictlySmaller)> wasSmaller = [];
            var reencodedTempFile = context.GetNewWorkFile(".mp4");
            reencodedTempFile.ParentDirectory.Create();
            var fromOriginalTempFileMp4 = context.GetNewWorkFile(".mp4");
            fromOriginalTempFileMp4.ParentDirectory.Create();
            bool mustRemux = remuxGuaranteedRequired;
            for (int i = 0; i < streamsToCheckSize.Count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
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
                    inputFiles: [(File: resultTempFile, Seek: null)],
                    outputFile: reencodedTempFile,
                    perInputStreamOverrides:
                    [
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
                    forceProgressiveDownloadSupport: false,
                    isToMov: true);
                try
                {
                    await FFmpegUtils.RunFFmpegCommandAsync(extractCommandReencoded, null, null, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new FileProcessingException("Failed to determine the approximate size of the reencoded stream.", ex);
                }

                long reencodedApproxSize = reencodedTempFile.Length;
                reencodedTempFile.Delete();

                // Now do the same for the original file:
                var fromOriginalTempFile = streamToCheck.SourceValidFileExtension != ".mp4"
                    ? context.GetNewWorkFile(streamToCheck.SourceValidFileExtension)
                    : fromOriginalTempFileMp4;
                fromOriginalTempFile.ParentDirectory.Create();
                FFmpegUtils.FFmpegCommand extractCommandOriginal = new(
                    inputFiles: [(File: sourceFileWithCorrectExtension, Seek: null)],
                    outputFile: fromOriginalTempFile,
                    perInputStreamOverrides:
                    [
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
                    forceProgressiveDownloadSupport: false,
                    isToMov: true);
                try
                {
                    await FFmpegUtils.RunFFmpegCommandAsync(extractCommandOriginal, null, null, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new FileProcessingException("Failed to determine the approximate size of the original stream.", ex);
                }

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
                newResultTempFile.ParentDirectory.Create();

                // Prepare the command:
                perInputStreamOverrides.Clear();
                perOutputStreamOverrides.Clear();
                int wasSmallerIdx = 0;
                var nextWasSmaller = (Value: streamsToCheckSize[wasSmaller[0].Index], wasSmaller[0].NeedsReencode, wasSmaller[0].StrictlySmaller);
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
                    context.CancellationToken.ThrowIfCancellationRequested();

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
                    inputFiles: [(File: sourceFileWithCorrectExtension, Seek: null), (File: resultTempFile, Seek: null)],
                    outputFile: newResultTempFile,
                    perInputStreamOverrides: [.. perInputStreamOverrides],
                    perOutputStreamOverrides: [.. perOutputStreamOverrides],
                    mapChaptersFrom: Options.MetadataStrippingMode is VideoMetadataStrippingMode.Required or VideoMetadataStrippingMode.Preferred ? -1 : 0,
                    forceProgressiveDownloadSupport: Options.ForceProgressiveDownload,
                    isToMov: true);

                // Run the command
                // Note: The last 2% of remaining available progress is reserved since the progress reported is the highest timestamp completed of any stream,
                // so we want to leave some headroom.
                const double ReservedProgressInner = 0.02;
                lastDone = 0.0;
                Func<double, ValueTask>? localProgressCallbackInner = Options.ProgressCallback != null && maxDuration != 0.0 ? async (durationDone) =>
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
                if (localProgressCallbackInner is not null) progressTempFile ??= context.GetNewWorkFile(".txt");
                try
                {
                    await FFmpegUtils.RunFFmpegCommandAsync(
                        mixCommand,
                        localProgressCallbackInner,
                        localProgressCallbackInner != null ? progressTempFile : null,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new FileProcessingException("Failed to combine original and reencoded streams.", ex);
                }

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
    internal static bool IsKnownSDRColorProfile(string? colorTransfer, string? colorPrimaries, string? colorSpace) =>
        (colorTransfer is null or "bt709" or "bt601" or "bt470" or "bt470m" or "bt470bg" or "smpte170m" or "smpte240m" or "iec61966-2-1") &&
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
        VideoQuality.Highest => 16,
        VideoQuality.High => 19,
        VideoQuality.Medium => 22,
        VideoQuality.Low => 27,
        VideoQuality.Lowest => 31,
        _ => throw new UnreachableException("Unrecognized VideoQuality value."),
    };

    private static int GetH265CRF(VideoQuality quality) => quality switch
    {
        VideoQuality.Highest => 14,
        VideoQuality.High => 17,
        VideoQuality.Medium => 20,
        VideoQuality.Low => 26,
        VideoQuality.Lowest => 30,
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
        AudioQuality.Highest => 112_000,
        AudioQuality.High => 88_000,
        AudioQuality.Medium => 64_000,
        AudioQuality.Low => 52_000,
        AudioQuality.Lowest => 40_000,
        _ => throw new UnreachableException("Unrecognized AudioQuality value."),
    };

    private static VideoCodec? MatchVideoCodecByName(IEnumerable<VideoCodec> options, string? codecName, string codecTagString)
        => options.FirstOrDefault((x) => x.Name == codecName && (x.TagName == null || x.TagName == codecTagString));

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

    // Helper to share the logic for resizing a video.
    // Returned mode is one of 0 (success), 1 (cannot preserve both min & max), or 2 (cannot reduce pixel count w/o violating minimum dimensions).
    private static (int Mode, int ResultWidth, int ResultHeight) CalculateVideoResize(
        int streamWidth,
        int streamHeight,
        int streamSarNum,
        int streamSarDen,
        (int Width, int Height)? maxDimensions,
        (int Num, int Den)? fps,
        bool roundW,
        bool roundH,
        bool isH265,
        CancellationToken cancellationToken)
    {
        // Determine the maximum dimensions allowed, and the minimum dimension allowed, based on codec and requested max dimensions:
        int maxW = int.Min(maxDimensions?.Width ?? int.MaxValue, isH265 ? 65535 : 16384);
        int maxH = int.Min(maxDimensions?.Height ?? int.MaxValue, isH265 ? 65535 : 16384);
        int minDimension = isH265 ? 16 : 2;

        // Determine the resulting size of the video:
        int resultWidth, resultHeight;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resize to square pixels if wanted:

            if (streamSarNum <= 0 || streamSarDen <= 0)
            {
                streamSarNum = 1;
                streamSarDen = 1;
            }

            double w0 = streamWidth, h0 = streamHeight;
            if (streamSarNum != 1 || streamSarDen != 1)
            {
                if (streamSarNum > streamSarDen)
                {
                    w0 *= (double)streamSarNum / streamSarDen;
                }
                else
                {
                    h0 *= (double)streamSarDen / streamSarNum;
                }
            }

            // Apply min & max scaling if any:
            {
                // Declare variables:
                double w1, w2, h1, h2;

                // Apply max:
                if (w0 > maxW || h0 > maxH)
                {
                    w1 = double.Min(maxW, (double)w0 / h0 * maxH);
                    h1 = double.Min(maxH, (double)h0 / w0 * maxW);
                }
                else
                {
                    w1 = w0;
                    h1 = h0;
                }

                // Apply min:
                if (w1 < minDimension || h1 < minDimension)
                {
                    w2 = double.Max(minDimension, w1 / h1 * minDimension);
                    h2 = double.Max(minDimension, h1 / w1 * minDimension);
                }
                else
                {
                    w2 = w1;
                    h2 = h1;
                }

                // Pre-round to 8dp, so that we are likely to round at midpoint correctly if we were to calculate exactly:
                // Note: the rounding is here to ensure that we do not end up with tiny fractions that cause us to round the wrong way later in the case of
                // what should end up nice values (e.g., 128x128 at 4:3 becoming 170.67x128, and then limiting to 100x100 becoming 100x75, which should round
                // to 100x76, not 100x74).
                w2 = double.Round(w2, 8);
                h2 = double.Round(h2, 8);

                // Round as required:
                double w3 = double.Round(w2 / (roundW ? 2 : 1)) * (roundW ? 2 : 1);
                double h3 = double.Round(h2 / (roundH ? 2 : 1)) * (roundH ? 2 : 1);
                if (w3 > maxW) w3 = double.Floor(w2 / (roundW ? 2 : 1)) * (roundW ? 2 : 1);
                if (h3 > maxH) h3 = double.Floor(h2 / (roundH ? 2 : 1)) * (roundH ? 2 : 1);

                // Store final size:
                resultWidth = int.CreateSaturating(w3);
                resultHeight = int.CreateSaturating(h3);
            }

            // If we couldn't achieve both the min & max dimensions, throw an error if there's no way to fix it:
            if (resultWidth < minDimension || resultHeight < minDimension || resultWidth > maxW || resultHeight > maxH)
            {
                // Tell the caller to throw an error:
                return (1, -1, -1);
            }

            // The libx265 HEVC encoder supports a minimum size of 16 usually, as it can usually set the CTU size to 16x16, but for suitably large videos, it
            // needs to use 32x32 CTUs (when one dimension is at least 4217) due to requiring HEVC level >=5.0 which only support 32x32 or 64x64 by default
            // (without enabling non-conformant streams).
            // There is also the bandwidth limit of 133,693,440 px per second for level 4.1 (libx265 implements it on displayed pixels, not coded pixels), so
            // we check that we haven't exceeded that either. Note: ffmpeg rounds the size up to the nearest multiple of 8 or 16 before being passed to x265 to
            // be used for level calculations; so we do the same here (assuming 16 to be safe).
            bool newMin = false;
            if (isH265 && (resultWidth >= 4217 || resultHeight >= 4217) && (minDimension < 32))
            {
                minDimension = 32;
                newMin = true;
            }
            else if (isH265 && fps is var (fpsNum, fpsDen)
                && uint.CreateSaturating(((resultWidth + 15) & ~15) * (long)((resultHeight + 15) & ~15) * ((double)fpsNum / fpsDen)) > 133_693_440)
            {
                minDimension = 32;
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
            // Note: we round width & height up to the nearest multiples of 64 (or 16 for height with H.264), since that's how wide AVX-512 is which is the
            // largest possible rounding up that might be required to decode the video we make with ffmpeg successfully, and it's also the largest size that
            // HEVC can take for CTU block size - this allows us to not have to worry about these cases having an issue when trying to decode the image, as it
            // gets decoded in the "coded size", which is based on block / CTU sizes, and the row is also padded in some places for vectorization.
            long bytesRequiredForPixelsDiv8 = (((resultWidth + 63L) & ~63) + 128L) * (((resultHeight + (isH265 ? 63L : 15L)) & ~(isH265 ? 63 : 15)) + 128L);
            if (bytesRequiredForPixelsDiv8 > int.MaxValue / 8)
            {
                // If one dimension is equal to the min, we can't reduce further, so throw an error:
                if (maxW == minDimension || maxH == minDimension)
                {
                    // We only fail if we've tried all of the smaller sizes:
                    Debug.Fail("If you found a way to trigger this case that isn't a bug, please add a unit test for it to test its handling.");
                    return (2, -1, -1);
                }

                // Set max dimensions to current size first
                maxW = resultWidth;
                maxH = resultHeight;

                // Now, reduce the max dimensions to what should be correct approximately, and ensure at least 1 dimension is reduced:
                double scaleFactor = double.Sqrt(int.MaxValue / 8.0 / bytesRequiredForPixelsDiv8);
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

    private static string GetPixelFormatString(int chromaSubsampling, int bitsPerChannel) => (chromaSubsampling, bitsPerChannel) switch
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

    private static string GetH264Profile(int bitsPerChannel, int chromaSubsampling) => (bitsPerChannel, chromaSubsampling) switch
    {
        (8, 420) => "high",
        (8, 422) => "high422",
        (8, 444) => "high444",
        (10, 420) => "high10",
        (10, 422) => "high422",
        (10, 444) => "high444",
        _ => throw new UnreachableException("Unimplemented H.264 profile for video encoding."),
    };

    private static string GetH265Profile(int bitsPerChannel, int chromaSubsampling) => (bitsPerChannel, chromaSubsampling) switch
    {
        (8, 420) => "main",
        (8, 422) => "main422-10",
        (8, 444) => "main444-8",
        (10, 420) => "main10",
        (10, 422) => "main422-10",
        (10, 444) => "main444-10",
        (12, 420) => "main12",
        (12, 422) => "main422-12",
        (12, 444) => "main444-12",
        _ => throw new UnreachableException("Unimplemented HEVC profile for video encoding."),
    };

    private static int NormalizeToValidAACSampleRate(int sampleRate) => sampleRate switch
    {
        > 88200 => 96000,
        > 64000 => 88200,
        > 48000 => 64000,
        > 44100 => 48000,
        > 32000 => 44100,
        > 24000 => 32000,
        > 22050 => 24000,
        > 16000 => 22050,
        > 12000 => 16000,
        > 11025 => 12000,
        > 8000 => 11025,
        _ => 8000,
    };

    private static bool IsValidAACSampleRate(int sampleRate)
        => sampleRate is 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 64000 or 88200 or 96000;

    private static int NormalizeBitsPerSample(int bitsPerSample) => bitsPerSample switch
    {
        <= 8 => 8,
        <= 10 => 10,
        <= 12 => 12,
        _ => 12,
    };

    private static int NormalizeChromaSubsampling(int? chromaSubsampling) => chromaSubsampling switch
    {
        440 => 444, // Normalize 4:4:0 to 4:4:4
        int x => x,
        _ => 444,
    };

    private static bool IsThumbnailStream(FFprobeUtils.VideoStreamInfo videoStream)
        => videoStream.IsAttachedPic || videoStream.IsTimedThumbnails;

    private static bool IsThumbnailStream(FFprobeUtils.UnrecognizedStreamInfo stream)
        => stream.IsAttachedPic || stream.IsTimedThumbnails;

    internal static bool HasNonSquarePixels(int sarNum, int sarDen)
        => (sarNum, sarDen) is not ((<= 0, <= 0) or (1, 1));

    private static (bool RoundWidth, bool RoundHeight) GetChromaSubsamplingRounding(int chromaSubsampling)
        => chromaSubsampling switch
        {
            420 => (true, true),
            422 => (true, false),
            444 => (false, false),
            _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
        };

    private static string? GetNormalizedLanguage(string? language)
        => (language != "und" && IsValidLanguage(language)) ? language : null;

    private static string NullDevicePath => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    // This helper implements the logic for the ForceValidateAllStreams check. We seperate it out into a helper so we can call it more cleverly.
    // In particular: since we expect it to be an edge case where it actually fails, we try to avoid doing it as much as possible, and try to do it as late as
    // possible. For when validating stream length throroughly, we will call it early still, but otherwise we rely on the main processing pass to receive an
    // exception also in the case that this would for streams that it re-encodes, and for streams that it copies or skips, we call that just before the main
    // processing loop to give everything else a chance to fail or exit first. For the shortcut exits, we also just manually check remaining streams in those
    // just before exiting also. This way, we can reduce the overhead of this measurably expensive check in the common case where everything is valid.
    private const double ValidateProgressFraction = 0.20;
    private async ValueTask<(bool ExceededMaxLength, double? ActualMaxDuration, IAbsoluteFilePath? ProgressTempFile, double ProgressUsed)>
        FullyValidateStreamsAsync(
            FileProcessingContext context,
            IAbsoluteFilePath sourceFileWithCorrectExtension,
            FFprobeUtils.VideoFileInfo sourceInfo,
            Func<(FileId FileId, string? VariantId), double, ValueTask>? progressCallback,
            double? maxLength,
            double expectedMaxDuration,
            Func<int, int?> inputToOutputIndexMapper,
            IEnumerable<FFmpegUtils.PerStreamMapOverride> mappedStreams,
            int mappedStreamCount,
            double progressUsed,
            int totalStreamCount,
            IAbsoluteFilePath? progressTempFile,
            bool reportDuration,
            CancellationToken cancellationToken)
    {
        // First check if we're in a no-op case:
        Debug.Assert(totalStreamCount > 0, "Total stream count must be greater than zero.");
        if (mappedStreamCount == 0)
        {
            return (
                false,
                null,
                progressTempFile,
                progressUsed + ((double)mappedStreamCount / totalStreamCount * ValidateProgressFraction));
        }

        // Validate input by doing a decode-only ffmpeg run to ensure no decoding errors.
        // Note: when active, we set aside the first 20% of progress for this.
        // Note: we compare duration to 0.0, as that's the value meaning "unknown".

        // Run the validation pass:
        // Note: we cancel early if we exceed max video/audio lengths (if specified), to avoid unnecessary extra processing on an invalid (and potentially
        // malicious, since our original detected duration is just from metadata, so our actual duration could be substantially longer if malicious) input.
        // Note: while we have options to check duration, we currently have not implemented similar options for related things like ridiculous FPS, etc.
        double lastDone = 0.0;
        using CancellationTokenSource exitEarlyCts = new();
        CancellationToken exitEarlyCt = exitEarlyCts.Token;
        Func<double, ValueTask> validateProgressCallback = (reportDuration || maxLength is not null)
            ? async (durationDone) =>
            {
                // Avoid going backwards or repeating the same progress - but ensure we can still hit 100% of this section:
                if (durationDone <= lastDone) return;
                if (durationDone > expectedMaxDuration && lastDone < expectedMaxDuration) (durationDone, lastDone) = (expectedMaxDuration, durationDone);
                else lastDone = durationDone;
                if (maxLength.HasValue && lastDone > maxLength.Value) exitEarlyCts.Cancel();
                if (durationDone < 0.0 || durationDone > expectedMaxDuration || double.IsNaN(durationDone) || totalStreamCount == 0) return;
                if (maxLength is null) return;
                if (progressCallback is null) return;

                // Clamp / adjust to [0.0, 0.20 * 0.999] range (the 0.999 is to avoid hitting 100% so we can increase strictly):
                double durationFraction = durationDone / maxLength.Value;
                double maxDuration = mappedStreamCount / (double)totalStreamCount * ValidateProgressFraction * 0.999;
                double clampedProgress = double.Min(durationFraction * maxDuration, maxDuration);
                await progressCallback((context.FileId, context.VariantId), progressUsed + clampedProgress).ConfigureAwait(false);
            }
            : null;
        if (validateProgressCallback is not null && progressTempFile is null) progressTempFile = context.GetNewWorkFile(".txt");
        try
        {
            if (validateProgressCallback is not null)
            {
                progressTempFile!.ParentDirectory.Create();
                progressTempFile.Delete();
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, exitEarlyCt);
            try
            {
                await FFmpegUtils.RunRawFFmpegCommandAsync(
                    [
                        "-i",
                        sourceFileWithCorrectExtension.PathExport,
                        .. validateProgressCallback is null ? [] : (IEnumerable<string>)["-progress", progressTempFile!.PathExport],
                        "-ignore_unknown",
                        "-xerror",
                        "-hide_banner",
                        "-nostdin",
                        .. mappedStreams.Aggregate((List<string>)[], (x, y) =>
                        {
                            y.PrepareArguments(x);
                            return x;
                        }),
                        .. sourceInfo.Streams.Select((x, i) => (x, i)).SelectMany(x => (x.x, inputToOutputIndexMapper(x.i)) switch
                        {
                            (FFprobeUtils.SubtitleStreamInfo si, int idx) => (IEnumerable<string>)[
                                "-c:" + idx.ToString(CultureInfo.InvariantCulture),
                                si.CodecName ?? "copy",
                            ],
                            _ => [],
                        }),
                        "-f",
                        "null",
                        "-",
                    ],
                    validateProgressCallback,
                    validateProgressCallback is null ? null : progressTempFile,
                    ensureAllProgressRead: reportDuration,
                    cancellationToken: linkedCts.Token)
                .ConfigureAwait(false);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException && sourceInfo.Streams
                    .Select((x, i) => (Value: x, Index: i))
                    .Any((x) => x.Value is not FFprobeUtils.SubtitleStreamInfo && inputToOutputIndexMapper(x.Index) is not null))
            {
                // If we get an exception here, we should try again & specify codecs for each stream, as sometimes ffmpeg doesn't want to auto-select properly
                // (we don't do this by default since it's more expensive):
                // Note: we only do this if there is at least one non-subtitle stream, since we already handle those above.
                await FFmpegUtils.RunRawFFmpegCommandAsync(
                    [
                        "-i",
                        sourceFileWithCorrectExtension.PathExport,
                        .. validateProgressCallback is null ? [] : (IEnumerable<string>)["-progress", progressTempFile!.PathExport],
                        "-ignore_unknown",
                        "-xerror",
                        "-hide_banner",
                        "-nostdin",
                        .. mappedStreams.Aggregate((List<string>)[], (x, y) =>
                        {
                            y.PrepareArguments(x);
                            return x;
                        }),
                        "-c", "copy",
                        "-c:v", "rawvideo",
                        "-c:a", "pcm_s16le",
                        .. sourceInfo.Streams.Select((x, i) => (x, i)).SelectMany(x => (x.x, inputToOutputIndexMapper(x.i)) switch
                        {
                            (FFprobeUtils.SubtitleStreamInfo { CodecName: not null } si, int idx) => (IEnumerable<string>)[
                                "-c:" + idx.ToString(CultureInfo.InvariantCulture),
                                si.CodecName,
                            ],
                            _ => [],
                        }),
                        "-f",
                        "null",
                        "-",
                    ],
                    validateProgressCallback,
                    validateProgressCallback is null ? null : progressTempFile,
                    ensureAllProgressRead: reportDuration,
                    cancellationToken: linkedCts.Token)
                .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken != cancellationToken)
        {
            // Keep / re-throw exception if cancellation was requested by the caller only, discard if it was our early exit.
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(message: null, ex, cancellationToken);
        }
        catch (Exception ex)
        {
            // Rethrow as a file processing exception with a more specific message:
            throw new FileProcessingException("An error occurred while validating the source video streams.", ex);
        }

        // Update the progress used:
        progressUsed += (double)mappedStreamCount / totalStreamCount * ValidateProgressFraction;

        // Notify the progress callback now:
        // Note: we use double.BitDecrement, as we try to not hit values twice, and the new progressUsed value is reserved for the next informer.
        if (progressCallback is not null)
        {
            await progressCallback((context.FileId, context.VariantId), double.BitDecrement(progressUsed)).ConfigureAwait(false);
        }

        // Return the info from the operation:
        return (maxLength.HasValue && lastDone > maxLength.Value, lastDone != 0.0 && reportDuration ? lastDone : null, progressTempFile, progressUsed);
    }
}
