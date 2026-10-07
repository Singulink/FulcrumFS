using Singulink.Enums;

namespace FulcrumFS;

/// <summary>
/// Describes a repository corruption condition surfaced via the <see cref="IFileRepo.CorruptionDetected"/> event.
/// </summary>
public sealed class RepoCorruptionInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoCorruptionInfo"/> class.
    /// </summary>
    /// <param name="kind">The kind of corruption detected.</param>
    /// <param name="fileId">The file ID of the affected file group.</param>
    /// <param name="variantId">The variant ID associated with the corruption, or <see langword="null"/> if it is not variant-scoped.</param>
    /// <param name="message">A human-readable description of the corruption suitable for logging.</param>
    /// <param name="exception">An optional exception carrying further detail.</param>
    /// <remarks>
    /// This constructor exists for <see cref="IFileRepo"/> implementations and is not meant for application code. The variant ID is not validated, because it
    /// can come from the damaged state being reported.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined <see cref="RepoCorruptionKind"/> value.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public RepoCorruptionInfo(RepoCorruptionKind kind, FileId fileId, string? variantId, string message, Exception? exception = null)
    {
        kind.ThrowIfNotDefined();

        Kind = kind;
        FileId = fileId;
        VariantId = variantId;
        Message = message;
        Exception = exception;
    }

    /// <summary>
    /// Gets the kind of corruption detected.
    /// </summary>
    public RepoCorruptionKind Kind { get; }

    /// <summary>
    /// Gets the file ID of the affected file group.
    /// </summary>
    public FileId FileId { get; }

    /// <summary>
    /// Gets the variant ID associated with the corruption, or <see langword="null"/> if not variant-scoped.
    /// </summary>
    public string? VariantId { get; }

    /// <summary>
    /// Gets a human-readable description of the corruption suitable for logging.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets an optional exception carrying further detail (for example a parse failure for a malformed marker).
    /// </summary>
    public Exception? Exception { get; }
}
