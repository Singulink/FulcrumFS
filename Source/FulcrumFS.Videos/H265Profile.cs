namespace FulcrumFS.Videos;

/// <summary>
/// The H.265 profile to use for video encoding.
/// </summary>
public enum H265Profile
{
    /// <summary>
    /// Main profile for H.265 encoding - 8 bits per channel, 4:2:0 chroma subsampling.
    /// </summary>
    Main8Bit420,

    /// <summary>
    /// Main intra profile for H.265 encoding - 8 bits per channel, 4:2:0 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main8Bit420Intra,

    /// <summary>
    /// Main 4:4:4 profile for H.265 encoding - 8 bits per channel, 4:4:4 chroma subsampling.
    /// </summary>
    Main8Bit444,

    /// <summary>
    /// Main intra 4:4:4 profile for H.265 encoding - 8 bits per channel, 4:4:4 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main8Bit444Intra,

    /// <summary>
    /// Main 10-bit profile for H.265 encoding - supports 10 bits per channel, 4:2:0 chroma subsampling.
    /// </summary>
    Main10Bit420,

    /// <summary>
    /// Main 10-bit intra profile for H.265 encoding - supports 10 bits per channel, 4:2:0 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main10Bit420Intra,

    /// <summary>
    /// Main 10-bit 4:2:2 profile for H.265 encoding - supports 10 bits per channel, 4:2:2 chroma subsampling.
    /// </summary>
    Main10Bit422,

    /// <summary>
    /// Main 10-bit intra 4:2:2 profile for H.265 encoding - supports 10 bits per channel, 4:2:2 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main10Bit422Intra,

    /// <summary>
    /// Main 10-bit 4:4:4 profile for H.265 encoding - supports 10 bits per channel, 4:4:4 chroma subsampling.
    /// </summary>
    Main10Bit444,

    /// <summary>
    /// Main 10-bit intra 4:4:4 profile for H.265 encoding - supports 10 bits per channel, 4:4:4 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main10Bit444Intra,

    /// <summary>
    /// Main 12-bit profile for H.265 encoding - supports 12 bits per channel, 4:2:0 chroma subsampling.
    /// </summary>
    Main12Bit420,

    /// <summary>
    /// Main 12-bit intra profile for H.265 encoding - supports 12 bits per channel, 4:2:0 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main12Bit420Intra,

    /// <summary>
    /// Main 12-bit 4:2:2 profile for H.265 encoding - supports 12 bits per channel, 4:2:2 chroma subsampling.
    /// </summary>
    Main12Bit422,

    /// <summary>
    /// Main 12-bit intra 4:2:2 profile for H.265 encoding - supports 12 bits per channel, 4:2:2 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main12Bit422Intra,

    /// <summary>
    /// Main 12-bit 4:4:4 profile for H.265 encoding - supports 12 bits per channel, 4:4:4 chroma subsampling.
    /// </summary>
    Main12Bit444,

    /// <summary>
    /// Main 12-bit intra 4:4:4 profile for H.265 encoding - supports 12 bits per channel, 4:4:4 chroma subsampling, disallows inter-frame prediction.
    /// </summary>
    Main12Bit444Intra,
}
