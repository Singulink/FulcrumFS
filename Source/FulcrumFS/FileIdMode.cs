namespace FulcrumFS;

/// <summary>
/// Specifies the algorithm used to generate file IDs in a file repository.
/// </summary>
public enum FileIdMode
{
    /// <summary>
    /// File IDs are generated as cryptographically random (version 4) GUIDs. This mode produces unpredictable identifiers with no embedded timing
    /// information, making them safe to expose to untrusted parties at the cost of reduced locality compared to <see cref="Sequential"/>.
    /// </summary>
    Secure,

    /// <summary>
    /// File IDs are generated as sequential (version 7) GUIDs. This mode produces time-ordered identifiers which improve locality and indexing performance,
    /// but the embedded timestamps make IDs predictable and reveal creation order. Suitable for repositories where file IDs are not exposed to untrusted
    /// parties.
    /// </summary>
    Sequential,
}
