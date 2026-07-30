namespace FulcrumFS.Videos;

/// <summary>
/// Represents the hardware acceleration kind to use for operations (such as decode or scaling).
/// </summary>
/// <remarks>
/// All modes allow fallback to software when required.
/// </remarks>
public enum HardwareAccelerationKind
{
    /// <summary>
    /// Disable hardware acceleration.
    /// </summary>
    None,

    /// <summary>
    /// Disable hardware acceleration, other than for decode.
    /// </summary>
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
    VideoToolbox,

    /// <summary>
    /// Use CUDA for hardware acceleration (when possible).
    /// </summary>
    Cuda,

    /// <summary>
    /// Use Intel Quick Sync Video (QSV) for hardware acceleration (when possible).
    /// </summary>
    Qsv,

    /// <summary>
    /// Use AMD Advanced Media Framework (AMF) for hardware acceleration (when possible).
    /// </summary>
    Amf,

    /// <summary>
    /// Use D3D12 for hardware acceleration (when possible).
    /// </summary>
    D3D12,
}
