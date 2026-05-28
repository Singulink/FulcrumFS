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
    /// Gets information about each variant of the main file. The list is empty if the file has no variants.
    /// </summary>
    public IReadOnlyList<RepoFileInfo> VariantFiles { get; }

    /// <summary>
    /// Gets the file ID of the group.
    /// </summary>
    public FileId FileId => MainFile.FileId;

    internal RepoFileGroupInfo(RepoFileInfo main, IEnumerable<RepoFileInfo> variants)
    {
        MainFile = main;
        VariantFiles = EquatableArray.Create(variants);

        Debug.Assert(VariantFiles.All(v => v.FileId == MainFile.FileId), "All files in the group must have the same file ID.");
    }
}
