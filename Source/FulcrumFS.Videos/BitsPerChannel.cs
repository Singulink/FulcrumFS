namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the bits per channel to use for video encoding.
/// </summary>
public enum BitsPerChannel
{
    /// <summary>
    /// Preserve the original bits per channel.
    /// </summary>
    Preserve,

    /// <summary>
    /// 8 bits per channel.
    /// </summary>
    Bits8,

    /// <summary>
    /// 10 bits per channel.
    /// </summary>
    Bits10,

    /// <summary>
    /// 12 bits per channel.
    /// </summary>
    Bits12,
}
