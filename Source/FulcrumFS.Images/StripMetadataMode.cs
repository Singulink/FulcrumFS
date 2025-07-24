namespace FulcrumFS.Images;

/// <summary>
/// Specifies the mode for stripping metadata from images during processing.
/// </summary>
public enum StripMetadataMode
{
    /// <summary>
    /// No metadata will be stripped from the image.
    /// </summary>
    None,

    /// <summary>
    /// All metadata will be stripped from the image, except for EXIF orientation if it is required for proper display.
    /// </summary>
    All,

    /// <summary>
    /// Only the thumbnail metadata will be stripped from the image, leaving all other metadata intact.
    /// </summary>
    ThumbnailOnly,
}
