using Singulink.Enums;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for configuring <see cref="VideoProcessor" />.
/// </summary>
public sealed record VideoProcessorOptions
{
    // Private constructor for our pre-defined configs.
    private VideoProcessorOptions()
    {
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessorOptions"/> that always re-encodes to standardized H.264 video stream/s (60fps max, 8 bits per
    /// channel, 4:2:0 chroma subsampling, and SDR) and standardized AAC audio stream/s (48kHz max, stereo max) in an MP4 container, while attempting to
    /// preserve all metadata other than thumbnails by default.
    /// </summary>
    public static VideoProcessorOptions StandardizedH264AACMP4 { get; } = new VideoProcessorOptions()
    {
        ResultVideoCodecs = [VideoCodec.H264],
        ResultAudioCodecs = [AudioCodec.AAC],
        ResultFormats = [MediaContainerFormat.MP4],
        ForceProgressiveDownload = true,
        PreserveUnrecognizedStreams = null,
        StripMetadata = StripVideoMetadataMode.ThumbnailOnly,
        VideoReencodeBehavior = VideoReencodeBehavior.Always,
        AudioReencodeBehavior = VideoReencodeBehavior.Always,
        MaximumBitsPerChannel = BitsPerChannel.Bits8,
        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
        FpsOptions = new(VideoFpsMode.LimitByIntegerDivision, 60),
        RemapHDRToSDR = true,
        MaxChannels = AudioChannels.Stereo,
        MaxSampleRate = AudioSampleRate.Hz48000,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessorOptions"/> that always preserves the original streams when possible.
    /// </summary>
    public static VideoProcessorOptions Preserve { get; } = new VideoProcessorOptions()
    {
        ResultVideoCodecs = VideoCodec.AllSourceCodecs,
        ResultAudioCodecs = AudioCodec.AllSourceCodecs,
        ResultFormats = MediaContainerFormat.AllSourceFormats,
        ForceProgressiveDownload = false,
        PreserveUnrecognizedStreams = true,
        StripMetadata = StripVideoMetadataMode.ThumbnailOnly,
        VideoReencodeBehavior = VideoReencodeBehavior.AvoidReencoding,
        AudioReencodeBehavior = VideoReencodeBehavior.AvoidReencoding,
        MaximumBitsPerChannel = BitsPerChannel.Preserve,
        MaximumChromaSubsampling = ChromaSubsampling.Preserve,
        FpsOptions = null,
        RemapHDRToSDR = false,
        MaxChannels = AudioChannels.Preserve,
        MaxSampleRate = AudioSampleRate.Preserve,
    };

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
    /// Gets or initializes the allowable result video codecs.
    /// Any streams of the video not matching one of these codecs will be re-encoded to use one of them.
    /// Video streams already using one of these codecs may be copied without re-encoding, depending on <see cref="VideoReencodeBehavior" />.
    /// When video streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Providing an empty list is not allowed.
    /// </summary>
    public required IReadOnlyList<VideoCodec> ResultVideoCodecs
    {
        get;
        init
        {
            IReadOnlyList<VideoCodec> result;

            result = [.. value];

            if (!result.Any())
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Count)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            if (!result[0].SupportsEncoding)
                throw new ArgumentException("The first codec in the list must support encoding.", nameof(value));

            field = result;
        }
    }

    /// <summary>
    /// Gets or initializes the allowable result audio codecs.
    /// Any streams of the audio not matching one of these codecs will be re-encoded to use one of them.
    /// Audio streams already using one of these codecs may be copied without re-encoding, depending on <see cref="AudioReencodeBehavior" />.
    /// When audio streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Providing an empty list is not allowed.
    /// </summary>
    public required IReadOnlyList<AudioCodec> ResultAudioCodecs
    {
        get;
        init
        {
            IReadOnlyList<AudioCodec> result;

            result = [.. value];

            if (!result.Any())
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Count)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            if (!result[0].SupportsEncoding)
                throw new ArgumentException("The first codec in the list must support encoding.", nameof(value));

            field = result;
        }
    }

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
    /// Gets the result media container format for this mapping, or null to use the same as the input.
    /// If re-encoding is required, or other modifications (e.g., metadata changes) are requested, and the source format does not support writing, the first
    /// format in the list is used, so it must be writable as per <see cref="MediaContainerFormat.SupportsWriting" /> - otherwise, non-writable formats will
    /// only be emitted when copying the file in full.
    /// </summary>
    public required IReadOnlyList<MediaContainerFormat> ResultFormats
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
    }

    /// <summary>
    /// Gets or initializes a value indicating whether to ensure the 'moov atom' in an MP4 file is at the beginning of the file.
    /// Note: this does not enable true streaming, but it does allow playback to begin before the entire streams are downloaded.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceProgressiveDownload { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to preserve unrecognized streams in the output video.
    /// Note: <see langword="null" /> means that unrecognized streams are removed when remuxing only - however more streams (e.g., subtitle streams) may become
    /// recognized in the future.
    /// </summary>
    public bool? PreserveUnrecognizedStreams { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata and/or thumbnails from this file.
    /// Note: metadata includes container metadata, stream metadata, chapters, stream groups and programs; but does not include side data, attachments, and
    /// dispositions.
    /// Note: currently stream groups and programs do not always copy correctly in preserving metadata mode.
    /// Note: also, not all "metadata" is successfully copied either, some fields may be missing, changed, or added, even in preserve mode.
    /// Note: incompatible/unrecognized metadata may be remapped on a best-effort basis, may be lost, or might cause an exception to be thrown when remuxing.
    /// Note: attachments currently show up under unrecognized streams, so they are currently controlled by <see cref="PreserveUnrecognizedStreams" />
    /// between different container formats or re-encoding streams.
    /// Note: metadata is correctly preserved when the source file is copied as-is, but in other cases, metadata copying is subject to the aforementioned
    /// considerations.
    /// </summary>
    public StripVideoMetadataMode StripMetadata
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the options for validating audio streams in the source video before processing.
    /// Default is <see cref="AudioStreamValidationOptions.None" />.
    /// </summary>
    public AudioStreamValidationOptions AudioSourceValidation
    {
        get;
        init
        {
            if (value.MinLength.HasValue && value.MaxLength.HasValue && value.MinLength.Value > value.MaxLength.Value)
            {
                throw new ArgumentException("MinLength cannot be greater than MaxLength.", nameof(value));
            }

            if (value.MinStreams.HasValue && value.MaxStreams.HasValue && value.MinStreams.Value > value.MaxStreams.Value)
            {
                throw new ArgumentException("MinStreams cannot be greater than MaxStreams.", nameof(value));
            }

            field = value;
        }
    } = AudioStreamValidationOptions.None;

    /// <summary>
    /// Gets or initializes the options for validating video streams in the source video before processing.
    /// Default is <see cref="VideoStreamValidationOptions.None" />.
    /// </summary>
    public VideoStreamValidationOptions VideoSourceValidation
    {
        get;
        init
        {
            if (value.MinWidth.HasValue && value.MaxWidth.HasValue && value.MinWidth.Value > value.MaxWidth.Value)
            {
                throw new ArgumentException("MinWidth cannot be greater than MaxWidth.", nameof(value));
            }

            if (value.MinHeight.HasValue && value.MaxHeight.HasValue && value.MinHeight.Value > value.MaxHeight.Value)
            {
                throw new ArgumentException("MinHeight cannot be greater than MaxHeight.", nameof(value));
            }

            if (value.MinPixels.HasValue && value.MaxPixels.HasValue && value.MinPixels.Value > value.MaxPixels.Value)
            {
                throw new ArgumentException("MinPixels cannot be greater than MaxPixels.", nameof(value));
            }

            if (value.MinLength.HasValue && value.MaxLength.HasValue && value.MinLength.Value > value.MaxLength.Value)
            {
                throw new ArgumentException("MinLength cannot be greater than MaxLength.", nameof(value));
            }

            if (value.MinStreams.HasValue && value.MaxStreams.HasValue && value.MinStreams.Value > value.MaxStreams.Value)
            {
                throw new ArgumentException("MinStreams cannot be greater than MaxStreams.", nameof(value));
            }

            field = value;
        }
    } = VideoStreamValidationOptions.None;

    /// <summary>
    /// Gets or initializes the progress callback, which gets invoked with the current approximate progress (between 0.0 and 1.0).
    /// Default is <see langword="null" />.
    /// </summary>
    public Action<(FileId FileId, string? VariantId), double>? ProgressCallback { get; init; }

    /// <summary>
    /// Gets or initializes the behavior for re-encoding video streams.
    /// </summary>
    public VideoReencodeBehavior VideoReencodeBehavior
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the behavior for re-encoding audio streams.
    /// </summary>
    public VideoReencodeBehavior AudioReencodeBehavior
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the quality to use for video encoding.
    /// Default is <see cref="VideoQuality.Medium" />.
    /// </summary>
    public VideoQuality VideoQuality
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the quality to use for audio encoding.
    /// Default is <see cref="AudioQuality.Medium" />.
    /// </summary>
    public AudioQuality AudioQuality
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum bits per channel to use for video encoding.
    /// Default is <see cref="BitsPerChannel.Bits8" />.
    /// </summary>
    public BitsPerChannel MaximumBitsPerChannel
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum chroma subsampling to use for video encoding.
    /// Default is <see cref="ChromaSubsampling.Subsampling420" />.
    /// </summary>
    public ChromaSubsampling MaximumChromaSubsampling
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the compression level to use for video encoding.
    /// Note: does not affect quality, only affects file size and encoding speed trade-off.
    /// Default is <see cref="VideoCompressionLevel.Medium" />.
    /// </summary>
    public VideoCompressionLevel VideoCompressionPreference
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the options for resizing the video.
    /// If set to <see langword="null" />, the video will not be resized.
    /// Default is <see langword="null" />.
    /// </summary>
    public VideoResizeOptions? ResizeOptions { get; init; }

    /// <summary>
    /// Gets or initializes the options for limiting the frames per second (FPS) of the video.
    /// If set to <see langword="null" />, the video will not be resampled.
    /// </summary>
    public VideoFpsOptions? FpsOptions { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to remap HDR to SDR for any HDR streams.
    /// Note: this uses a basic tone-mapping algorithm and may not produce optimal results for all content.
    /// Note: if the video is re-encoded, it will always be converted to SDR regardless of this setting, as currently we do not support encoding to HDR.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool RemapHDRToSDR { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes the maximum number of audio channels to include in the output stream.
    /// Note: if the source has more channels than this value, channels will be downmixed.
    /// </summary>
    public AudioChannels MaxChannels
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum sample rate (in Hz) for the audio stream (any rates above this will be downsampled).
    /// </summary>
    public AudioSampleRate MaxSampleRate
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }
}
