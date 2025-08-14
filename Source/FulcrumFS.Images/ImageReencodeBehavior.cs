namespace FulcrumFS.Images;

/// <summary>
/// Specifies the behavior for re-encoding images, determining whether and how an image should be re-encoded based on changes and size considerations.
/// </summary>
/// <remarks>
/// Re-encoded results are always discarded in favor of the source image if the source and result formats are the same, no changes were made to the image data
/// or metadata, and the re-encoded image is larger in size than the source.
/// </remarks>
public enum ImageReencodeBehavior
{
    /// <summary>
    /// Always re-encode the image (unless re-encoding is known to have no effect on the output, both in terms of content and size).
    /// </summary>
    Always,

    /// <summary>
    /// Skip re-encoding entirely if no changes were effectively made to the image format, quality, data or metadata. Using this option means that the image
    /// will not be re-encoded if the compression level is different from the source image.
    /// </summary>
    SkipIfUnchanged,

    /// <summary>
    /// Discard the re-encoded result if it is larger in size than the source, the formats are the same and no changes were made to the image data. Only use
    /// this mode if metadata changes (e.g., metadata stripping) are not important (e.g. they are only stripped for size optimization and not privacy) and you
    /// want to keep the source image if it was encoded more efficiently than the re-encoded image.
    /// </summary>
    DiscardIfLarger,
}
