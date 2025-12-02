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
    public VideoFpsOptions(VideoFpsMode mode, int targetFps)
    {
        Mode = mode;
        TargetFps = targetFps;
    }

    /// <summary>
    /// Gets or initializes the mode for limiting the FPS.
    /// </summary>
    public VideoFpsMode Mode
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(Mode));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the target frames per second (FPS) value.
    /// </summary>
    public int TargetFps
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(TargetFps));
            field = value;
        }
    }
}
