namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the options for processing videos to create a thumbnail with a <see cref="ThumbnailProcessor" />.
/// If <see cref="IncludeThumbnailVideoStreams" /> is <see langword="true" /> and there is a video stream marked as a thumbnail stream, the thumbnail image is
/// taken from that stream.
/// Otherwise, the thumbnail image is taken from the lowest of <see cref="ImageTimestamp" /> and <see cref="ImageTimestampFraction" /> if both are specified
/// and in range (this allows specifying options that work well for both long and short videos, e.g., by specifying 5s and 30%, for short videos 5s may be way
/// too far in, it could be the end of the video so it takes 30%, and for long videos 30% could be way too far in, whereas 5s would be better).
/// The thumbnail image is taken from the first video stream that is most likely to be the main video based on dispositions.
/// The default format for the extracted thumbnail image is PNG.
/// The default options result in no thumbnail extraction options being applied.
/// Note: if no thumbnail is able to be extracted, then a <see cref="ThumbnailSelectingException" /> will be thrown when processing.
/// Note: does not attempt to remove alpha channels, nor attempt to reduce bit depth to 8; callers can utilize additional image processing if needed for these.
/// </summary>
public sealed record ThumbnailProcessingOptions
{
    /// <summary>
    /// Gets an options instance with standard thumbnail extraction settings - ignores thumbnail streams, and selects from the lesser of 5 seconds and 30% of
    /// the video duration.
    /// </summary>
    public static ThumbnailProcessingOptions Standard { get; } = new()
    {
        ImageTimestamp = TimeSpan.FromSeconds(5),
        ImageTimestampFraction = 0.3,
    };

    /// <summary>
    /// Gets or initializes a value indicating whether video streams marked as thumbnail streams should be considered for thumbnail extraction.
    /// Default is <see langword="false" />, meaning this option is not used.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool IncludeThumbnailVideoStreams { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes the timestamp within the video at which to capture the thumbnail image.
    /// Default is <see langword="null" />, meaning this option is not used.
    /// </summary>
    public TimeSpan? ImageTimestamp
    {
        get;
        init
        {
            if (value is not null) ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, TimeSpan.Zero, nameof(ImageTimestamp));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the fraction of the video's duration at which to capture the thumbnail image.
    /// Default is <see langword="null" />, meaning this option is not used.
    /// </summary>
    public double? ImageTimestampFraction
    {
        get;
        init
        {
            if (value is not null)
            {
                if (value < 0.0 || value > 1.0)
                    throw new ArgumentOutOfRangeException(nameof(ImageTimestampFraction), "ImageTimestampFraction must be between 0.0 and 1.0 inclusive.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating whether to remap HDR to SDR for the thumbnail.
    /// Note: this uses a basic tone-mapping algorithm and may not produce optimal results for all content.
    /// Note: color profile, transfer, and matrix information will be updated if needed to reflect the remapping (but the file will still have color metadata).
    /// Default is <see langword="true" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool RemapHDRToSDR { get; init; } = true;
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to force square pixels in the output video streams.
    /// Default is <see langword="true" />.
    /// Note: when set to <see langword="true" />, if the source video has non-square pixels (i.e., a sample aspect ratio different from 1:1), the frame will
    /// be rescaled to have square pixels.
    /// Note: when set to <see langword="false" />, scaling the frame will preserve the original display aspect ratio by adjusting the pixel dimensions and
    /// sample aspect ratio accordingly.
    /// Note: most photo viewers and web browsers expect images to have square pixels, so keeping this as <see langword="true" /> may improve compatibility.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceSquarePixels { get; init; } = true;
#pragma warning restore SA1623 // Property summary documentation should match accessors
}
