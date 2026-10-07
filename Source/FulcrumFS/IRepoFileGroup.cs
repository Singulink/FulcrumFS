namespace FulcrumFS;

/// <summary>
/// Represents a file stored in an <see cref="IFileRepo"/> together with all of its variants.
/// </summary>
/// <remarks>
/// <para>
/// An instance describes the group as it was when it was fetched or added; variants added or retired afterwards are not reflected in it.</para>
/// <para>
/// The files of a group returned by an add know their sizes without storage access or a network round trip, while the files of a fetched group read them on
/// demand (see <see cref="IRepoFile.GetLengthAsync(CancellationToken)"/>).</para>
/// </remarks>
public interface IRepoFileGroup
{
    /// <summary>
    /// Gets the file ID of the group.
    /// </summary>
    FileId FileId { get; }

    /// <summary>
    /// Gets the main file of the group.
    /// </summary>
    IRepoFile MainFile { get; }

    /// <summary>
    /// Gets each variant of the main file, or an empty list if it has none.
    /// </summary>
    /// <remarks>
    /// Every entry has the group's <see cref="FileId"/> and a non-null <see cref="IRepoFile.VariantId"/>, no variant ID appears twice, and every entry can be
    /// read: a variant alias whose source content is missing is not listed here but in <see cref="DanglingAliases"/>.
    /// </remarks>
    IReadOnlyList<IRepoFile> VariantFiles { get; }

    /// <summary>
    /// Gets each variant alias in the group whose source data file is missing, or an empty list in a healthy repository.
    /// </summary>
    /// <remarks>
    /// A dangling alias is corruption, and each one found is also reported through <see cref="IFileRepo.CorruptionDetected"/>. Fetching such a variant by its
    /// ID throws <see cref="DanglingAliasException"/>, and adding a variant with its ID replaces it.
    /// </remarks>
    IReadOnlyList<DanglingAliasInfo> DanglingAliases { get; }
}
