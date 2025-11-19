namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for predicates used during video processing - requires all options specified to match for the corresponding
/// <see cref="VideoFileProcessingOptions" /> to activate.
/// </summary>
public class VideoProcessingPredicateOptions
{
    /// <summary>
    /// Gets the source video codecs for this mapping (it matches any of these, but all streams must match).
    /// If <see langword="null" />, matches any supported video codec.
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
    /// processing.
    /// </summary>
    public VideoSourceValidationOptions? SourceValidation { get; init; }
}
