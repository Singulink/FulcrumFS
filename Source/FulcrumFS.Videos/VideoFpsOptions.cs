using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for resampling the frames per second (FPS) of a video during processing.
/// </summary>
public sealed record VideoFpsOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoFpsOptions" /> class with the specified mode and target FPS.
    /// </summary>
    /// <param name="mode">The mode for limiting the FPS.</param>
    /// <param name="targetFps">The target frames per second (FPS) value. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined <see cref="VideoFpsMode"/> value, or
    /// <paramref name="targetFps"/> is zero or negative.</exception>
    public VideoFpsOptions(VideoFpsMode mode, int targetFps)
    {
        mode.ThrowIfNotDefined();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetFps);

        Mode = mode;
        TargetFps = targetFps;
    }

    /// <summary>
    /// Gets or initializes the mode for limiting the FPS.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="VideoFpsMode"/> value.</exception>
    public VideoFpsMode Mode
    {
        get;
        init
        {
            value.ThrowIfNotDefined();
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the target frames per second (FPS) value. Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int TargetFps
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }
}
