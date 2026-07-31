namespace FulcrumFS.Videos;

/// <summary>
/// Represents the hardware acceleration kind to use for operations (such as decode or scaling).
/// </summary>
/// <remarks>
/// <para>
/// Hardware acceleration is applied to decoding and to filtering (such as scaling and deinterlacing) - encoding is always done in software.
/// </para>
/// <para>
/// All modes allow fallback to software when required - if the configured ffmpeg build or the system does not support the requested mode, or if it fails while
/// processing a particular file, processing transparently falls back to software so the operation still succeeds if it should.
/// </para>
/// <para>
/// Hardware acceleration is disabled by default (see <see cref="None" />). Results always vary slightly from device to device, but hardware decoders and
/// especially filters vary considerably more than their software equivalents - differing between vendors, driver versions and hardware generations, and
/// sometimes very visibly so - which is the trade made for the substantial speed improvement.
/// </para>
/// </remarks>
public enum HardwareAccelerationKind
{
    /// <summary>
    /// Disable hardware acceleration. This is the default.
    /// </summary>
    /// <remarks>
    /// Everything is decoded and filtered in software, which gives the most consistent output across devices, at the cost of speed. Note that results can
    /// still vary slightly between devices, just far less than when hardware acceleration is used.
    /// </remarks>
    None,

    /// <summary>
    /// Disable hardware acceleration, other than for decode.
    /// </summary>
    /// <remarks>
    /// This is a middle ground that speeds up decoding while keeping all filtering (such as scaling and deinterlacing) in software, so most of the output
    /// quality characteristics are preserved. Note that result files are still not guaranteed to match software decoding in every scenario, however differences
    /// should generally not be too noticeable.
    /// </remarks>
    DecodeOnly,

    /// <summary>
    /// Automatically select an arbitrary hardware acceleration mode, based on the available modes.
    /// </summary>
    /// <remarks>
    /// If you have an AMD or Intel GPU, you will want to manually select the appropriate mode (AMF or QSV) instead of using this option, as we deprioritize
    /// those modes, as we assume they are most likely going to match the CPU on most systems, rather than a GPU.
    /// </remarks>
    Auto,

    /// <summary>
    /// Use VideoToolbox for hardware acceleration (when possible).
    /// </summary>
    /// <remarks>
    /// VideoToolbox is the Apple platform (e.g. macOS) acceleration framework, and uses the media engine built into Apple silicon and Intel Macs.
    /// </remarks>
    VideoToolbox,

    /// <summary>
    /// Use CUDA for hardware acceleration (when possible).
    /// </summary>
    /// <remarks>
    /// CUDA uses the media engine on NVIDIA GPUs, and is available on Windows and Linux.
    /// </remarks>
    Cuda,

    /// <summary>
    /// Use Intel Quick Sync Video (QSV) for hardware acceleration (when possible).
    /// </summary>
    /// <remarks>
    /// QSV uses the media engine built into Intel hardware, which is most commonly the integrated GPU of an Intel CPU rather than a discrete GPU. Since
    /// <see cref="Auto" /> deprioritizes this mode (to avoid preferring a CPU's integrated media engine over a discrete GPU), select it explicitly if you want
    /// Intel hardware to be used, e.g. if the machine has an Intel discrete GPU, or has no other GPU available.
    /// </remarks>
    Qsv,

    /// <summary>
    /// Use AMD Advanced Media Framework (AMF) for hardware acceleration (when possible).
    /// </summary>
    /// <remarks>
    /// AMF uses the media engine on AMD hardware, which may be either a discrete GPU or the integrated GPU of an AMD CPU. Since <see cref="Auto" />
    /// deprioritizes this mode (to avoid preferring a CPU's integrated media engine over a discrete GPU), select it explicitly if you want AMD hardware to be
    /// used, e.g. if the machine has an AMD discrete GPU.
    /// </remarks>
    Amf,

    /// <summary>
    /// Use D3D12 for hardware acceleration (when possible).
    /// </summary>
    /// <remarks>
    /// D3D12 is a vendor-neutral Windows acceleration path, so it can be used with hardware from any vendor, but a vendor-specific mode is usually preferable
    /// where one is available.
    /// </remarks>
    D3D12,
}
