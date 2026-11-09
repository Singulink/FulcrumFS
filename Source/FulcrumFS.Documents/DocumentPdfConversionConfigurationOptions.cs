using System.Diagnostics;
using System.Runtime.Versioning;

namespace FulcrumFS.Documents;

/// <summary>
/// Extended options for initializing the document PDF conversion processor configuration.
/// </summary>
public sealed record DocumentPdfConversionConfigurationOptions
{
    /// <summary>
    /// Gets or initializes the maximum number of concurrent LibreOffice processes to allow. Must be at least 1. Default is <see langword="null" />, which
    /// uses <see cref="Environment.ProcessorCount" />.
    /// </summary>
    /// <remarks>
    /// Each LibreOffice process can use a significant amount of memory, so the memory that conversions use grows with this limit.
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
    /// Gets or initializes the processor affinity for the LibreOffice processes. Must not be zero. Default is <see langword="null" />, which means no
    /// affinity is set.
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

            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The value is zero.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes the process priority class for the LibreOffice processes. Default is <see langword="null" />, which means LibreOffice will run
    /// at the default priority.
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
