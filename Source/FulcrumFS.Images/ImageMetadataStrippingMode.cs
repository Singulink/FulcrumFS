namespace FulcrumFS.Images;

/// <summary>
/// Specifies the mode for stripping metadata from images during processing.
/// </summary>
public enum ImageMetadataStrippingMode
{
    /// <summary>
    /// Disables image metadata stripping.
    /// </summary>
    None,

    /// <summary>
    /// Stripping only thumbnail metadata from the image is preferred in order to reduce file size, but is not required. Other metadata should be preserved.
    /// </summary>
    ThumbnailOnly,

    /// <summary>
    /// Stripping all metadata from the image is preferred in order to reduce file size, but is not required.
    /// </summary>
    Preferred,

    /// <summary>
    /// Stripping all metadata from the image is required in order to protect privacy.
    /// </summary>
    Required,
}
