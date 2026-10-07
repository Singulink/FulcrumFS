namespace FulcrumFS;

/// <summary>
/// Describes a variant alias in a file group whose source data file is missing. Returned by <see cref="IFileRepo.GetGroupAsync(FileId)"/> via
/// <see cref="IRepoFileGroup.DanglingAliases"/> to surface alias corruption without polluting <see cref="IRepoFileGroup.VariantFiles"/>. Per-ID fetches
/// of a dangling alias throw <see cref="DanglingAliasException"/>.
/// </summary>
public sealed class DanglingAliasInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DanglingAliasInfo"/> class.
    /// </summary>
    /// <param name="variantId">The normalized variant ID of the dangling alias.</param>
    /// <param name="sourceVariantId">The normalized variant ID of the alias's expected source, or <see langword="null"/> if the source is the main file.</param>
    /// <param name="sourceExtension">The normalized file extension of the alias's expected source data file, including the leading dot, or empty if it has
    /// none.</param>
    /// <remarks>
    /// This constructor exists for <see cref="IFileRepo"/> implementations and is not meant for application code.
    /// </remarks>
    /// <exception cref="ArgumentException">A variant ID is not a valid normalized variant ID, or <paramref name="sourceExtension"/> is not empty or a valid
    /// normalized file extension.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public DanglingAliasInfo(string variantId, string? sourceVariantId, string sourceExtension)
    {
        if (!Utilities.VariantId.IsValidAndNormalized(variantId))
            throw new ArgumentException("Variant ID must be a valid normalized variant ID.", nameof(variantId));

        if (sourceVariantId is not null && !Utilities.VariantId.IsValidAndNormalized(sourceVariantId))
            throw new ArgumentException("Source variant ID must be a valid normalized variant ID.", nameof(sourceVariantId));

        if (sourceExtension.Length is not 0 && !FileExtension.IsValidAndNormalized(sourceExtension))
            throw new ArgumentException("Source extension must be empty or a valid normalized file extension.", nameof(sourceExtension));

        VariantId = variantId;
        SourceVariantId = sourceVariantId;
        SourceExtension = sourceExtension;
    }

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
}
