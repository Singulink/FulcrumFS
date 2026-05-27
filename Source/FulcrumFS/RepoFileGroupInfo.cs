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

    /// <summary>
    /// Gets the file ID of the main file. Convenience forwarder for <see cref="Main"/>.<see cref="RepoFileInfo.FileId"/>.
    /// </summary>
    public FileId FileId => Main.FileId;

    /// <summary>
    /// Gets the extension of the main file (including the leading period). Convenience forwarder for <see cref="Main"/>.<see cref="RepoFileInfo.Extension"/>.
    /// </summary>
    public string Extension => Main.Extension;

    internal RepoFileGroupInfo(RepoFileInfo main, IEnumerable<RepoFileInfo> variants)
    {
        Main = main;
        Variants = EquatableArray.Create(variants);
    }
}
