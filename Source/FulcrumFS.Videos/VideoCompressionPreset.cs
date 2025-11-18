namespace FulcrumFS.Videos;

/// <summary>
/// Controls how long it takes to compress the video, and how small the file is - does not affect quality.
/// </summary>
public enum VideoCompressionPreset
{
    /// <summary>
    /// Fastest compression level, at the expense of much larger files.
    /// </summary>
    UltraFast,

    /// <summary>
    /// Second fastest compression level, at the expense of an even larger file than <see cref="VeryFast" />.
    /// </summary>
    SuperFast,

    /// <summary>
    /// Compression level that is faster, at the expense of a substantially larger file.
    /// </summary>
    VeryFast,

    /// <summary>
    /// Compression level that is about half-way between <see cref="VeryFast" /> and <see cref="Fast" />.
    /// </summary>
    Faster,

    /// <summary>
    /// Compression level that is slightly faster than the default at the expense of a small increase in file size.
    /// </summary>
    Fast,

    /// <summary>
    /// Default compression level.
    /// </summary>
    Medium,

    /// <summary>
    /// Compression level that is slower than <see cref="Medium" /> to save some file size, but not way slower.
    /// </summary>
    Slow,

    /// <summary>
    /// Compression level between <see cref="Slow" /> and <see cref="VerySlow" />.
    /// </summary>
    Slower,

    /// <summary>
    /// Slowest compression level that is still recommended for use - substantially slower than <see cref="Slower" /> for a minor size improvement.
    /// </summary>
    VerySlow,

    /// <summary>
    /// Slowest compression level - not recommended for use as the results are barely any better than <see cref="VerySlow" />, but takes much longer.
    /// </summary>
    Placebo,
}
