namespace FulcrumFS.Videos;

/// <summary>
/// Represents a background color to use when processing videos.
/// </summary>
public readonly struct VideoBackgroundColor
{
    /// <summary>
    /// Gets the red component of the background color, between 0 and 1.
    /// </summary>
    public float R { get; }

    /// <summary>
    /// Gets the green component of the background color, between 0 and 1.
    /// </summary>
    public float G { get; }

    /// <summary>
    /// Gets the blue component of the background color, between 0 and 1.
    /// </summary>
    public float B { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoBackgroundColor"/> struct with the specified RGB values.
    /// </summary>
    public VideoBackgroundColor(float r, float g, float b)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(r, 0.0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(g, 0.0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(b, 0.0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(r, 1.0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(g, 1.0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(b, 1.0f);
        R = r;
        G = g;
        B = b;
    }
}
