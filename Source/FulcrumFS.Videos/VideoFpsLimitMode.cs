namespace FulcrumFS.Videos;

/// <summary>
/// Defines modes for limiting the frames per second (FPS) of a video during processing.
/// </summary>
public enum VideoFpsLimitMode
{
    /// <summary>
    /// Limits the FPS to an exact value - that is, if the original video has a higher FPS, it will be reduced to this exact value.
    /// </summary>
    Exact,

    /// <summary>
    /// Limits the FPS by dividing the original FPS by an integer value - that is, if the original video has a higher FPS, it will be reduced to the original
    /// FPS divided by the smallest integer that is large enough to achieve the desired limit.
    /// </summary>
    DivideByInteger,
}
