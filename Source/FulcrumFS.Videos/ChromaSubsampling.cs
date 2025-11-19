namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the chroma subsampling format to use for video encoding.
/// </summary>
public enum ChromaSubsampling
{
    /// <summary>
    /// 4:2:0 chroma subsampling - each group of four pixels (2x2) shares chroma information.
    /// </summary>
    Subsampling420,

    /// <summary>
    /// 4:2:2 chroma subsampling - each pair of pixels (horizontally) shares chroma information.
    /// </summary>
    Subsampling422,

    /// <summary>
    /// 4:4:4 chroma subsampling - every individual pixel has its own color information.
    /// </summary>
    Subsampling444,
}
