namespace FulcrumFS.Videos;

/// <summary>
/// Identifies the expected lifetime / resource consumption of an external process, which determines which process slot tier/s it is allowed to run in.
/// </summary>
internal enum ProcessLifetime
{
    /// <summary>
    /// A long-lived process (e.g. a full video processing run). Only runs in the main process slots.
    /// </summary>
    LongLived,

    /// <summary>
    /// A cheap long-lived process (e.g. smallest-result detection, stream recombination passes, or compatibility check). Has its own extra slot in addition to
    /// the main process slots, so it can still run even if unrelated long-lived jobs have taken all the main slots.
    /// </summary>
    CheapLongLived,

    /// <summary>
    /// A medium-lived process (e.g. a video frame / thumbnail extraction). Has its own extra slot in addition to the main / cheap long-lived process slots, so
    /// one can always run and remain relatively quick without having to queue behind long-lived processes.
    /// </summary>
    MediumLived,

    /// <summary>
    /// A short-lived process (e.g. an ffprobe run). Has its own extra slot in addition to the main / cheap long-lived / medium process slots, so it can always
    /// start and finish fast.
    /// </summary>
    ShortLived,
}
