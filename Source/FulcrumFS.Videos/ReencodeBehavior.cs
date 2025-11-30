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
    /// Only re-encode the stream to meet criteria such as <see cref="VideoStreamProcessingOptions.ResizeOptions" />.
    /// </summary>
    IfNeeded,

    /// <summary>
    /// Re-encode the stream only if it ends up smaller, and additionally if needed to make a valid codec for the result container format.
    /// Note: also includes the cases that <see cref="IfNeeded" /> would re-encode unconditionally.
    /// </summary>
    IfSmaller,
}
