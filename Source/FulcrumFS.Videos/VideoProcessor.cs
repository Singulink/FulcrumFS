using Singulink.IO;

namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides functionality to process video files with specified options.
/// </summary>
public class VideoProcessor : FileProcessor
{
    /// <summary>
    /// Gets or initializes the collection of video file processing options, which are attempted in order until one matches the predicate. The
    /// <see cref="VideoFileProcessingOptions" /> specifies what to do the video and each stream.
    /// The list must not be empty and should not contain duplicate / otherwise unnecessary source formats (but these are currently not validated).
    /// By default, it is initialized to a convertor that takes in any recognised video file and always re-encodes to standardised H.264 + AAC streams in MP4.
    /// file format, that preserves metadata and discards thumbnails.
    /// </summary>
    public IReadOnlyList<VideoFileProcessingOptions> FileProcessingOptions
    {
        get;
        init
        {
            IReadOnlyList<VideoFileProcessingOptions> v = [.. value];

            if (v.Count is 0)
                throw new ArgumentException("Options cannot be empty.", nameof(value));

            if (v.Any((x) => x is null))
                throw new ArgumentException("Options cannot contain null values.", nameof(value));

            field = value;
        }
    } = [new VideoFileProcessingOptions()];

    /// <summary>
    /// Gets or initializes the options for validating the source video before processing.
    /// </summary>
    public VideoSourceValidationOptions? SourceValidation { get; init; }

    /// <summary>
    /// Gets or initializes the directory containing ffmpeg binaries to use for processing.
    /// On Windows: should contain ffmpeg.exe and ffprobe.exe.
    /// On Linux/macOS: should contain ffmpeg and ffprobe executables with appropriate execute permissions.
    /// </summary>
    public static void InitializeWithFFMpegExecutablesFromPath(IAbsoluteDirectoryPath dirPath)
    {
        var (ffmpeg, ffprobe) = OperatingSystem.IsWindows()
            ? (dirPath.CombineFile("ffmpeg.exe"), dirPath.CombineFile("ffprobe.exe"))
            : (dirPath.CombineFile("ffmpeg"), dirPath.CombineFile("ffprobe"));

        if (!ffmpeg.Exists)
            throw new FileNotFoundException("FFMpeg executable not found in specified directory.", dirPath.ToString());

        if (!ffprobe.Exists)
            throw new FileNotFoundException("FFProbe executable not found in specified directory.", dirPath.ToString());

        FFMpegExePath = ffmpeg;
        FFProbeExePath = ffprobe;
    }

    internal static IFilePath? FFMpegExePath { get; private set; }
    internal static IFilePath? FFProbeExePath { get; private set; }

    // TODO
}
