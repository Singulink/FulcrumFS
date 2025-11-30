using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Singulink.IO;
using Singulink.Threading;

namespace FulcrumFS.Videos;

/// <summary>
/// Provides functionality to process video files with specified options.
/// </summary>
public sealed class VideoProcessor : FileProcessor
{
    // Helper fields to support our copy constructor based on PropertyHelpers:
    private readonly VideoProcessor? _baseConfig;
    private bool _isForceProgressiveDownloadInitialized;
    private bool _isPreserveUnrecognizedStreamsInitialized;
    private bool _isStripMetadataInitialized;
    private bool _isProgressCallbackInitialized;
    private bool _isThrowWhenReencodeOptionalInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoProcessor"/> class - this constructor is the copy constructor.
    /// Note: the baseConfig must be fully initialized.
    /// </summary>
    public VideoProcessor(VideoProcessor baseConfig)
    {
        baseConfig.CheckFullyInitialized();
        _baseConfig = baseConfig;
    }

    // Private constructor for our pre-defined configs.
    private VideoProcessor()
    {
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessor"/> that always re-encodes to a standardized H.264 video and AAC audio streams in an MP4
    /// container, while preserving all metadata other than thumbnails by default.
    /// </summary>
    public static VideoProcessor StandardizedH264AACMP4 { get; } = new VideoProcessor()
    {
        SourceVideoCodecs = VideoCodec.AllSourceCodecs,
        SourceAudioCodecs = AudioCodec.AllSourceCodecs,
        SourceFormats = MediaContainerFormat.AllSourceFormats,
        VideoStreamOptions = VideoStreamProcessingOptions.StandardizedH264,
        AudioStreamOptions = AudioStreamProcessingOptions.StandardizedAAC,
        ResultFormats = [MediaContainerFormat.MP4],
        ForceProgressiveDownload = true,
        PreserveUnrecognizedStreams = null,
        StripMetadata = StripVideoMetadataMode.ThumbnailOnly,
        AudioSourceValidation = AudioStreamValidationOptions.None,
        VideoSourceValidation = VideoStreamValidationOptions.None,
        ProgressCallback = null,
        ThrowWhenReencodeOptional = false,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessor"/> that always preserves the original streams when possible.
    /// </summary>
    public static VideoProcessor Preserve { get; } = new VideoProcessor()
    {
        SourceVideoCodecs = VideoCodec.AllSourceCodecs,
        SourceAudioCodecs = AudioCodec.AllSourceCodecs,
        SourceFormats = MediaContainerFormat.AllSourceFormats,
        VideoStreamOptions = VideoStreamProcessingOptions.Preserve,
        AudioStreamOptions = AudioStreamProcessingOptions.Preserve,
        ResultFormats = MediaContainerFormat.AllSourceFormats,
        ForceProgressiveDownload = false,
        PreserveUnrecognizedStreams = true,
        StripMetadata = StripVideoMetadataMode.ThumbnailOnly,
        AudioSourceValidation = AudioStreamValidationOptions.None,
        VideoSourceValidation = VideoStreamValidationOptions.None,
        ProgressCallback = null,
        ThrowWhenReencodeOptional = false,
    };

    /// <summary>
    /// Gets the source video codecs for this mapping (it matches any of these, but all streams must match).
    /// Default is <see cref="VideoCodec.AllSourceCodecs" />.
    /// </summary>
    [field: AllowNull]
    public IReadOnlyList<VideoCodec> SourceVideoCodecs
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.SourceVideoCodecs, ref field)!;
        init
        {
            IReadOnlyList<VideoCodec> result = [.. value];

            if (result.Count is 0)
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Count)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            field = result;
        }
    }

    /// <summary>
    /// Gets the source audio codecs for this mapping (it matches any of these, but all streams must match).
    /// Default is <see cref="AudioCodec.AllSourceCodecs" />.
    /// </summary>
    [field: AllowNull]
    public IReadOnlyList<AudioCodec> SourceAudioCodecs
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.SourceAudioCodecs, ref field)!;
        init
        {
            IReadOnlyList<AudioCodec> result = [.. value];

            if (result.Count is 0)
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Count)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            field = result;
        }
    }

    /// <summary>
    /// Gets the source media container format for this mapping (it matches any of these).
    /// Default is <see cref="MediaContainerFormat.AllSourceFormats" />.
    /// </summary>
    [field: AllowNull]
    public IReadOnlyList<MediaContainerFormat> SourceFormats
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.SourceFormats, ref field)!;
        init
        {
            IReadOnlyList<MediaContainerFormat> result = [.. value];

            if (result.Count is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Count)
                throw new ArgumentException("Formats cannot contain duplicates.", nameof(value));

            field = result;
        }
    }

    /// <summary>
    /// Gets the video stream processing options for the video streams in the source video.
    /// </summary>
    [field: AllowNull]
    public VideoStreamProcessingOptions VideoStreamOptions
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.VideoStreamOptions, ref field)!;
        init => field = new(field, value);
    }

    /// <summary>
    /// Gets the audio stream processing options for the audio streams in the source video.
    /// </summary>
    [field: AllowNull]
    public AudioStreamProcessingOptions AudioStreamOptions
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.AudioStreamOptions, ref field)!;
        init => field = new(field, value);
    }

    /// <summary>
    /// Gets the result media container format for this mapping, or null to use the same as the input.
    /// If re-encoding is required, or other modifications (e.g., metadata changes) are requested, and the source format does not support writing, the first
    /// format in the list is used, so it must be writable as per <see cref="MediaContainerFormat.SupportsWriting" /> - otherwise, non-writable formats will
    /// only be emitted when copying the file in full.
    /// </summary>
    [field: AllowNull]
    public IReadOnlyList<MediaContainerFormat> ResultFormats
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.ResultFormats, ref field)!;
        init
        {
            IReadOnlyList<MediaContainerFormat> result = [.. value];

            if (result.Count is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Count)
                throw new ArgumentException("Formats cannot contain duplicates.", nameof(value));

            if (!result[0].SupportsWriting)
                throw new ArgumentException("The first format in the list must support writing.", nameof(value));

            field = result;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating whether to ensure the 'moov atom' in an MP4 file is at the beginning of the file.
    /// Note: this does not enable true streaming, but it does allow playback to begin before the entire streams are downloaded.
    /// Default is <see langword="true" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceProgressiveDownload
#pragma warning restore SA1623 // Property summary documentation should match accessors
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.ForceProgressiveDownload, ref field, ref _isForceProgressiveDownloadInitialized);
        init => PropertyHelpers.InitHelper(ref field, value, ref _isForceProgressiveDownloadInitialized);
    }

    /// <summary>
    /// Gets or initializes a value indicating whether to preserve unrecognized streams in the output video.
    /// Note: <see langword="null" /> means that unrecognized streams are removed when remuxing only - however more streams (e.g., subtitle streams) may become
    /// recognized in the future.
    /// </summary>
    public bool? PreserveUnrecognizedStreams
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.PreserveUnrecognizedStreams, ref field, ref _isPreserveUnrecognizedStreamsInitialized);
        init => PropertyHelpers.InitHelper(ref field, value, ref _isPreserveUnrecognizedStreamsInitialized);
    }

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata and/or thumbnails from this file.
    /// Note: metadata includes metadata, chapters, stream groups and programs; but does not include side data, attachments, and dispositions.
    /// Note: currently stream groups and programs do not always copy correctly in preserving metadata mode.
    /// Note: also, not all "metadata" is successfully copied either, some fields may be missing, changed, or added, even in preserve mode.
    /// Note: incompatible/unrecognized metadata may be remapped on a best-effort basis, may be lost, or might cause an exception to be thrown when remuxing
    /// Note: attachments currently show up under unrecognized streams, so they are currently controlled by <see cref="PreserveUnrecognizedStreams" />.
    /// between different container formats or re-encoding streams.
    /// Note: metadata is correctly preserved when the source file is copied as-is, but in other cases, metadata copying is subject to the aformentioned
    /// considerations.
    /// </summary>
    public StripVideoMetadataMode StripMetadata
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.StripMetadata, ref field, ref _isStripMetadataInitialized);
        init => PropertyHelpers.InitHelper(ref field, value, ref _isStripMetadataInitialized);
    }

    /// <summary>
    /// Gets or initializes the options for validating audio streams in the source video before processing.
    /// Default is <see cref="AudioStreamValidationOptions.None" />.
    /// </summary>
    public AudioStreamValidationOptions AudioSourceValidation
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.AudioSourceValidation, ref field)!;
        init => field = new(field, value);
    }

    /// <summary>
    /// Gets or initializes the options for validating video streams in the source video before processing.
    /// Default is <see cref="VideoStreamValidationOptions.None" />.
    /// </summary>
    public VideoStreamValidationOptions VideoSourceValidation
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.VideoSourceValidation, ref field)!;
        init => field = new(field, value);
    }

    /// <summary>
    /// Gets or initializes the progress callback, which gets invoked with the current approximate progress (between 0.0 and 1.0).
    /// Default is <see langword="null" />.
    /// </summary>
    public Action<(FileId FileId, string? VariantId), double>? ProgressCallback
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.ProgressCallback, ref field, ref _isProgressCallbackInitialized);
        init => PropertyHelpers.InitHelper(ref field, value, ref _isProgressCallbackInitialized);
    }

    /// <summary>
    /// Gets or initializes a value indicating whether an <see cref="VideoReencodeOptionalException" /> should be thrown when recoding all streams in a video
    /// is skippable (if we were using IfNeeded modes). Note: this includes the following causes of re-encoding, but not any others: video resize options, max
    /// bits per channel, max chroma subsamling, FPS limit, HDR to SDR conversion, max audio channels, and max audio sample rate.
    /// </summary>
    /// <remarks>
    /// Setting this property to <see langword="true" /> can help avoid storing duplicate videos in a repository. For example, if you attempt to generate a
    /// low-res version from an existing repository video that is already equal to or smaller than the desired low-res size, <see
    /// cref="VideoReencodeOptionalException" /> will be thrown. You can catch this exception and use the reference to the existing video, rather than storing
    /// a new identical low-res version.
    /// </remarks>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ThrowWhenReencodeOptional
    {
        get => PropertyHelpers.GetHelper(_baseConfig, static (x) => x.ThrowWhenReencodeOptional, ref field, ref _isThrowWhenReencodeOptionalInitialized);
        init => PropertyHelpers.InitHelper(ref field, value, ref _isThrowWhenReencodeOptionalInitialized);
    }
#pragma warning restore SA1623 // Property summary documentation should match accessors

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

    private void CheckFullyInitialized()
    {
        // If we have a base instance, we can skip these checks, as we checked in the constructor:
        if (_baseConfig is not null)
            return;

        // Check this is a fully initialized / complete instance - we just read all properties to trigger their getters:
        _ = SourceVideoCodecs;
        _ = SourceAudioCodecs;
        _ = SourceFormats;
        _ = VideoStreamOptions;
        _ = AudioStreamOptions;
        _ = ResultFormats;
        _ = ForceProgressiveDownload;
        _ = PreserveUnrecognizedStreams;
        _ = StripMetadata;
        _ = AudioSourceValidation;
        _ = VideoSourceValidation;
        _ = ProgressCallback;
        _ = ThrowWhenReencodeOptional;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<string> AllowedFileExtensions => field ??= [.. SourceFormats.SelectMany(f => f.CommonExtensions).Distinct()];

    /// <inheritdoc/>
    protected override async Task<FileProcessResult> ProcessAsync(FileProcessContext context)
    {
        // Check this is a fully initialized complete instance:
        CheckFullyInitialized();

        // Check if ffmpeg is configured:
        _ = FFmpegExePath;

        // Check if the required decoders / demuxers are available:

        foreach (var codec in SourceVideoCodecs)
        {
            if (!codec.HasSupportedDecoder)
                throw new NotSupportedException($"The required video codec '{codec.Name}' is not supported by the configured ffmpeg installation.");
        }

        foreach (var codec in SourceAudioCodecs)
        {
            if (!codec.HasSupportedDecoder)
                throw new NotSupportedException($"The required audio codec '{codec.Name}' is not supported by the configured ffmpeg installation.");
        }

        foreach (var format in SourceFormats)
        {
            string formatName = format.Name;
            if (!format.HasSupportedDemuxer)
                throw new NotSupportedException($"The required media container format '{formatName}' is not supported by the configured ffmpeg installation.");
        }

        // Check if the required encoders / muxers are available (note: we only check for the first result codec / format, as that is what will be used):

        if (!(VideoStreamOptions.ResultCodecs[0] == VideoCodec.H264
            ? FFprobeUtils.Configuration.SupportsLibX264Encoder
            : FFprobeUtils.Configuration.SupportsLibX265Encoder))
        {
            string codecName = VideoStreamOptions.ResultCodecs[0].Name;
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

        // Get temp file for video:
        var tempOutputFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
        var sourceFileWithCorrectExtension = tempOutputFile;

        // Read info of source video:
        var sourceInfo = await FFprobeUtils.GetVideoFileAsync(tempOutputFile, context.CancellationToken).ConfigureAwait(false);

        // Figure out which source format it supposedly is & check if we support it & that it matches the file extension:
        var sourceFormat = SourceFormats
            .FirstOrDefault((x) => x.NameMatches(sourceInfo.FormatName))
                ?? throw new FileProcessException($"The source video format '{sourceInfo.FormatName}' is not supported by this processor.");

        // If file extension does not match the source format, ensure we still detect the same format after copying to the correct extension:
        if (!sourceFormat.CommonExtensions.Contains(context.Extension, StringComparer.OrdinalIgnoreCase))
        {
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
                throw new FileProcessException("The video format is inconsistent with its file extension in a potentially malicious way.");
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
                // Skip thumbails
                if (videoStream.IsAttachedPic || videoStream.IsTimedThumbnail)
                {
                    continue;
                }

                numVideoStreams++;

                double? duration = videoStream.Duration ?? sourceInfo.Duration;
                if (duration is not null)
                {
                    if (VideoSourceValidation.MaxLength.HasValue && duration > VideoSourceValidation.MaxLength.Value.TotalSeconds)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} is longer than maximum allowed duration.");

                    if (VideoSourceValidation.MinLength.HasValue && duration < VideoSourceValidation.MinLength.Value.TotalSeconds)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} is shorter than minimum allowed duration.");
                }
                else if (VideoSourceValidation.MaxLength.HasValue || VideoSourceValidation.MinLength.HasValue)
                {
                    throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown duration, cannot validate.");
                }

                if (VideoSourceValidation.MaxWidth.HasValue)
                {
                    if (videoStream.Width > VideoSourceValidation.MaxWidth.Value)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} width exceeds maximum allowed.");

                    if (videoStream.Width <= 0)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown width, cannot validate.");
                }

                if (VideoSourceValidation.MaxHeight.HasValue)
                {
                    if (videoStream.Height > VideoSourceValidation.MaxHeight.Value)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} height exceeds maximum allowed.");

                    if (videoStream.Height <= 0)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown height, cannot validate.");
                }

                if (VideoSourceValidation.MaxPixels.HasValue)
                {
                    if ((long)videoStream.Width * videoStream.Height > VideoSourceValidation.MaxPixels.Value)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} exceeds maximum allowed pixel count.");

                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown dimensions, cannot validate.");
                }

                if (VideoSourceValidation.MinWidth.HasValue)
                {
                    if (videoStream.Width <= 0)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown width, cannot validate.");

                    if (videoStream.Width < VideoSourceValidation.MinWidth.Value)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} width is less than minimum required.");
                }

                if (VideoSourceValidation.MinHeight.HasValue)
                {
                    if (videoStream.Height <= 0)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown height, cannot validate.");

                    if (videoStream.Height < VideoSourceValidation.MinHeight.Value)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} height is less than minimum required.");
                }

                if (VideoSourceValidation.MinPixels.HasValue)
                {
                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown dimensions, cannot validate.");

                    if ((long)videoStream.Width * videoStream.Height < VideoSourceValidation.MinPixels.Value)
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} is less than minimum required pixel count.");
                }

                // Check codec:
                if (MatchVideoCodecByName(SourceVideoCodecs, videoStream.CodecName) is null)
                {
                    anyInvalidCodecs = true;
                    continue;
                }

                // If resizing is enabled, check if we would resize:
                if (VideoStreamOptions.ResizeOptions is { } resizeOptions)
                {
                    if (videoStream.Width <= 0 || videoStream.Height <= 0)
                    {
                        throw new FileProcessException($"Video stream {numVideoStreams - 1} has unknown dimensions, cannot determine resizing.");
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
                if (VideoStreamOptions.MaximumBitsPerChannel != BitsPerChannel.Preserve)
                {
                    if (bitsPerSample <= 0)
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                    else if (bitsPerSample > (GetBitsPerChannel(VideoStreamOptions.MaximumBitsPerChannel) ?? int.MaxValue))
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
                if (VideoStreamOptions.MaximumChromaSubsampling != ChromaSubsampling.Preserve)
                {
                    if (pixFormatInfo is null)
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                    else if (VideoStreamOptions.MaximumChromaSubsampling != ChromaSubsampling.Preserve && (
                        !pixFormatInfo.Value.IsStandard ||
                        !pixFormatInfo.Value.IsYUV ||
                        pixFormatInfo.Value.ChromaSubsampling > (GetChromaSubsampling(VideoStreamOptions.MaximumChromaSubsampling) ?? 444)))
                    {
                        anyWouldReencodeForReencodeOptionalPurposes = true;
                        continue;
                    }
                }

                // Check fps:
                var targetFps = VideoStreamOptions.FpsOptions?.TargetFps;
                if (targetFps is not null && (
                    videoStream.FpsNum <= 0 ||
                    videoStream.FpsDen <= 0 ||
                    (long)videoStream.FpsNum * targetFps.Value.Den > (long)videoStream.FpsDen * targetFps.Value.Num))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                    continue;
                }

                // Check if hdr:
                if (VideoStreamOptions.RemapHDRToSDR && !IsKnownSDRColorProfile(videoStream.ColorTransfer, videoStream.ColorPrimaries, videoStream.ColorSpace))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                }
            }
            else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
            {
                numAudioStreams++;

                double? duration = audioStream.Duration ?? sourceInfo.Duration;
                if (duration is not null)
                {
                    if (AudioSourceValidation.MaxLength.HasValue && duration > AudioSourceValidation.MaxLength.Value.TotalSeconds)
                        throw new FileProcessException($"Audio stream {numAudioStreams - 1} is longer than maximum allowed duration.");

                    if (AudioSourceValidation.MinLength.HasValue && duration < AudioSourceValidation.MinLength.Value.TotalSeconds)
                        throw new FileProcessException($"Audio stream {numAudioStreams - 1} is shorter than minimum allowed duration.");
                }
                else if (AudioSourceValidation.MaxLength.HasValue || AudioSourceValidation.MinLength.HasValue)
                {
                    throw new FileProcessException($"Audio stream {numAudioStreams - 1} has unknown duration, cannot validate.");
                }

                // Check codec:
                if (MatchAudioCodecByName(SourceAudioCodecs, audioStream.CodecName, audioStream.ProfileName) is null)
                {
                    anyInvalidCodecs = true;
                    continue;
                }

                // Check channels:
                if ((AudioStreamOptions.MaxChannels != AudioChannels.Preserve && audioStream.Channels < 0) ||
                    audioStream.Channels > GetAudioChannelCount(AudioStreamOptions.MaxChannels))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                    continue;
                }

                // Check sample rate:
                if ((AudioStreamOptions.SampleRate != AudioSampleRate.Preserve && audioStream.SampleRate < 0) ||
                    audioStream.SampleRate > GetAudioSampleRate(AudioStreamOptions.SampleRate))
                {
                    anyWouldReencodeForReencodeOptionalPurposes = true;
                    continue;
                }
            }
        }

        if (VideoSourceValidation.MaxStreams.HasValue && numVideoStreams > VideoSourceValidation.MaxStreams.Value)
            throw new FileProcessException("The number of video streams exceeds the maximum allowed.");

        if (VideoSourceValidation.MinStreams.HasValue && numVideoStreams < VideoSourceValidation.MinStreams.Value)
            throw new FileProcessException("The number of video streams is less than the minimum required.");

        if (AudioSourceValidation.MaxStreams.HasValue && numAudioStreams > AudioSourceValidation.MaxStreams.Value)
            throw new FileProcessException("The number of audio streams exceeds the maximum allowed.");

        if (AudioSourceValidation.MinStreams.HasValue && numAudioStreams < AudioSourceValidation.MinStreams.Value)
            throw new FileProcessException("The number of audio streams is less than the minimum required.");

        if (numAudioStreams == 0 && numVideoStreams == 0)
            throw new FileProcessException("The source video contains no audio or video streams.");

        if (anyInvalidCodecs)
            throw new FileProcessException("One or more streams use a codec that is not supported by this processor.");

        if (!anyWouldReencodeForReencodeOptionalPurposes && ThrowWhenReencodeOptional)
            throw new VideoReencodeOptionalException();

        // Determine if we need to make any changes to the file at all - this involves checking: resizing, re-encoding, removing metadata, etc. - note: some
        // are already checked above with anyWouldReencodeForReencodeOptionalPurposes.
        // Also check if we need to remux ignoring "if smaller" (and similar potentially unnecessary) re-encodings.
        bool remuxRequired =
            anyWouldReencodeForReencodeOptionalPurposes ||
            !ResultFormats.Contains(sourceFormat) ||
            StripMetadata == StripVideoMetadataMode.All ||
            ForceProgressiveDownload;
        bool remuxGuaranteedRequired = remuxRequired;
        if (!remuxRequired)
        {
            foreach (var stream in sourceInfo.Streams)
            {
                if (stream is FFprobeUtils.VideoStreamInfo videoStream)
                {
                    bool isThumbnail = videoStream.IsAttachedPic || videoStream.IsTimedThumbnail;
                    if (StripMetadata != StripVideoMetadataMode.None && isThumbnail)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        break;
                    }

                    if (isThumbnail)
                    {
                        continue;
                    }

                    if (VideoStreamOptions.StripMetadata || MatchVideoCodecByName(VideoStreamOptions.ResultCodecs, videoStream.CodecName) is null)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        break;
                    }

                    if (VideoStreamOptions.ReencodeBehavior != ReencodeBehavior.IfNeeded)
                    {
                        remuxRequired = true;
                    }
                }
                else if (stream is FFprobeUtils.AudioStreamInfo audioStream)
                {
                    if (AudioStreamOptions.StripMetadata ||
                        MatchAudioCodecByName(AudioStreamOptions.ResultCodecs, audioStream.CodecName, audioStream.ProfileName) is null)
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        break;
                    }

                    if (AudioStreamOptions.ReencodeBehavior != ReencodeBehavior.IfNeeded)
                    {
                        remuxRequired = true;
                    }
                }
                else if (stream is FFprobeUtils.UnrecognisedStreamInfo unrecognizedStream)
                {
                    bool isThumbnail = unrecognizedStream.IsAttachedPic || unrecognizedStream.IsTimedThumbnail;
                    if (isThumbnail ? (StripMetadata != StripVideoMetadataMode.None) : (PreserveUnrecognizedStreams == false))
                    {
                        remuxRequired = true;
                        remuxGuaranteedRequired = true;
                        break;
                    }
                }
            }
        }

        // If remuxing is not required, we can just return the original file:
        if (!remuxRequired) return FileProcessResult.File(sourceFileWithCorrectExtension);

        // Set up the main command:

        bool preserveUnrecognizedStreams = PreserveUnrecognizedStreams ?? (sourceFormat == ResultFormats[0]);
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
                fileIndex: StripMetadata == StripVideoMetadataMode.All ? -1 : 0,
                streamKind: 'g',
                streamIndexWithinKind: -1,
                outputIndex: -1));

        List<(int InputIndex, int OutputIndex, string SourceValidFileExtension, bool SupportsMP4Container, char Kind)> streamsToCheckSize = [];
        List<(char Kind, int InputIndex, int OutputIndex, bool MapMetadata)> streamMapping = [];
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
                            mapToOutput: StripMetadata != StripVideoMetadataMode.None));

                    if (StripMetadata == StripVideoMetadataMode.None)
                    {
                        outputVideoStreamIndex++;
                        outputStreamIndex++;
                        streamMapping.Add((Kind: 'v', InputIndex: inputVideoStreamIndex, OutputIndex: id, MapMetadata: true));

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
                bool mapMetadata = StripMetadata != StripVideoMetadataMode.All && !VideoStreamOptions.StripMetadata;
                streamMapping.Add((Kind: 'v', InputIndex: inputVideoStreamIndex, OutputIndex: id, MapMetadata: mapMetadata));
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
                VideoCodec? videoCodec = MatchVideoCodecByName(VideoStreamOptions.ResultCodecs, videoStream.CodecName);
                bool isRequiredReencode = videoCodec is null;
                bool reencode =
                    isRequiredReencode ||
                    videoCodec is { SupportsMP4Muxing: false } ||
                    VideoStreamOptions.ReencodeBehavior != ReencodeBehavior.IfNeeded;
                FFmpegUtils.PerStreamFilterOverride? filterOverride = null;
                if (VideoStreamOptions.ResizeOptions is { } resizeOptions &&
                    ((videoStream.Width > resizeOptions.Width) || (videoStream.Height > resizeOptions.Height)))
                {
                    reencode = true;
                    isRequiredReencode = true;
                    filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                    perOutputStreamOverrides.Add(filterOverride);
                    double potentialWidth1 = resizeOptions.Width;
                    double potentialHeight2 = resizeOptions.Height;
                    double potentialHeight1 = (double)videoStream.Width / videoStream.Height * potentialWidth1;
                    double potentialWidth2 = (double)videoStream.Height / videoStream.Width * potentialHeight2;
                    var (newWidth, newHeight) = potentialWidth1 < potentialWidth2
                        ? ((int)Math.Round(potentialWidth1), (int)Math.Round(potentialHeight1))
                        : ((int)Math.Round(potentialWidth2), (int)Math.Round(potentialHeight2));
                    filterOverride.Scale = (newWidth, newHeight);
                }

                // Check for fps:
                if (VideoStreamOptions.FpsOptions is not null)
                {
                    int maxFpsNum = VideoStreamOptions.FpsOptions.TargetFps.Num;
                    int maxFpsDen = VideoStreamOptions.FpsOptions.TargetFps.Den;

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
                        if (VideoStreamOptions.FpsOptions.LimitMode == VideoFpsLimitMode.Exact || videoStream.FpsNum <= 0 || videoStream.FpsDen <= 0)
                        {
                            newFpsNum = maxFpsNum;
                            newFpsDen = maxFpsDen;
                        }
                        else
                        {
                            Debug.Assert(
                                VideoStreamOptions.FpsOptions.LimitMode == VideoFpsLimitMode.DivideByInteger,
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
                bool isHdr =
                    videoStream.ColorTransfer is not (null or "bt709" or "bt601" or "smpte170m") ||
                    videoStream.ColorPrimaries is not (null or "bt709" or "bt601") ||
                    videoStream.ColorSpace is not (null or "bt709" or "bt601");

                // Check if we have to select a pixel format:
                // Note: if we're re-encoding, we don't try to preserve the original pixel format.
                int finalBitsPerChannel = -1, finalChromaSubsampling = -1;
                var pixFormatInfo = GetPixelFormatCharacteristics(videoStream.PixelFormat);
                int bitsPerSample = pixFormatInfo?.BitsPerSample ?? videoStream.BitsPerSample;
                if (bitsPerSample > (GetBitsPerChannel(VideoStreamOptions.MaximumBitsPerChannel) ?? int.MaxValue) ||
                    (pixFormatInfo is not null && bitsPerSample != pixFormatInfo.Value.BitsPerSample))
                {
                    reencode = true;
                    isRequiredReencode = true;
                }
                else if (VideoStreamOptions.MaximumChromaSubsampling != ChromaSubsampling.Preserve && (
                    pixFormatInfo is null ||
                    !pixFormatInfo.Value.IsStandard ||
                    !pixFormatInfo.Value.IsYUV ||
                    pixFormatInfo.Value.ChromaSubsampling > GetChromaSubsampling(VideoStreamOptions.MaximumChromaSubsampling)))
                {
                    reencode = true;
                    isRequiredReencode = true;
                }

                // If we're re-encoding, we want to specify the pixel format always:
                // Note: we also check for what HDR remapping causing re-endoding would give here, since it also requires us to always specify something.
                string pixFormat = string.Empty;
                if (reencode || (isHdr && VideoStreamOptions.RemapHDRToSDR))
                {
                    int videoSubsampling = pixFormatInfo switch
                    {
                        { ChromaSubsampling: 400 } => 420,
                        { ChromaSubsampling: 440 } => 444,
                        { ChromaSubsampling: var x } => x,
                        _ => 444,
                    };
                    int maxSubsampling = GetChromaSubsampling(VideoStreamOptions.MaximumChromaSubsampling) ?? 444;
                    int videoBitsPerSample = bitsPerSample >= 0 ? bitsPerSample : 12;
                    int maxBitsPerSample = GetBitsPerChannel(VideoStreamOptions.MaximumBitsPerChannel) ?? 12;
                    if (VideoStreamOptions.ResultCodecs[0] != VideoCodec.HEVC) maxBitsPerSample = int.Min(maxBitsPerSample, 10);
                    finalBitsPerChannel = int.Min(videoBitsPerSample, maxBitsPerSample);
                    finalChromaSubsampling = int.Min(videoSubsampling, maxSubsampling);
                    pixFormat = (finalChromaSubsampling, finalBitsPerChannel) switch
                    {
                        (420, 8) => "yuvj420p",
                        (420, 10) => "yuv420p10le",
                        (420, 12) => "yuv420p12le",
                        (422, 8) => "yuvj422p",
                        (422, 10) => "yuv422p10le",
                        (422, 12) => "yuv422p12le",
                        (444, 8) => "yuvj444p",
                        (444, 10) => "yuv444p10le",
                        (444, 12) => "yuv444p12le",
                        _ => throw new UnreachableException("Unimplemented pixel format for video encoding."),
                    };

                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamPixelFormatOverride(streamKind: 'v', streamIndexWithinKind: id, pixelFormat: pixFormat));
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamColorRangeOverride(streamKind: 'v', streamIndexWithinKind: id, colorRange: "pc"));

                    if (videoStream.ColorRange != "pc")
                    {
                        if (filterOverride is null)
                        {
                            filterOverride = new FFmpegUtils.PerStreamFilterOverride(streamKind: 'v', streamIndexWithinKind: id);
                            perOutputStreamOverrides.Add(filterOverride);
                        }

                        filterOverride.NewVideoRange = "pc";
                    }
                }

                // Deal with HDR:
                if (isHdr && (VideoStreamOptions.RemapHDRToSDR || reencode))
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
                if (!isRequiredReencode && VideoStreamOptions.ReencodeBehavior == ReencodeBehavior.IfSmaller)
                {
                    var codec = MatchVideoCodecByName(VideoStreamOptions.ResultCodecs, videoStream.CodecName)!;
                    streamsToCheckSize.Add((
                        InputIndex: inputVideoStreamIndex,
                        OutputIndex: id,
                        SourceValidFileExtension: codec.WritableFileExtension,
                        SupportsMP4Container: codec.SupportsMP4Muxing,
                        Kind: 'v'));
                }

                // Set up codec to use
                if (!reencode)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: id, codec: "copy"));
                }
                else if (VideoStreamOptions.ResultCodecs[0] == VideoCodec.H264 && FFprobeUtils.Configuration.SupportsLibX264Encoder)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: id, codec: "libx264"));

                    int crf = GetH264CRF(VideoStreamOptions.Quality);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCRFOverride(streamKind: 'v', streamIndexWithinKind: id, crf: crf));

                    string preset = GetVideoCompressionPreset(VideoStreamOptions.CompressionPreference);
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
                else if (VideoStreamOptions.ResultCodecs[0] == VideoCodec.HEVC && FFprobeUtils.Configuration.SupportsLibX265Encoder)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: id, codec: "libx265"));

                    int crf = GetH265CRF(VideoStreamOptions.Quality);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCRFOverride(streamKind: 'v', streamIndexWithinKind: id, crf: crf));

                    string preset = GetVideoCompressionPreset(VideoStreamOptions.CompressionPreference);
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
                // Map the stream:
                inputAudioStreamIndex++;
                int id = outputAudioStreamIndex;
                outputAudioStreamIndex++;
                outputStreamIndex++;
                bool mapMetadata = StripMetadata != StripVideoMetadataMode.All && !AudioStreamOptions.StripMetadata;
                streamMapping.Add((Kind: 'a', InputIndex: inputAudioStreamIndex, OutputIndex: id, MapMetadata: mapMetadata));
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
                AudioCodec? audioCodec = MatchAudioCodecByName(AudioStreamOptions.ResultCodecs, audioStream.CodecName, audioStream.ProfileName);
                bool isRequiredReencode = audioCodec is null;
                bool reencode =
                    isRequiredReencode ||
                    audioCodec is { SupportsMP4Muxing: false } ||
                    AudioStreamOptions.ReencodeBehavior != ReencodeBehavior.IfNeeded;
                int? targetChannels = GetAudioChannelCount(AudioStreamOptions.MaxChannels);
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
                int? targetSampleRate = GetAudioSampleRate(AudioStreamOptions.SampleRate);
                if (targetSampleRate.HasValue && audioStream.SampleRate > targetSampleRate.Value)
                {
                    reencode = true;
                    isRequiredReencode = true;
                    perOutputStreamOverrides.Add(
                        new FFmpegUtils.PerStreamSampleRateOverride(streamKind: 'a', streamIndexWithinKind: id, sampleRate: targetSampleRate.Value));
                }

                // Keep track if we want to check the size later:
                if (!isRequiredReencode && AudioStreamOptions.ReencodeBehavior == ReencodeBehavior.IfSmaller)
                {
                    var codec = MatchAudioCodecByName(AudioStreamOptions.ResultCodecs, audioStream.CodecName, audioStream.ProfileName)!;
                    streamsToCheckSize.Add((
                        InputIndex: inputAudioStreamIndex,
                        OutputIndex: id,
                        SourceValidFileExtension: codec.WritableFileExtension,
                        SupportsMP4Container: codec.SupportsMP4Muxing,
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

                    int quality = GetLibFDKAACEncoderQuality(AudioStreamOptions.Quality);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamProfileOverride(streamKind: 'a', streamIndexWithinKind: id, profile: "lc"));
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamVBROverride(streamKind: 'a', streamIndexWithinKind: id, vbr: quality));

                    // For best & high quality, ensure we use the maximum supported cutoff frequency (20kHz):
                    if (quality >= 4)
                        perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCutoffOverride(streamKind: 'a', streamIndexWithinKind: id, cutoff: 20000));
                }
                else if (FFprobeUtils.Configuration.SupportsAACEncoder)
                {
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamCodecOverride(streamKind: 'a', streamIndexWithinKind: id, codec: "aac"));

                    int bitrate = (int)long.Min((long)numChannels * GetNativeAACEncoderBitratePerChannel(AudioStreamOptions.Quality), int.MaxValue);
                    perOutputStreamOverrides.Add(new FFmpegUtils.PerStreamBitrateOverride(streamKind: 'a', streamIndexWithinKind: id, bitrate: bitrate));
                }
                else
                {
                    Debug.Fail("The requested audio codec did not have a supported encoder available in the configured FFmpeg build (unexpected).");
                }
            }
            else if (stream is FFprobeUtils.UnrecognisedStreamInfo unrecognizedStream)
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
                            mapToOutput: StripMetadata != StripVideoMetadataMode.None));

                    if (StripMetadata == StripVideoMetadataMode.None)
                    {
                        outputStreamIndex++;
                        streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: true));

                        perInputStreamOverrides.Add(
                            new FFmpegUtils.PerStreamMapMetadataOverride(
                                fileIndex: 0,
                                streamKind: '\0',
                                streamIndexWithinKind: inputStreamIndex,
                                outputIndex: id));
                    }

                    continue;
                }

                // If we're not preserving unrecognized streams, skip it (we already marked as excluded by default):
                if (!preserveUnrecognizedStreams)
                {
                    continue;
                }

                // Map the stream:
                outputStreamIndex++;
                bool mapMetadata = StripMetadata != StripVideoMetadataMode.All;
                streamMapping.Add((Kind: '\0', InputIndex: inputStreamIndex, OutputIndex: id, MapMetadata: mapMetadata));

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
            mapChaptersFrom: StripMetadata == StripVideoMetadataMode.All ? -1 : 0,
            forceProgressiveDownloadSupport: ForceProgressiveDownload);

        // Run the command
        // Note: The last 5% of progress is reserved for the "checking if smaller" pass & since the progress reported is the highest timestamp completed of any
        // stream, so we want to leave some headroom.
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
        var progressTempFile = (ProgressCallback != null && maxDuration != 0.0) ? context.GetNewWorkFile(".mp4") : null;
        double lastDone = 0.0;
        const double ReservedProgress = 0.05;
        Action<double>? localProgressCallback = progressTempFile != null ? (durationDone) =>
        {
            // Avoid going backwards or repeating the same progress:
            if (durationDone <= lastDone || durationDone < 0.0 || durationDone > maxDuration) return;

            // Clamp to [0.0, 0.95] range:
            double clampedProgress = double.Clamp(durationDone / maxDuration * (1.0 - ReservedProgress), 0.0, 1.0 - ReservedProgress);
            lastDone = durationDone;
            ProgressCallback!((context.FileId, context.VariantId), clampedProgress);
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
                (int InputIndex, int OutputIndex, string SourceValidFileExtension, bool SupportsMP4Container, char Kind) streamToCheck = streamsToCheckSize[i];

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
                    wasSmaller.Add((i, !streamToCheck.SupportsMP4Container));
                }

                // We use half of our headroom for this first pass, so update progress:
                if (localProgressCallback != null)
                {
                    double progressPortion = ReservedProgress * 0.5 / (streamsToCheckSize.Count + 2);
                    double progressValue = 1.0 - ReservedProgress + (progressPortion * (i + 1));
                    ProgressCallback!((context.FileId, context.VariantId), progressValue * maxDuration);
                }
            }

            // Determine if all were smaller & if there's no other reason to stop us just using the original file:
            if (wasSmaller.Count == streamsToCheckSize.Count && !remuxGuaranteedRequired)
            {
                // All streams were smaller, and we do not otherwise require remuxing, so just return the original file:
                resultTempFile.Delete();
                return FileProcessResult.File(sourceFileWithCorrectExtension);
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
                    new FFmpegUtils.PerStreamMapOverride(fileIndex: 0, streamKind: '\0', streamIndexWithinKind: -1, mapToOutput: false));
                perInputStreamOverrides.Add(
                    new FFmpegUtils.PerStreamMapOverride(fileIndex: 1, streamKind: '\0', streamIndexWithinKind: -1, mapToOutput: false));
                foreach (var (kind, inputIndex, outputIndex, mapMetadata) in streamMapping)
                {
                    // Determine if we are using the original or re-encoded stream:
                    bool useOriginal = true;
                    if (nextWasSmaller.Value.InputIndex == inputIndex && nextWasSmaller.Value.Kind == kind)
                    {
                        useOriginal = false;
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
                }

                FFmpegUtils.FFmpegCommand mixCommand = new(
                    inputFiles: [sourceFileWithCorrectExtension, resultTempFile],
                    outputFile: newResultTempFile,
                    perInputStreamOverrides: [.. perInputStreamOverrides],
                    perOutputStreamOverrides: [.. perOutputStreamOverrides],
                    mapChaptersFrom: StripMetadata == StripVideoMetadataMode.All ? -1 : 0,
                    forceProgressiveDownloadSupport: ForceProgressiveDownload);

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
                    ProgressCallback!((context.FileId, context.VariantId), 1.0 - (ReservedProgress / 2.0) + (clampedProgress * (ReservedProgress / 2.0)));
                } : null;
                await FFmpegUtils.RunFFmpegCommandAsync(mixCommand, localProgressCallbackInner, progressTempFile, context.CancellationToken)
                    .ConfigureAwait(false);

                // Update the result file:
                resultTempFile.Delete();
                resultTempFile = newResultTempFile;
            }
        }

        // Return the result file:
        return FileProcessResult.File(resultTempFile);
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

    private static bool IsKnownSDRColorProfile(string? colorTransfer, string? colorPrimaries, string? colorSpace) =>
        (colorTransfer is null or "bt709" or "bt601" or "smpte170m") &&
        (colorPrimaries is null or "bt709" or "bt601") &&
        (colorSpace is null or "bt709" or "bt601");

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
        VideoQuality.Best => 17,
        VideoQuality.High => 20,
        VideoQuality.Medium => 23,
        VideoQuality.Low => 26,
        VideoQuality.Worst => 29,
        _ => throw new UnreachableException("Unrecognized VideoQuality value."),
    };

    private static int GetH265CRF(VideoQuality quality) => quality switch
    {
        VideoQuality.Best => 19,
        VideoQuality.High => 23,
        VideoQuality.Medium => 28,
        VideoQuality.Low => 31,
        VideoQuality.Worst => 34,
        _ => throw new UnreachableException("Unrecognized VideoQuality value."),
    };

    private static string GetVideoCompressionPreset(VideoCompressionLevel compressionLevel) => compressionLevel switch
    {
        VideoCompressionLevel.Best => "slower",
        VideoCompressionLevel.High => "slow",
        VideoCompressionLevel.Medium => "medium",
        VideoCompressionLevel.Low => "faster",
        VideoCompressionLevel.Worst => "superfast",
        _ => throw new UnreachableException("Unrecognized VideoCompressionLevel value."),
    };

    private static int GetLibFDKAACEncoderQuality(AudioQuality quality) => quality switch
    {
        AudioQuality.Best => 5,
        AudioQuality.High => 4,
        AudioQuality.Medium => 3,
        AudioQuality.Low => 2,
        AudioQuality.Worst => 1,
        _ => throw new UnreachableException("Unrecognized AudioQuality value."),
    };

    private static int GetNativeAACEncoderBitratePerChannel(AudioQuality quality) => quality switch
    {
        AudioQuality.Best => 192_000,
        AudioQuality.High => 160_000,
        AudioQuality.Medium => 128_000,
        AudioQuality.Low => 80_000,
        AudioQuality.Worst => 64_000,
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
}
