namespace FulcrumFS.Videos;

/// <summary>
/// Defines the behavior for re-encoding video or audio streams.
/// </summary>
public enum ReencodeBehavior
{
    /// <summary>
    /// Always re-encode the stream.
    /// </summary>
    Always,

    /// <summary>
    /// Never re-encode the stream, unless needed to meet criteria such as <see cref="VideoStreamProcessingOptions.ResizeOptions" />.
    /// </summary>
    IfNeeded,

    /// <summary>
    /// Re-encode the stream only if it ends up smaller.
    /// </summary>
    IfSmaller,
}
