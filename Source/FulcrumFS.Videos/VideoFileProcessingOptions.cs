using Singulink.Enums;

namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options that control how a specific kind of source video file (specified by <see cref="SourceVideoCodecs" />, <see cref="SourceAudioCodecs" />,
/// <see cref="SourceMediaContainerFormats" />, and <see cref="SourcePredicate" />) is processed and the result format/s that can be produced.
/// </summary>
public class VideoFileProcessingOptions
{
    /// <summary>
    /// Gets the source video codecs for this mapping (it matches any of these, but all streams must match).
    /// If <see langword="null" />, matches any supported video codec.
    /// Default is <see langword="null" />.
    /// </summary>
    public IReadOnlyList<VideoCodec>? SourceVideoCodecs
    {
        get;
        init
        {
            // Handle null
            if (value is null)
            {
                field = null;
            }

            // Make a copy, and validate it and store it
            else
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
    }

    /// <summary>
    /// Gets the source audio codecs for this mapping (it matches any of these, but all streams must match).
    /// If <see langword="null" />, matches any supported audio codec.
    /// Default is <see langword="null" />.
    /// </summary>
    public IReadOnlyList<AudioCodec>? SourceAudioCodecs
    {
        get;
        init
        {
            // Handle null
            if (value is null)
            {
                field = null;
            }

            // Make a copy, and validate it and store it
            else
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
    }

    /// <summary>
    /// Gets the source media container format for this mapping (it matches any of these).
    /// If <see langword="null" />, matches any supported container format.
    /// Default is <see langword="null" />.
    /// </summary>
    public IReadOnlyList<MediaContainerFormat>? SourceMediaContainerFormats
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
    }

    /// <summary>
    /// Gets or initializes additional options in the form of <see cref="VideoSourceValidationOptions" /> for validating the source video file before
    /// processing. Uses the same format as validation options, but acts as a predicate here.
    /// If <see langword="null" />, no additional validation is performed.
    /// Default is <see langword="null" />.
    /// </summary>
    public VideoSourceValidationOptions? SourcePredicate { get; init; }

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
    /// If <see langword="null" />, the container format of the source video is preserved (when possible, i.e., if re-encoding, or otherwise requesting to
    /// modify things like metadata, it will automatically default to another container if unsupported for writing).
    /// Default is a list containing <see cref="MediaContainerFormat.MP4" />.
    /// </summary>
    public IReadOnlyList<MediaContainerFormat>? ResultMediaContainerFormat
    {
        get;
        init
        {
            if (value is null)
            {
                field = null;
                return;
            }

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
    /// Gets or initializes a value indicating whether to strip metadata from this stream.
    /// If set to <see langword="null" />, metadata preservation is undefined, and it may only be partially preserved.
    /// Default is <see langword="false" />.
    /// </summary>
    public bool? StripMetadata { get; init; } = false;

    /// <summary>
    /// Gets or initializes a value indicating whether to ensure the 'moov atom' in an MP4 file is at the beginning of the file.
    /// Note: this does not enable true streaming, but it does allow playback to begin before the entire streams are downloaded.
    /// Default is <see langword="true" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceProgressiveDownload { get; init; } = true;
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes the behavior for selecting video streams to include in the output video.
    /// Default is <see cref="StreamSelectionBehavior.KeepAll" />.
    /// </summary>
    public StreamSelectionBehavior VideoStreamSelectionBehavior
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = StreamSelectionBehavior.KeepAll;

    /// <summary>
    /// Gets or initializes the behavior for selecting audio streams to include in the output video.
    /// Default is <see cref="StreamSelectionBehavior.KeepAll" />.
    /// </summary>
    public StreamSelectionBehavior AudioStreamSelectionBehavior
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = StreamSelectionBehavior.KeepAll;

    /// <summary>
    /// Gets or initializes a value indicating whether to preserve unrecognized streams in the output video.
    /// Note: <see langword="null" /> is the default behavior, meaning unrecognized streams are removed when changing container format only - however more
    /// streams (e.g., subtitle streams) may become recognized in the future.
    /// Default is <see langword="null" />.
    /// </summary>
    public bool? PreserveUnrecognizedStreams { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to trim the thumbnail from the video.
    /// Default is <see langword="true" />.
    /// Note: disabling trimming may not be respected if the container format is changed.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool StripThumbnail { get; init; } = true;
#pragma warning restore SA1623 // Property summary documentation should match accessors
}
