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
    public VideoFpsOptions(VideoFpsLimitMode limitMode, (int Num, int Den) targetFps)
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
    /// For example, to specify 59.94 FPS, use (60000, 1001), or for 25 FPS, use (25, 1).
    /// </summary>
    public (int Num, int Den) TargetFps
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Num, nameof(TargetFps) + nameof(TargetFps.Num));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Den, nameof(TargetFps) + nameof(TargetFps.Den));

            if (!BigInteger.GreatestCommonDivisor(value.Num, value.Den).IsOne)
                throw new ArgumentException("The fraction must be in its simplest form.", nameof(TargetFps));

            field = value;
        }
    }
}
