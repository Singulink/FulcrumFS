using System.Diagnostics;
using System.Runtime.Versioning;

namespace FulcrumFS.Videos;

/// <summary>
/// Extended options for initializing the video processor configuration.
/// </summary>
public sealed record VideoProcessorConfigurationOptions
{
    /// <summary>
    /// Gets or initializes the maximum number of concurrent ffmpeg processes to allow. Must be at least 1. Default is <see langword="null" />, which uses
    /// <see cref="Environment.ProcessorCount" />.
    /// </summary>
    /// <remarks>
    /// Up to three more processes can run on top of the limit, one in each of three extra slots: one for short processes such as ffprobe runs, one for frame
    /// and thumbnail extractions, and one for the lighter passes of video processing, such as recombining streams. These slots let quick steps finish while
    /// long processes fill the limit, so the limit is the number of concurrent processes most of the time, with occasional short or light ones above it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxConcurrentProcesses
    {
        get;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The value is less than 1.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the processor affinity for the ffmpeg processes. Must not be zero. Default is <see langword="null" />, which means no affinity
    /// is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="Process.ProcessorAffinity" /> for more details. Processor affinity is supported only on Windows and Linux.
    /// </para>
    /// <para>
    /// On some platforms a mask with a bit for a processor that the machine doesn't have (e.g., 0x1F with 4 hardware threads) is not valid, and the
    /// processes that the library starts fail when it sets the mask on them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero.</exception>
    /// <exception cref="PlatformNotSupportedException">The value is set on a platform other than Windows or Linux.</exception>
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
                throw new ArgumentOutOfRangeException(nameof(value), value, "The value is zero.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the maximum number of threads for each ffmpeg processing task; e.g., each decoder may get its own set of this many threads, along
    /// with each filter, encoder, etc. Must be at least 1. Default is <see langword="null" />, which means ffmpeg will choose the number of threads
    /// automatically.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? ThreadLimit
    {
        get;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The value is less than 1.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the process priority class for the ffmpeg processes. Default is <see langword="null" />, which means ffmpeg will run at the
    /// default priority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="Process.PriorityClass" /> for more details.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="System.Diagnostics.ProcessPriorityClass"/> value.</exception>
    public ProcessPriorityClass? ProcessPriorityClass
    {
        get;
        init
        {
            if (value is not null && !Enum.IsDefined(value.Value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, $"The value is not a defined {nameof(System.Diagnostics.ProcessPriorityClass)} value.");
            }

            field = value;
        }
    }
}
