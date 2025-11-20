using Singulink.IO;
using Singulink.Threading;

namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides functionality to process video files with specified options.
/// </summary>
public class VideoProcessor : FileProcessor
{
    /// <summary>
    /// Gets the source video codecs for this mapping (it matches any of these, but all streams must match).
    /// Default is <see cref="VideoCodec.AllSourceCodecs" />.
    /// </summary>
    public IReadOnlyList<VideoCodec> SourceVideoCodecs
    {
        get;
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
    } = VideoCodec.AllSourceCodecs;

    /// <summary>
    /// Gets the source audio codecs for this mapping (it matches any of these, but all streams must match).
    /// Default is <see cref="AudioCodec.AllSourceCodecs" />.
    /// </summary>
    public IReadOnlyList<AudioCodec> SourceAudioCodecs
    {
        get;
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
    } = AudioCodec.AllSourceCodecs;

    /// <summary>
    /// Gets the source media container format for this mapping (it matches any of these).
    /// Default is <see cref="MediaContainerFormat.AllSourceFormats" />.
    /// </summary>
    public IReadOnlyList<MediaContainerFormat> SourceFormats
    {
        get;
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
    } = MediaContainerFormat.AllSourceFormats;

    /// <summary>
    /// Gets the video stream processing options for the video streams in the source video.
    /// Default is a new instance of <see cref="VideoStreamProcessingOptions" /> that specifies always re-encoding to a standardised H.264 stream.
    /// </summary>
    public VideoStreamProcessingOptions VideoStreamOptions { get; init; } = new VideoStreamProcessingOptions();

    /// <summary>
    /// Gets the audio stream processing options for the audio streams in the source video.
    /// Default is a new instance of <see cref="AudioStreamProcessingOptions" /> that specifies always re-encoding to a standardised AAC stream.
    /// </summary>
    public AudioStreamProcessingOptions AudioStreamOptions { get; init; } = new AudioStreamProcessingOptions();

    /// <summary>
    /// Gets the result media container format for this mapping, or null to use the same as the input.
    /// If re-encoding is required, or other modifications (e.g., metadata changes) are requested, and the source format does not support writing, the first
    /// format in the list is used, so it must be writable as per <see cref="MediaContainerFormat.SupportsWriting" /> - otherwise, non-writable formats will
    /// only be emitted when copying the file in full.
    /// Default is a list containing <see cref="MediaContainerFormat.MP4" />.
    /// </summary>
    public IReadOnlyList<MediaContainerFormat> ResultFormats
    {
        get;
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
    } = [MediaContainerFormat.MP4];

    /// <summary>
    /// Gets or initializes a value indicating whether to ensure the 'moov atom' in an MP4 file is at the beginning of the file.
    /// Note: this does not enable true streaming, but it does allow playback to begin before the entire streams are downloaded.
    /// Default is <see langword="true" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceProgressiveDownload { get; init; } = true;
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to preserve unrecognized streams in the output video.
    /// Note: <see langword="null" /> is the default behavior, meaning unrecognized streams are removed when changing container format only - however more
    /// streams (e.g., subtitle streams) may become recognized in the future.
    /// Default is <see langword="null" />.
    /// </summary>
    public bool? PreserveUnrecognizedStreams { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata and/or thumbnails from this file.
    /// Default is <see cref="StripVideoMetadataMode.ThumbnailOnly" />.
    /// </summary>
    public StripVideoMetadataMode StripMetadata { get; init; } = StripVideoMetadataMode.ThumbnailOnly;

    /// <summary>
    /// Gets or initializes the options for validating audio streams in the source video before processing.
    /// Default is <see langword="null" />.
    /// </summary>
    public AudioStreamValidationOptions? AudioSourceValidation { get; init; }

    /// <summary>
    /// Gets or initializes the options for validating video streams in the source video before processing.
    /// Default is <see langword="null" />.
    /// </summary>
    public VideoStreamValidationOptions? VideoSourceValidation { get; init; }

    /// <summary>
    /// Initializes the directory containing ffmpeg binaries to use for processing.
    /// On Windows: should contain ffmpeg.exe and ffprobe.exe.
    /// On Linux/macOS: should contain ffmpeg and ffprobe executables with appropriate execute permissions.
    /// </summary>
    public static void InitializeWithFFMpegExecutablesFromPath(IAbsoluteDirectoryPath dirPath)
    {
        var (ffmpeg, ffprobe) = OperatingSystem.IsWindows()
            ? (dirPath.CombineFile("ffmpeg.exe"), dirPath.CombineFile("ffprobe.exe"))
            : (dirPath.CombineFile("ffmpeg"), dirPath.CombineFile("ffprobe"));

        if (!ffmpeg.Exists)
            throw new FileNotFoundException("FFMpeg executable not found in specified directory.", ffmpeg.ToString());

        if (!ffprobe.Exists)
            throw new FileNotFoundException("FFProbe executable not found in specified directory.", ffprobe.ToString());

        if (!_ffmpegPathInitialized.TrySet())
            throw new InvalidOperationException("FFMpeg executable paths have already been initialized.");

        FFMpegExePath = ffmpeg;
        FFProbeExePath = ffprobe;
    }

    private static InterlockedFlag _ffmpegPathInitialized;

    internal static IFilePath FFMpegExePath
    {
        get => field ?? throw new InvalidOperationException("Cannot access FFMpeg executable path before it has been initialized. Call InitializeWithFFMpegExecutablesFromPath first.");
        private set;
    }

    internal static IFilePath FFProbeExePath
    {
        get => field ?? throw new InvalidOperationException("Cannot access FFMpeg executable path before it has been initialized. Call InitializeWithFFMpegExecutablesFromPath first.");
        private set;
    }

    // TODO
}
