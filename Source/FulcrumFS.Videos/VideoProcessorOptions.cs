namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides configuration options for processing videos in a file repository.
/// </summary>
public class VideoProcessorOptions
{
    /// <summary>
    /// Gets or initializes the collection of video file processing options, which are attempted in order until one matches the predicate
    /// (<see cref="VideoFileProcessingOptions.Predicate" />). The <see cref="VideoFileProcessingOptions" /> specifies what to do the video and each stream.
    /// The list must not be empty and should not contain duplicate / otherwise unnecessary source formats (but these are currently not validated).
    /// By default, it is initialized to a list which maps every supported video format to itself (no conversion).
    /// </summary>
    public IReadOnlyList<VideoFileProcessingOptions> FileProcessingOptions
    {
        get;
        init
        {
            IReadOnlyList<VideoFileProcessingOptions> v = [.. value];

            if (v.Count is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (v.Any(x => x is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            field = value;
        }
    } = [new VideoFileProcessingOptions(new VideoProcessingPredicateOptions()) { StripMetadata = false, PreserveUnrecognisedStreams = true }];

    /// <summary>
    /// Gets or initializes the options for validating the source video before processing.
    /// </summary>
    public VideoSourceValidationOptions? SourceValidation { get; init; }

    /// <summary>
    /// Gets or initializes the directory containing ffmpeg binaries to use for processing.
    /// On Windows: should contain ffmpeg.exe and ffprobe.exe.
    /// On Linux/macOS: should contain ffmpeg and ffprobe executables with appropriate execute permissions.
    /// </summary>
    public required string FFMpegDirectory { get; init; }
}
