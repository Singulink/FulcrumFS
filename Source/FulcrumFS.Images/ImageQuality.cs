namespace FulcrumFS.Images;

/// <summary>
/// Specifies the quality level to use during lossy image encoding (e.g. JPEG, WebP). Higher quality levels result in larger file sizes.
/// </summary>
public enum ImageQuality
{
    /// <summary>
    /// <para>
    /// Lowest quality setting, suitable for small thumbnails when file size is critical.</para>
    /// <para>
    /// Produces results equivalent to a JPEG quality of 30 for all encoders.</para>
    /// </summary>
    Lowest,

    /// <summary>
    /// <para>
    /// Low quality setting, suitable for thumbnails or preview images when file size is critical.</para>
    /// <para>
    /// Produces results equivalent to a JPEG quality of 50 for all encoders.</para>
    /// </summary>
    Low,

    /// <summary>
    /// <para>
    /// Medium quality setting, balancing file size and visual fidelity. Visual artifacts may be present.</para>
    /// <para>
    /// Produces results equivalent to a JPEG quality of 65 for all encoders.</para>
    /// </summary>
    Medium,

    /// <summary>
    /// <para>
    /// High quality setting, providing results that are close to visually lossless for most images.</para>
    /// <para>
    /// Produces results equivalent to a JPEG quality of 78 for all encoders.</para>
    /// </summary>
    High,

    /// <summary>
    /// <para>
    /// Highest quality setting, where artifacts are rarely visible.</para>
    /// <para>
    /// Produces results equivalent to a JPEG quality of 90 for all encoders.</para>
    /// </summary>
    Highest,
}

#pragma warning disable SA1649 // File name should match first type name

internal static class ImageQualityExtensions
{
    internal static int ToJpegEquivalentQuality(this ImageQuality quality)
    {
        return quality switch
        {
            ImageQuality.Lowest => 30,
            ImageQuality.Low => 50,
            ImageQuality.Medium => 65,
            ImageQuality.High => 78,
            ImageQuality.Highest => 90,
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };
    }
}
