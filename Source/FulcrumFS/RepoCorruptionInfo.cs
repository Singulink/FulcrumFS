namespace FulcrumFS;

/// <summary>
/// Describes a repository corruption condition surfaced via the <see cref="FileRepo.CorruptionDetected"/> event.
/// </summary>
public sealed record RepoCorruptionInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoCorruptionInfo"/> class.
    /// </summary>
    internal RepoCorruptionInfo(RepoCorruptionKind kind, FileId fileId, string? variantId, string message, Exception? exception = null)
    {
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
