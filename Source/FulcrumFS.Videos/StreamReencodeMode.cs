namespace FulcrumFS.Videos;

/// <summary>
/// Defines the mode for re-encoding video or audio streams.
/// </summary>
public enum StreamReencodeMode
{
    /// <summary>
    /// Always re-encode the stream.
    /// </summary>
    Always,

    /// <summary>
    /// Re-encode each stream only if it ends up smaller, or in cases when <see cref="AvoidReencoding" /> would re-encode.
    /// </summary>
    SelectSmallest,

    /// <summary>
    /// Only re-encode the stream to meet criteria such as <see cref="VideoProcessingOptions.ResizeOptions" />, or to make a valid codec for the result
    /// container format.
    /// </summary>
    AvoidReencoding,
}
