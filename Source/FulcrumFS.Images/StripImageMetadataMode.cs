namespace FulcrumFS.Images;

/// <summary>
/// Specifies the mode for stripping metadata from images during processing.
/// </summary>
public enum StripImageMetadataMode
{
    /// <summary>
    /// Do not strip any metadata from the image.
    /// </summary>
    None,

    /// <summary>
    /// Strip only thumbnail metadata from the image.
    /// </summary>
    ThumbnailOnly,

    /// <summary>
    /// Strip all metadata from the image (except for the EXIF orientation, if it is required to properly orient the image). Use in combination with the <see
    /// cref="ImageProcessor.OrientToNormal"/> option if you want to ensure EXIF orientation metadata is also removed.
    /// </summary>
    All,
}
