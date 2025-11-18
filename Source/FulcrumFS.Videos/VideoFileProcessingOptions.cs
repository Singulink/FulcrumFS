namespace FulcrumFS.Videos;

/// <summary>
/// Provides options that control how a specific kind of source video file (specified by <see cref="Predicate" />) is processed and the result format that is
/// produced.
/// </summary>
public class VideoFileProcessingOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoFileProcessingOptions"/> class with the specified predicate.
    /// </summary>
    public VideoFileProcessingOptions(VideoProcessingPredicateOptions predicate)
    {
        Predicate = predicate;
    }

    /// <summary>
    /// Gets the condition under which this format processing option applies.
    /// </summary>
    public VideoProcessingPredicateOptions Predicate { get; }

    /// <summary>
    /// Gets the video stream processing options for each video stream in the source video.
    /// If the value is <see langword="null" />, then the behaviour is specified by <see cref="PreserveUnrecognisedStreams" />.
    /// If there are no entries in this collection, all video streams are copied as-is.
    /// If there are move video streams than entries in this collection, the extra streams use the last value.
    /// If there are fewer video streams than entries in this collection, the extra entries are ignored.
    /// </summary>
    public IReadOnlyList<VideoStreamProcessingOptions>? VideoStreamOptions
    {
        get;
        init
        {
            // Handle null
            if (value is null)
            {
                field = null;
            }

            // Make a copy and store it
            else
            {
                field = [.. value];
            }
        }
    }

    /// <summary>
    /// Gets the audio stream processing options for each audio stream in the source video.
    /// If the value is <see langword="null" />, then the behaviour is specified by <see cref="PreserveUnrecognisedStreams" />.
    /// If there are no entries in this collection, all audio streams are copied as-is.
    /// If there are move audio streams than entries in this collection, the extra streams use the last value.
    /// If there are fewer audio streams than entries in this collection, the extra entries are ignored.
    /// </summary>
    public IReadOnlyList<AudioStreamProcessingOptions>? AudioStreamOptions
    {
        get;
        init
        {
            // Handle null
            if (value is null)
            {
                field = null;
            }

            // Make a copy and store it
            else
            {
                field = [.. value];
            }
        }
    }

    /// <summary>
    /// Gets the result media container format for this mapping, or null to use the same as the input.
    /// </summary>
    public MediaContainerFormat? ResultMediaContainerFormat { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to strip all metadata from the resulting video.
    /// If unset, metadata preservation is undefined, and it may only be partially preserved.
    /// Note: disabling metadata globally will result in stream metadata being removed as well.
    /// Note: The metadata here excludes thumbnails (that the tool understands), as that is controlled by <see cref="ThumbnailOptions" />.
    /// </summary>
    public bool? StripMetadata { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to preserve unrecognised streams in the output video.
    /// Note: <see langword="false" /> is the default behavior, meaning unrecognised streams are removed - however more streams (e.g., subtitle streams) may be
    /// recognised in the future.
    /// If support for more streams is added, they will default to the value specified here (i.e., preserved or removed), like with
    /// <see cref="VideoStreamOptions" /> and <see cref="AudioStreamOptions" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool PreserveUnrecognisedStreams { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes the options for generating or copying a thumbnail for the video.
    /// If set, a thumbnail image will be generated from the specified video stream at the specified offset.
    /// The offset is derived from Offset and MaxFraction by trying to use Offset seconds into the video, but clamping it to MaxFraction of the total duration
    /// if necessary - this is then clamped between 0 and Length.
    /// The video stream used is specified by VideoStream, which is the zero-based index of the video stream in the source video.
    /// If set to null (the default), the thumbnail will be left as-is from the source video (if present); but it can be forced to be removed by setting the
    /// VideoStream to an invalid index like -1.
    /// </summary>
    public (int VideoStream, double Offset, double MaxFraction)? ThumbnailOptions { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to just use the original file if it was larger than originally after applying all of the other options.
    /// Default is false.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ShouldCopyIfLarger { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors
}
