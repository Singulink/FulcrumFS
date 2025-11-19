using Singulink.Enums;

namespace FulcrumFS.Videos;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents options for processing an audio stream.
/// </summary>
public class AudioStreamProcessingOptions
{
    /// <summary>
    /// Gets or initializes the allowable result audio codecs.
    /// Any streams of the audio not matching one of these codecs will be re-encoded to use one of them.
    /// Audio streams already using one of these codecs may be copied without re-encoding, depending on <see cref="ReencodeBehavior" />.
    /// When audio streams are re-encoded, they are re-encoded to the first codec in this list.
    /// Default is a list containing <see cref="AudioCodec.AAC" />.
    /// Providing an empty list is not allowed.
    /// Providing <see langword="null" /> indicates to preserve the audio streams as-is.
    /// </summary>
    public IReadOnlyList<AudioCodec>? ResultCodecs
    {
        get;
        init
        {
            if (value is null)
            {
                field = null;
                return;
            }

            IReadOnlyList<AudioCodec> result = [.. value];

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
    } = [AudioCodec.AAC];

    /// <summary>
    /// Gets or initializes the behavior for re-encoding the audio stream.
    /// Default is <see cref="ReencodeBehavior.Always" />.
    /// </summary>
    public ReencodeBehavior ReencodeBehavior
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = ReencodeBehavior.Always;

    /// <summary>
    /// Gets or initializes a value indicating whether to strip metadata from this stream.
    /// If set to <see langword="null" />, metadata preservation is undefined, and it may only be partially preserved.
    /// Note: if stripping metadata is enabled globally, it will be removed regardless.
    /// Default is <see langword="false" />.
    /// </summary>
    public bool? StripMetadata { get; init; } = false;

    /// <summary>
    /// Gets or initializes the quality to use for audio encoding.
    /// Default is <see cref="AudioQuality.Medium" />.
    /// </summary>
    public AudioQuality Quality
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = AudioQuality.Medium;

    /// <summary>
    /// Gets or initializes the maximum number of audio channels to include in the output stream.
    /// Note: if the source has more channels than this value, channels will be downmixed.
    /// Default is <see cref="AudioChannels.Stereo" />.
    /// </summary>
    public AudioChannels MaxChannels
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = AudioChannels.Stereo;

    /// <summary>
    /// Gets or initializes the maximum sample rate (in Hz) for the audio stream (any rates above this will be downsampled).
    /// Default is <see cref="AudioSampleRate.Hz48000" />.
    /// </summary>
    public AudioSampleRate SampleRate
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    } = AudioSampleRate.Hz48000;
}
