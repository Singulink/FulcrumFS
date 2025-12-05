namespace FulcrumFS.Videos;

/// <summary>
/// Specifies the chroma subsampling format to use for video encoding.
/// </summary>
public enum ChromaSubsampling
{
    /// <summary>
    /// Preserves the original chroma subsampling.
    /// </summary>
    Preserve,

    /// <summary>
    /// 4:2:0 chroma subsampling - each group of four pixels (2x2) shares chroma information - does not allow alpha, nor non-standard or non-YUV formats.
    /// </summary>
    Subsampling420,

    /// <summary>
    /// 4:2:2 chroma subsampling - each pair of pixels (horizontally) shares chroma information - does not allow alpha, nor non-standard or non-YUV formats.
    /// </summary>
    Subsampling422,

    /// <summary>
    /// 4:4:4 chroma subsampling - every individual pixel has its own color information - does not allow alpha, nor non-standard or non-YUV formats.
    /// </summary>
    Subsampling444,
}
