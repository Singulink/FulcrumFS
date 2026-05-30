using System.Diagnostics;
using Singulink.Collections;

namespace FulcrumFS;

/// <summary>
/// Represents information about a file in a <see cref="FileRepo"/> together with all of its variants.
/// </summary>
public sealed record RepoFileGroupInfo
{
    /// <summary>
    /// Gets information about the main file.
    /// </summary>
    public RepoFileInfo MainFile { get; }

    /// <summary>
    /// Gets information about each variant of the main file. The list is empty if the file has no variants. Variants whose alias source data file is missing
    /// are omitted from this list and surfaced via <see cref="DanglingAliases"/> instead.
    /// </summary>
    public IReadOnlyList<RepoFileInfo> VariantFiles { get; }

    /// <summary>
    /// Gets information about each variant alias in the group whose source data file is missing. Empty in a healthy repository. Per-ID fetches of these
    /// variants throw <see cref="DanglingAliasException"/>; subsequent add operations targeting these variant IDs overwrite the dangling markers. See
    /// <see cref="FileRepo.CorruptionDetected"/> for programmatic notification of dangling aliases as they are encountered.
    /// </summary>
    public IReadOnlyList<DanglingAliasInfo> DanglingAliases { get; }

    /// <summary>
    /// Gets the file ID of the group.
    /// </summary>
    public FileId FileId => MainFile.FileId;

    internal RepoFileGroupInfo(RepoFileInfo main, IEnumerable<RepoFileInfo> variants, IEnumerable<DanglingAliasInfo>? danglingAliases = null)
    {
        MainFile = main;
        VariantFiles = EquatableArray.Create(variants);
        DanglingAliases = danglingAliases?.ToEquatableArray() ?? [];

        Debug.Assert(VariantFiles.All(v => v.FileId == MainFile.FileId), "All files in the group must have the same file ID.");
    }
}
