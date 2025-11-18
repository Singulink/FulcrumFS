namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for predicates used during video processing - requires all options specified to match for the corresponding
/// <see cref="VideoFileProcessingOptions" /> to activate.
/// </summary>
public class VideoProcessingPredicateOptions
{
    /// <summary>
    /// Gets the options for source video codecs for this mapping.
    /// At each position, a list of allowed source video codecs is specified for the corresponding video stream index.
    /// If the list at a position is null, any codec is allowed for that stream.
    /// If the entire property is null, any codec is allowed for all streams.
    /// If there are more streams than entries in this collection, the extra streams use the last value.
    /// If there are fewer streams than entries in this collection, the extra entries are ignored.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<VideoCodec>?>? SourceVideoCodecs
    {
        get;
        init
        {
            // Handle null
            if (value is null)
            {
                field = null;
            }

            // Make a deep copy and store it
            else
            {
                field = [.. value.Select((x) => x is null ? null : (IReadOnlyList<VideoCodec>)[.. x])];
            }
        }
    }

    /// <summary>
    /// Gets the options for source audio codecs for this mapping.
    /// At each position, a list of allowed source audio codecs is specified for the corresponding audio stream index.
    /// If the list at a position is null, any codec is allowed for that stream.
    /// If the entire property is null, any codec is allowed for all streams.
    /// If there are more streams than entries in this collection, the extra streams use the last value.
    /// If there are fewer streams than entries in this collection, the extra entries are ignored.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<AudioCodec>?>? SourceAudioCodecs
    {
        get;
        init
        {
            // Handle null
            if (value is null)
            {
                field = null;
            }

            // Make a deep copy and store it
            else
            {
                field = [.. value.Select((x) => x is null ? null : (IReadOnlyList<AudioCodec>)[.. x])];
            }
        }
    }

    /// <summary>
    /// Gets the source media container format for this mapping (it matches any of these).
    /// If null, matches any container format.
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
                field = [.. value];
            }
        }
    }

    /// <summary>
    /// Gets or initializes additional options in the form of <see cref="VideoSourceValidationOptions" /> for validating the source video file before
    /// processing.
    /// </summary>
    public VideoSourceValidationOptions? SourceValidation { get; init; }
}
