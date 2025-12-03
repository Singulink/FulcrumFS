namespace FulcrumFS.Images;

/// <summary>
/// Specifies the mode for re-orienting images.
/// </summary>
public enum ImageReorientMode
{
    /// <summary>
    /// Disable image re-orientation.
    /// </summary>
    None,

    /// <summary>
    /// Re-orienting the image to normal orientation based on its metadata is preferred.
    /// </summary>
    PreferNormal,

    /// <summary>
    /// Re-orienting the image to normal orientation based on its metadata is required.
    /// </summary>
    RequireNormal,
}
