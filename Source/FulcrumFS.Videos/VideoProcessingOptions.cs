using System.Collections.Immutable;
using FulcrumFS.Internals;
using Singulink.Enums;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for configuring <see cref="VideoProcessor" />'s video file processing.
/// </summary>
public sealed record VideoProcessingOptions
{
    // Private constructor for our pre-defined configs.
    private VideoProcessingOptions()
    {
    }

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessingOptions"/> that always re-encodes to standardized H.264 video stream/s (60fps max, 8 bits per
    /// channel, 4:2:0 chroma subsampling, SDR, square pixels, and progressive frames) and standardized AAC audio stream/s (48kHz max, stereo max) in an MP4
    /// container, while attempting to preserve all metadata other than thumbnails by default. Does not preserve unrecognized streams.
    /// Note: to ensure you get a stream that corresponds to a real level (e.g., 6.2), you must set the appropriate resolution limit (based on your frame rate
    /// limit) yourself via <see cref="ResizeOptions" />.
    /// </summary>
    public static VideoProcessingOptions StandardizedH264AACMP4 { get; } = new VideoProcessingOptions()
    {
        ResultVideoCodecs = [VideoCodec.H264],
        ResultAudioCodecs = [AudioCodec.AAC],
        ResultFormats = [MediaContainerFormat.MP4],
        ForceProgressiveDownload = true,
        TryPreserveUnrecognizedStreams = false,
        MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
        VideoReencodeMode = StreamReencodeMode.Always,
        AudioReencodeMode = StreamReencodeMode.Always,
        MaximumBitsPerChannel = BitsPerChannel.Bits8,
        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
        FpsOptions = new(VideoFpsMode.LimitByIntegerDivision, 60),
        RemapHDRToSDR = true,
        MaxChannels = AudioChannels.Stereo,
        MaxSampleRate = AudioSampleRate.Hz48000,
        ForceSquarePixels = true,
        ForceProgressiveFrames = true,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessingOptions"/> that always re-encodes to standardized HEVC video stream/s (60fps max, 8 bits per
    /// channel, 4:2:0 chroma subsampling, SDR, square pixels, progressive frames, and hvc1 tag) and standardized AAC audio stream/s (48kHz max, stereo max) in
    /// an MP4 container, while attempting to preserve all metadata other than thumbnails by default. Does not preserve unrecognized streams.
    /// Note: to ensure you get a stream that corresponds to a real level (e.g., 6.2), you must set the appropriate resolution limit (based on your frame rate
    /// limit) yourself via <see cref="ResizeOptions" />.
    /// </summary>
    public static VideoProcessingOptions StandardizedHEVCAACMP4 { get; } = new VideoProcessingOptions()
    {
        ResultVideoCodecs = [VideoCodec.HEVC],
        ResultAudioCodecs = [AudioCodec.AAC],
        ResultFormats = [MediaContainerFormat.MP4],
        ForceProgressiveDownload = true,
        TryPreserveUnrecognizedStreams = false,
        MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
        VideoReencodeMode = StreamReencodeMode.Always,
        AudioReencodeMode = StreamReencodeMode.Always,
        MaximumBitsPerChannel = BitsPerChannel.Bits8,
        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
        FpsOptions = new(VideoFpsMode.LimitByIntegerDivision, 60),
        RemapHDRToSDR = true,
        MaxChannels = AudioChannels.Stereo,
        MaxSampleRate = AudioSampleRate.Hz48000,
        ForceSquarePixels = true,
        ForceProgressiveFrames = true,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessingOptions"/> that always preserves the original streams and file when possible.
    /// </summary>
    public static VideoProcessingOptions Preserve { get; } = new VideoProcessingOptions()
    {
        ResultVideoCodecs = VideoCodec.AllSourceCodecs,
        ResultAudioCodecs = AudioCodec.AllSourceCodecs,
        ResultFormats = MediaContainerFormat.AllSourceFormats,
        ForceProgressiveDownload = false,
        TryPreserveUnrecognizedStreams = true,
        MetadataStrippingMode = VideoMetadataStrippingMode.None,
        VideoReencodeMode = StreamReencodeMode.AvoidReencoding,
        AudioReencodeMode = StreamReencodeMode.AvoidReencoding,
        MaximumBitsPerChannel = BitsPerChannel.Preserve,
        MaximumChromaSubsampling = ChromaSubsampling.Preserve,
        FpsOptions = null,
        RemapHDRToSDR = false,
        MaxChannels = AudioChannels.Preserve,
        MaxSampleRate = AudioSampleRate.Preserve,
        ForceSquarePixels = false,
        ForceProgressiveFrames = false,
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
            ImmutableArray<VideoCodec> result = [.. value];

            if (result.Length is 0)
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Length)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            field = new EquatableArray<VideoCodec>(result);
        }
    } = new EquatableArray<VideoCodec>([.. VideoCodec.AllSourceCodecs]);

    /// <summary>
    /// Gets the source audio codecs for this mapping (it matches any of these, but all streams must match).
    /// Default is <see cref="AudioCodec.AllSourceCodecs" />.
    /// </summary>
    public IReadOnlyList<AudioCodec> SourceAudioCodecs
    {
        get;
        init
        {
            ImmutableArray<AudioCodec> result = [.. value];

            if (result.Length is 0)
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Length)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            field = new EquatableArray<AudioCodec>(result);
        }
    } = new EquatableArray<AudioCodec>([.. AudioCodec.AllSourceCodecs]);

    /// <summary>
    /// Gets or initializes the allowable result video codecs.
    /// Any streams of the video not matching one of these codecs will be re-encoded to use one of them.
    /// Video streams already using one of these codecs may be copied without re-encoding, depending on <see cref="VideoReencodeMode" />.
    /// When video streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Providing an empty list is not allowed.
    /// </summary>
    public required IReadOnlyList<VideoCodec> ResultVideoCodecs
    {
        get;
        init
        {
            ImmutableArray<VideoCodec> result = [.. value];

            if (!result.Any())
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Length)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            if (!result[0].SupportsEncoding)
                throw new ArgumentException("The first codec in the list must support encoding.", nameof(value));

            field = new EquatableArray<VideoCodec>(result);
        }
    }

    /// <summary>
    /// Gets or initializes the allowable result audio codecs.
    /// Any streams of the audio not matching one of these codecs will be re-encoded to use one of them.
    /// Audio streams already using one of these codecs may be copied without re-encoding, depending on <see cref="AudioReencodeMode" />.
    /// When audio streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Providing an empty list is not allowed.
    /// </summary>
    public required IReadOnlyList<AudioCodec> ResultAudioCodecs
    {
        get;
        init
        {
            ImmutableArray<AudioCodec> result = [.. value];

            if (!result.Any())
                throw new ArgumentException("Codecs cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Codecs cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Length)
                throw new ArgumentException("Codecs cannot contain duplicates.", nameof(value));

            if (!result[0].SupportsEncoding)
                throw new ArgumentException("The first codec in the list must support encoding.", nameof(value));

            field = new EquatableArray<AudioCodec>(result);
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
            ImmutableArray<MediaContainerFormat> result = [.. value];

            if (result.Length is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Length)
                throw new ArgumentException("Formats cannot contain duplicates.", nameof(value));

            field = new EquatableArray<MediaContainerFormat>(result);
        }
    } = new EquatableArray<MediaContainerFormat>([.. MediaContainerFormat.AllSourceFormats]);

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
            ImmutableArray<MediaContainerFormat> result = [.. value];

            if (result.Length is 0)
                throw new ArgumentException("Formats cannot be empty.", nameof(value));

            if (result.Any((x) => x is null))
                throw new ArgumentException("Formats cannot contain null values.", nameof(value));

            if (result.Distinct().Count() != result.Length)
                throw new ArgumentException("Formats cannot contain duplicates.", nameof(value));

            if (!result[0].SupportsWriting)
                throw new ArgumentException("The first format in the list must support writing.", nameof(value));

            field = new EquatableArray<MediaContainerFormat>(result);
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
    /// Gets or initializes a value indicating whether to try to preserve unrecognized streams in the output video.
    /// Each unrecognised stream that is not compatible with the output container format will be dropped when <see langword="true" />, or all will be dropped
    /// when <see langword="false" />.
    /// Note: unrecognized streams include attachments, subtitles, data streams, and any streams not recognized by ffmpeg. We reserve the right to recognize
    /// additional stream types in the future, such as subtitle streams.
    /// Note: some metadata might appear as unrecognized streams (e.g., as a data stream).
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool TryPreserveUnrecognizedStreams { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata and/or thumbnails from this file.
    /// Note: metadata includes container metadata, stream metadata, chapters, stream groups and programs; but does not include side data (including rotation
    /// info / transformation matrix), attachments, and dispositions.
    /// Note: currently stream groups and programs do not always copy correctly in preserving metadata mode.
    /// Note: also, not all "metadata" is successfully copied either, some fields may be missing, changed, or added, even in preserve mode.
    /// Note: incompatible/unrecognized metadata may be remapped on a best-effort basis, may be lost, or might cause an exception to be thrown when remuxing.
    /// Note: attachments currently show up under unrecognized streams, so they are currently controlled by <see cref="TryPreserveUnrecognizedStreams" />
    /// between different container formats or re-encoding streams.
    /// Note: metadata is correctly preserved when the source file is copied as-is, but in other cases, metadata copying is subject to the aforementioned
    /// considerations.
    /// Note: some metadata might appear as unrecognized streams (e.g., as a data stream), which is controlled by
    /// <see cref="TryPreserveUnrecognizedStreams" />.
    /// </summary>
    public VideoMetadataStrippingMode MetadataStrippingMode
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
    /// Gets or initializes a value indicating whether to force validation of all streams, as opposed to assuming they're valid.
    /// This can be useful to catch potentially invalid streams or other issues that might cause problems during playback, or potentially indicate a malicious
    /// stream (e.g., maybe some players interpret that particular invalid sequence in a problematic way), but has a significant performance impact (i.e.,
    /// slower processing) due to requiring to decode all streams, compared to assuming they're valid and using the copy codec where possible (the performance
    /// difference is lower as a percent when re-encoding is already required).
    /// Note: when enabled, duration is also validated based on the actual measured duration of each stream, rather than just the claimed duration.
    /// It can be safe to disable this when the source video is from a trusted source, if you prefer better performance over ensuring catching potential
    /// errors.
    /// Default is <see langword="true" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceValidateAllStreams { get; init; } = true;
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to remove all audio streams from the output video.
    /// Default is <see langword="false" />.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool RemoveAudioStreams { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes the progress callback, which gets invoked with the current approximate progress (between 0.0 and 1.0).
    /// Default is <see langword="null" />.
    /// </summary>
    public Func<(FileId FileId, string? VariantId), double, ValueTask>? ProgressCallback { get; init; }

    /// <summary>
    /// Gets or initializes the behavior for re-encoding video streams.
    /// </summary>
    public StreamReencodeMode VideoReencodeMode
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
    public StreamReencodeMode AudioReencodeMode
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
    public VideoCompressionLevel VideoCompressionLevel
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

    /// <summary>
    /// Gets or initializes a value indicating whether to force square pixels in the output video streams.
    /// Note: when set to <see langword="true" />, if the source video has non-square pixels (i.e., a sample aspect ratio different from 1:1), the video will
    /// be rescaled to have square pixels.
    /// Note: when set to <see langword="false" />, scaling the video will preserve the original display aspect ratio by adjusting the pixel dimensions and
    /// sample aspect ratio accordingly.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceSquarePixels { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to force progressive frames in the output video streams, as opposed to interlaced frames.
    /// Note: when set to <see langword="true" />, any interlaced video in the source will be deinterlaced.
    /// Note: when set to <see langword="false" />, the original video may still be deinterlaced if deemed necessary by the processing pipeline, e.g., for
    /// scaling.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ForceProgressiveFrames { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

#if DEBUG
    // Internal property to force usage of libfdk_aac or native aac encoding for testing purposes:
    internal bool? ForceLibFDKAACUsage { get; set; }
#endif
}
