namespace FulcrumFS;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// The exception that is thrown when a variant alias is encountered whose source data file is missing from the repository. This always indicates repository
/// corruption: in normal operation every code path that could remove the source (deletion, retirement, rollback) is coordinated against the aliases pointing
/// at it so this state is unreachable. A fetch does not throw it for an alias that a retirement of its source is promoting or re-pointing at that moment:
/// it follows the retirement to the content the variant resolves to, and throws only for an alias that still has no source data file, whether or not its
/// source is retired. See the <see cref="IFileRepo.CorruptionDetected"/> event for programmatic discovery of these (and other) corruption conditions across
/// all fetch and add operations.
/// </summary>
public class DanglingAliasException : RepoFileNotFoundException
{
    /// <summary>
    /// Gets the file ID of the file group containing the dangling alias.
    /// </summary>
    public FileId FileId { get; }

    /// <summary>
    /// Gets the variant ID of the dangling alias.
    /// </summary>
    public string VariantId { get; }

    /// <summary>
    /// Gets the variant ID of the alias's expected source, or <see langword="null"/> if the alias source is the main file.
    /// </summary>
    public string? SourceVariantId { get; }

    /// <summary>
    /// Gets the file extension (including the leading dot, or empty) of the alias's expected source data file.
    /// </summary>
    public string SourceExtension { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DanglingAliasException"/> class.
    /// </summary>
    public DanglingAliasException(FileId fileId, string variantId, string? sourceVariantId, string sourceExtension)
        : this(fileId, variantId, sourceVariantId, sourceExtension, BuildMessage(fileId, variantId, sourceVariantId)) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DanglingAliasException"/> class with a specified message.
    /// </summary>
    public DanglingAliasException(FileId fileId, string variantId, string? sourceVariantId, string sourceExtension, string message)
        : base(message)
    {
        FileId = fileId;
        VariantId = variantId;
        SourceVariantId = sourceVariantId;
        SourceExtension = sourceExtension;
    }

    private static string BuildMessage(FileId fileId, string variantId, string? sourceVariantId)
    {
        string sourceDescription = sourceVariantId is null ? "main file" : $"variant '{sourceVariantId}'";
        return $"File ID '{fileId}' variant '{variantId}' is an alias whose source ({sourceDescription}) was not found.";
    }
}
