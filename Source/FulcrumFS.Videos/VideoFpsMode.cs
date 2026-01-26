namespace FulcrumFS.Videos;

/// <summary>
/// Defines modes for controlling the frames per second (FPS) of a video during processing.
/// </summary>
public enum VideoFpsMode
{
    /// <summary>
    /// Limits the FPS to an exact value - that is, if the original video has a higher FPS, it will be reduced to this exact value.
    /// </summary>
    LimitToExact,

    /// <summary>
    /// <para>
    /// Limits the FPS by dividing the original FPS by an integer value - that is, if the original video has a higher FPS, it will be reduced to the original
    /// FPS divided by the smallest integer that is large enough to achieve the desired limit.</para>
    /// <para>
    /// Note: this can be calculated by using integer division: <c>newFps = originalFps / (originalFps \ targetFps)</c>; where '\' is integer division (rounded
    /// up, as opposed to the normal rounding of integer division). Or with '\' representing standard integer division rounding (floored):
    /// <c>newFps = -originalFps / (-originalFps \ targetFps)</c>.</para>
    /// </summary>
    LimitByIntegerDivision,
}
