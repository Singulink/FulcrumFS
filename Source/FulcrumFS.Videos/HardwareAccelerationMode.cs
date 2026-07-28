using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents the hardware acceleration mode to use.
/// </summary>
public sealed record HardwareAccelerationMode
{
    private HardwareAccelerationMode(HardwareAccelerationKind preferred, bool isStrict)
    {
        PreferredKind = preferred;
        IsStrict = isStrict;
    }

    /// <summary>
    /// Gets a value indicating the hardware acceleration kind to use for operations where possible (such as decode or scaling), with a fallback to
    /// <see cref="HardwareAccelerationKind.Auto" />.
    /// </summary>
    public HardwareAccelerationKind PreferredKind
    {
        get;
        init
        {
            value.ThrowIfNotDefined();
            field = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether to only allow hardware acceleration that guarantees the result is theoretically identical to the software result.
    /// </summary>
    /// <remarks>
    /// Note: we do not guarantee that it is actually byte-for-byte identical, but it should not be slightly different due to things like using bilinear scaling
    /// rather than bicubic scaling. To get it byte-for-byte identical, use <see cref="HardwareAccelerationKind.None" /> or
    /// <see cref="None" /> to disable hardware acceleration entirely.
    /// </remarks>
    public bool IsStrict { get; init; }

    /// <summary>
    /// Creates a new <see cref="HardwareAccelerationMode"/> instance with the specified preferred kind that does not require strictly identical results to the
    /// software result.
    /// </summary>
    public static HardwareAccelerationMode Create(HardwareAccelerationKind preferredKind)
    {
        return new HardwareAccelerationMode(preferredKind, false);
    }

    /// <summary>
    /// Creates a new <see cref="HardwareAccelerationMode"/> instance with the specified preferred kind that requires results to be theoretically identical to
    /// the software result.
    /// </summary>
    /// <remarks>
    /// Note: we do not guarantee that it is actually byte-for-byte identical, but it should not be slightly different due to things like using bilinear scaling
    /// rather than bicubic scaling. To get it byte-for-byte identical, use <see cref="HardwareAccelerationKind.None" /> or
    /// <see cref="None" /> to disable hardware acceleration entirely.
    /// </remarks>
    public static HardwareAccelerationMode CreateStrict(HardwareAccelerationKind preferredKind)
    {
        return new HardwareAccelerationMode(preferredKind, true);
    }

    /// <summary>
    /// Gets the default <see cref="HardwareAccelerationMode"/> instance, which uses <see cref="HardwareAccelerationKind.Auto"/> and does not require strictly
    /// identical results to the software result.
    /// </summary>
    public static HardwareAccelerationMode Default { get; } = new HardwareAccelerationMode(HardwareAccelerationKind.Auto, false);

    /// <summary>
    /// Gets a predefined instance of <see cref="HardwareAccelerationMode"/> that disables hardware acceleration for operations.
    /// </summary>
    public static HardwareAccelerationMode None { get; } = new HardwareAccelerationMode(HardwareAccelerationKind.None, true);

    /// <summary>
    /// Gets a predefined instance of <see cref="HardwareAccelerationMode"/> that disables hardware acceleration for operations (other than decoding which still
    /// uses it opportunistically).
    /// </summary>
    public static HardwareAccelerationMode DecodeOnly { get; } = new HardwareAccelerationMode(HardwareAccelerationKind.DecodeOnly, true);
}
