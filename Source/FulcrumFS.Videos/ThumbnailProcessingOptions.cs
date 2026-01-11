namespace FulcrumFS.Videos;

#pragma warning disable SA1623 // Property summary documentation should match accessors

/// <summary>
/// <para>
/// Specifies the options for processing videos to create a thumbnail with a <see cref="ThumbnailProcessor" />.</para>
/// <para>
/// If <see cref="IncludeThumbnailVideoStreams" /> is <see langword="true" /> and there is a video stream marked as a thumbnail stream, the thumbnail image is
/// taken from that stream.</para>
/// <para>
/// Otherwise, the thumbnail image is taken from the earlier of <see cref="ImageTimestamp" /> and <see cref="ImageTimestampFraction" /> if both are specified
/// and in range (this allows specifying options that work well for both long and short videos, e.g., by specifying 5s and 30%, for short videos 5s may be way
/// too far in, it could be the end of the video so it takes 30%, and for long videos 30% could be way too far in, whereas 5s would be better).</para>
/// <para>
/// The thumbnail image is taken from the first video stream that is most likely to be the main video based on dispositions.</para>
/// <para>
/// The default format for the extracted thumbnail image is PNG.</para>
/// <para>
/// The default options result in no thumbnail extraction options being applied.</para>
/// <para>
/// Note: if no thumbnail is able to be extracted, then a <see cref="ThumbnailSelectingException" /> will be thrown when processing.</para>
/// <para>
/// Note: does not attempt to remove alpha channels, nor attempt to reduce bit depth to 8; callers can utilize additional image processing if needed for these.
/// </para>
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
    /// <para>
    /// Gets or initializes a value indicating whether video streams marked as thumbnail streams should be considered for thumbnail extraction.</para>
    /// <para>
    /// Default is <see langword="false" />, meaning this option is not used.</para>
    /// </summary>
    public bool IncludeThumbnailVideoStreams { get; init; }

    /// <summary>
    /// <para>
    /// Gets or initializes the timestamp within the video at which to capture the thumbnail image.</para>
    /// <para>
    /// Default is <see langword="null" />, meaning this option is not used.</para>
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
    /// <para>
    /// Gets or initializes the fraction of the video's duration at which to capture the thumbnail image.</para>
    /// <para>
    /// Default is <see langword="null" />, meaning this option is not used.</para>
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
    /// <para>
    /// Gets or initializes a value indicating whether to remap HDR to SDR for the thumbnail.</para>
    /// <para>
    /// Note: this uses a basic tone-mapping algorithm and may not produce optimal results for all content.</para>
    /// <para>
    /// Note: color profile, transfer, and matrix information will be updated if needed to reflect the remapping (but the file will still have color metadata).
    /// </para><para>
    /// Default is <see langword="true" />.</para>
    /// </summary>
    public bool RemapHDRToSDR { get; init; } = true;

    /// <summary>
    /// <para>
    /// Gets or initializes a value indicating whether to force square pixels in the output video streams.</para>
    /// <para>
    /// Default is <see langword="true" />.</para>
    /// <para>
    /// Note: when set to <see langword="true" />, if the source video has non-square pixels (i.e., a sample aspect ratio different from 1:1), the frame will
    /// be rescaled to have square pixels.</para>
    /// <para>
    /// Note: when set to <see langword="false" />, scaling the frame will preserve the original display aspect ratio by adjusting the pixel dimensions and
    /// sample aspect ratio accordingly.</para>
    /// <para>
    /// Note: most photo viewers and web browsers expect images to have square pixels, so keeping this as <see langword="true" /> may improve compatibility.
    /// </para>
    /// </summary>
    public bool ForceSquarePixels { get; init; } = true;
}
