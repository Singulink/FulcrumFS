namespace FulcrumFS.Videos;

/// <summary>
/// Specifies how to handle streams when processing videos.
/// </summary>
public enum StreamSelectionBehavior
{
    /// <summary>
    /// Keep all streams.
    /// </summary>
    KeepAll,

    /// <summary>
    /// Keep the first stream only.
    /// </summary>
    KeepFirst,

    /// <summary>
    /// Keep the best stream (as determined by a heuristic) only.
    /// </summary>
    KeepBest,

    /// <summary>
    /// Remove all streams.
    /// </summary>
    RemoveAll,
}
