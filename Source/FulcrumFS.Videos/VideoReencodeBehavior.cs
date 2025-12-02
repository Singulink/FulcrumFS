namespace FulcrumFS.Videos;

/// <summary>
/// Defines the behavior for re-encoding video or audio streams.
/// </summary>
public enum VideoReencodeBehavior
{
    /// <summary>
    /// Always re-encode the stream.
    /// </summary>
    Always,

    /// <summary>
    /// Only re-encode the stream to meet criteria such as <see cref="VideoProcessorOptions.ResizeOptions" />, or to make a valid codec for the result
    /// container format.
    /// </summary>
    AvoidReencoding,

    /// <summary>
    /// Re-encode each stream only if it ends up smaller, or in cases when <see cref="AvoidReencoding" /> would re-encode.
    /// </summary>
    SelectSmallest,
}
