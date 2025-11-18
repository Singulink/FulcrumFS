namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for processing an audio stream.
/// </summary>
public class AudioStreamProcessingOptions
{
    /// <summary>
    /// Gets the options that indicate to copy the codec without re-encoding.
    /// </summary>
    public static AudioStreamProcessingOptions CopyCodecOptions { get; } = new();

    /// <summary>
    /// Gets the options that indicate to remove this stream.
    /// </summary>
    public static AudioStreamProcessingOptions RemoveStreamOptions { get; } = new() { ShouldRemove = true };

    /// <summary>
    /// Gets or initializes the result video codec for this mapping, or null for copy.
    /// Note: if using the copy codec, options that require re-encoding will be ignored.
    /// </summary>
    public VideoCodec? ResultCodec { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to remove this stream.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ShouldRemove { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to copy the stream without re-encoding if it makes the final file smaller overall than otherwise.
    /// Default is false.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public bool ShouldCopyIfLarger { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata from this stream.
    /// If unset, metadata preservation is undefined, and it may only be partially preserved.
    /// Note: if stripping metadata is enabled globally, it will be removed regardless.
    /// </summary>
    public bool? StripMetadata { get; init; }

    /// <summary>
    /// Gets or intializes a value indicating how to re-order this stream.
    /// If set, adjusts the index of this stream within its stream type (video) by the specified amount with respect to other streams of the same type;
    /// if movement exceeds the maximum amount possible, it is clamped.
    /// The re-ordering is performed after any streams with <see cref="ShouldRemove" /> have been removed, and is performed in order for each stream as per the
    /// original order.
    /// </summary>
    public int? IndexAdjustmentWithinStreamType { get; init; }

    /// <summary>
    /// Gets or initializes maximum bitrate of the stream (in bits / second).
    /// For AAC and MP3 audio, this corresponds to the bit rate in CBR (constant bit rate) mode.
    /// </summary>
    public int? MaximumBitRate { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether to ensure the 'moov atom' in MP4 audio (AAC) is at the start.
    /// This option is ignored for unsupported codecs.
    /// Note: this option is compatible with the copy codec.
    /// Note: this does not enable true streaming, but it does allow playback to begin before the entire stream is downloaded.
    /// </summary>
    public bool? ForceProgressiveDownload { get; init; }

    /// <summary>
    /// Gets or initializes the encoder to use for this stream, or null for default.
    /// Note: some codecs may support multiple encoders, such as AAC - the encoder must be valid for the codec, otherwise the default one will be used.
    /// </summary>
    public AudioCodecEncoder? Encoder { get; init; }

    /// <summary>
    /// Gets or initializes the quality value (-qscale) to use when encoding this stream, or null for default.
    /// Note: the specific meaning and range of meaningful values depends on the codec and encoder used.
    /// </summary>
    public int? Quality { get; init; }

    /// <summary>
    /// Gets or initializes the maximum number of audio channels to include in the output stream, or null for default (i.e., same as source, up to max the
    /// result codec can support).
    /// Note: if the source has more channels than this value, channels will be downmixed.
    /// </summary>
    public int? MaxChannels { get; init; }

    /// <summary>
    /// Gets or initializes the cutoff frequency for the libfdk_aac encoder (other encoders currently ignore this value).
    /// By default, it is ~14kHz.
    /// </summary>
    public int? CutoffFrequency { get; init; }

    /// <summary>
    /// Gets or initializes the sample rate (in Hz) for the audio stream, or <see langword="null" />  for default (same as source, if the result codec supports
    /// it).
    /// Common values these days are 44.1kHz (44100), 48kHz (48000), 92kHz (92000), and 192kHz (192000).
    /// </summary>
    public int? SampleRate { get; init; }
}
