using Singulink.Enums;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents the hardware acceleration mode to use.
/// </summary>
public sealed record HardwareAccelerationMode
{
    private HardwareAccelerationMode(HardwareAccelerationKind kind, bool isStrict)
    {
        Kind = kind;
        IsStrict = isStrict;
    }

    /// <summary>
    /// Gets a value indicating the hardware acceleration kind to use for operations where possible (such as decode or scaling).
    /// </summary>
    public HardwareAccelerationKind Kind
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
    public bool IsStrict { get; init; }

    /// <summary>
    /// Creates a new <see cref="HardwareAccelerationMode"/> instance with the specified kind that does not require strictly identical results to the software
    /// result.
    /// </summary>
    public static HardwareAccelerationMode Create(HardwareAccelerationKind kind)
    {
        return new HardwareAccelerationMode(kind, false);
    }

    /// <summary>
    /// Creates a new <see cref="HardwareAccelerationMode"/> instance with the specified kind that requires results to be theoretically identical to the
    /// software result.
    /// </summary>
    public static HardwareAccelerationMode CreateStrict(HardwareAccelerationKind kind)
    {
        return new HardwareAccelerationMode(kind, true);
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
