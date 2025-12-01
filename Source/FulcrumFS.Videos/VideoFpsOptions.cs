using System.Numerics;
using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents options for limiting the frames per second (FPS) of a video during processing.
/// </summary>
public sealed class VideoFpsOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoFpsOptions" /> class.
    /// </summary>
    public VideoFpsOptions(VideoFpsLimitMode limitMode, int targetFps)
    {
        LimitMode = limitMode;
        TargetFps = targetFps;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoFpsOptions" /> class - this constructor is the copy constructor.
    /// </summary>
    public VideoFpsOptions(VideoFpsOptions baseConfig)
    {
        LimitMode = baseConfig.LimitMode;
        TargetFps = baseConfig.TargetFps;
    }

    /// <summary>
    /// Gets or initializes the mode for limiting the FPS.
    /// </summary>
    public VideoFpsLimitMode LimitMode
    {
        get;
        init
        {
            value.ThrowIfNotDefined(nameof(LimitMode));
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
