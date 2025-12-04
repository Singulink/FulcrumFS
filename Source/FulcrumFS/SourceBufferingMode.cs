namespace FulcrumFS;

/// <summary>
/// Specifies the mode for buffering source streams to temporary repository work files. This setting controls the trade-off between throughput, memory usage,
/// and security resilience against slow sources.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Security &amp; Performance Trade-offs:</strong><br/> Disabling intermediate buffering avoids unnecessary I/O and can improve throughput. However, it
/// may increase memory usage (due to in-memory buffering of slow sources) and expose the service to "slow POST" / Slowloris-style denial-of-service
/// attacks.</para>
/// <para>
/// <strong>Recommendations:</strong>
/// <list type="bullet">
///   <item>
///     <description><strong>General Use:</strong> <see cref="Auto"/> is recommended as it balances performance and security. It avoids copying likely fast
///     sources (like <see cref="MemoryStream"/> or <see cref="FileStream"/>) while protecting against unknown streams.</description>
///   </item>
///   <item>
///     <description><strong>Slow Storage:</strong> If a file source exists on slow storage (e.g. high-latency network shares) and the repository is located on
///     fast storage, use <see cref="ForceTempCopy"/> to ensure it is copied to the faster storage location before processing.</description>
///   </item>
///   <item>
///     <description><strong>Trusted Environments:</strong> <see cref="Disabled"/> is appropriate if clients are trusted or other security mitigations are in
///     place. May increase memory usage if many concurrent files are processed from slow sources.</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public enum SourceBufferingMode
{
    /// <summary>
    /// Optimizes based on the source type. Sources presumed to be fast (i.e. inheriting from <see cref="MemoryStream"/> or <see cref="FileStream"/>, and direct
    /// file paths) are processed directly. All other source streams are force-copied to a temporary file before processing.
    /// </summary>
    Auto,

    /// <summary>
    /// Sources are always force-copied to a temporary repository work file before processing.
    /// </summary>
    ForceTempCopy,

    /// <summary>
    /// Sources may be processed directly, bypassing the initial copy to a temporary repository work file. Use this only for sources known to be fast (e.g.
    /// wrappers over local file streams) to avoid copy overhead.
    /// </summary>
    Disabled,
}
