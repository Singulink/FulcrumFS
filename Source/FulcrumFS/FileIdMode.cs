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
    /// but the embedded timestamps make IDs predictable and reveal creation order and creation time to the millisecond (see <see cref="FileId.Timestamp"/>).
    /// Suitable for repositories where file IDs are not exposed to untrusted parties.
    /// </summary>
    /// <remarks>
    /// A repository generates its IDs with a <see cref="FileIdGenerator"/> of its own, and they increase in the order they are created. Up to 16 IDs per
    /// millisecond carry the current time; a faster burst runs the embedded timestamps ahead of the clock until the rate drops again.
    /// </remarks>
    Sequential,
}
