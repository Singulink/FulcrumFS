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
    public RepoFileInfo Main { get; }

    /// <summary>
    /// Gets information about each variant of the main file. The list is empty if the file has no variants.
    /// </summary>
    public IReadOnlyList<RepoFileInfo> Variants { get; }

    internal RepoFileGroupInfo(RepoFileInfo main, IReadOnlyList<RepoFileInfo> variants)
    {
        Main = main;
        Variants = EquatableArray.Create<RepoFileInfo>(variants);
    }
}
