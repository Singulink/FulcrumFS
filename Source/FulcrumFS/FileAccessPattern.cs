namespace FulcrumFS;

/// <summary>
/// Specifies how a caller is going to read the content of a repository file, as a hint to the implementation that opens it.
/// </summary>
/// <remarks>
/// The pattern is a hint about the caller's intent. An implementation may use it to choose its buffering, read-ahead or request strategy, and it returns the
/// same, correct content whichever pattern is passed: a stream opened for <see cref="Sequential"/> reading can still seek, and one opened for
/// <see cref="Random"/> reading can still be read to the end. The local implementation opens the file with <see cref="FileOptions.SequentialScan"/> or
/// <see cref="FileOptions.RandomAccess"/>, which the operating system turns into caching and read-ahead hints.
/// </remarks>
public enum FileAccessPattern
{
    /// <summary>
    /// The content is read from start to end, for example to copy it to a response or to process it in one pass.
    /// </summary>
    Sequential,

    /// <summary>
    /// The caller seeks within the content, for example to serve byte ranges or to read a format whose index is not at the start of the file.
    /// </summary>
    Random,
}
