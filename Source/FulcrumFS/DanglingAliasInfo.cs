namespace FulcrumFS;

/// <summary>
/// Describes a variant alias in a file group whose source data file is missing. Returned by <see cref="FileRepo.GetGroupAsync(FileId)"/> via
/// <see cref="RepoFileGroupInfo.DanglingAliases"/> to surface alias corruption without polluting <see cref="RepoFileGroupInfo.VariantFiles"/>. Per-ID fetches
/// of a dangling alias throw <see cref="DanglingAliasException"/>.
/// </summary>
public sealed record DanglingAliasInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DanglingAliasInfo"/> class.
    /// </summary>
    internal DanglingAliasInfo(string variantId, string? sourceVariantId, string sourceExtension)
    {
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
