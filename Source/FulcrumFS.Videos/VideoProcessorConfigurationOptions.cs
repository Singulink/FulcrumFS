using System.Diagnostics;
using System.Runtime.Versioning;

namespace FulcrumFS.Videos;

/// <summary>
/// Extended options for initializing the video processor configuration.
/// </summary>
public sealed record VideoProcessorConfigurationOptions()
{
    /// <summary>
    /// Gets or initializes the maximum number of concurrent ffmpeg processes to allow. Default is currently
    /// <see cref="Environment.ProcessorCount" />. Note: there is an additional process added used for short-lived processes.
    /// </summary>
    public int? MaxConcurrentProcesses
    {
        get;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Maximum concurrent processes must be at least 1 (or null for default).");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the processor affinity for the ffmpeg processes. Default is <see langword="null" /> which means no affinity is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="Process.ProcessorAffinity" /> for more details.
    /// </para>
    /// <para>
    /// Note: you should not set this value with an invalid bit set (on some platforms) (e.g., if you have 4 hardware threads, you cannot set the affinity to
    /// 0x1F).
    /// </para>
    /// </remarks>
    public IntPtr? ProcessorAffinity
    {
        get;

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        init
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Processor affinity is only supported on Windows and Linux.");
            }

            // We could try to validate the affinity value here properly, I am not sure that it is as simple as comparing to
            // '(nint)1 << Environment.ProcessorCount' in all cases (up to 63 processors), due to things like virtualization and containers.
            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Processor affinity cannot be zero.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum number of threads for each ffmpeg processing task; e.g., each decoder may get its own set of this many threads, along
    /// with each filter, encoder, etc. Default is currently <see langword="null" /> which means ffmpeg will choose the number of threads automatically.
    /// </summary>
    public int? ThreadLimit
    {
        get;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Thread limit must be at least 1 (or null for default).");
            }

            field = value;
        }
    }
}
