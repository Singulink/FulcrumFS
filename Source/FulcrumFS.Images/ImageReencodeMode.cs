namespace FulcrumFS.Images;

/// <summary>
/// Specifies the mode that is used to determine when to use re-encoded images versus original source files.
/// </summary>
public enum ImageReencodeMode
{
    /// <summary>
    /// Always re-encode the image and use the re-encoded output. The source image file is never returned directly.
    /// </summary>
    Always,

    /// <summary>
    /// Re-encode but prefer the source image file if it is smaller and no required changes were made to its metadata, file format or pixel data.
    /// </summary>
    SelectSmallest,

    /// <summary>
    /// Avoid re-encoding and use the source image file if it does not require changes to its metadata, file format or pixel data. When re-encoding is skipped,
    /// the source image is returned directly, so any quality or compression settings on the image processor are ignored.
    /// </summary>
    AvoidReencoding,
}
