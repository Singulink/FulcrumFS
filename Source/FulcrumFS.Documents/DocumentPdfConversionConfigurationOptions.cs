using System.Diagnostics;
using System.Runtime.Versioning;

namespace FulcrumFS.Documents;

/// <summary>
/// Extended options for initializing the document PDF conversion processor configuration.
/// </summary>
public sealed record DocumentPdfConversionConfigurationOptions()
{
    /// <summary>
    /// Gets or initializes the maximum number of concurrent LibreOffice processes to allow. Default is currently
    /// <see cref="Environment.ProcessorCount" />. Note: each LibreOffice process can consume a significant amount of memory, so consider lowering this on
    /// machines with many processors if memory is constrained.
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
    /// Gets or initializes the processor affinity for the LibreOffice processes. Default is <see langword="null" /> which means no affinity is set.
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

            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Processor affinity cannot be zero.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the process priority class for the LibreOffice processes. Default is currently <see langword="null" /> which means LibreOffice
    /// will run at the default priority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="Process.PriorityClass" /> for more details.
    /// </para>
    /// </remarks>
    public ProcessPriorityClass? ProcessPriorityClass
    {
        get;
        init
        {
            if (value is not null && !Enum.IsDefined(value.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Invalid process priority class.");
            }

            field = value;
        }
    }
}
