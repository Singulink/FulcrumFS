namespace FulcrumFS;

/// <summary>
/// Specifies a version of the on-disk format of a FulcrumFS repository.
/// </summary>
/// <remarks>
/// The format version is recorded in the repository's info file. A library opens a repository with full access and cleans it only if it supports the
/// repository's version; reading files needs less, as described for each version. See <see cref="FileRepoOptions.MinimumFormatVersion"/> for how a
/// repository gets its version.
/// </remarks>
public enum RepoFormatVersion
{
    /// <summary>
    /// The newest format this library supports, whichever version that is. New repositories are created at it and older repositories are upgraded to it when
    /// they are initialized, including after the library is updated to a version with a newer format.
    /// </summary>
    /// <remarks>
    /// Use this only for repositories that the application owns exclusively. Never use it when a process running an older version of FulcrumFS, or a cleanup
    /// job deployed separately from the application, uses or cleans the repositories: updating the library would upgrade them and lock those processes out.
    /// </remarks>
    Latest = int.MaxValue,

    /// <summary>
    /// The original format, which FulcrumFS 1.0.x reads and writes. Transactions record their deletions with a marker per file on a repository of this
    /// version.
    /// </summary>
    Version1 = 1,

    /// <summary>
    /// Adds delete journals, which record all of the deletions of a staged transaction, or of an ordinary transaction that commits two or more deletions in a
    /// deferred delete mode, in one file. Full access and cleaning need FulcrumFS 2.0 or later. Files are stored as in version 1, so reading stays compatible
    /// with version 1: a library that supports version 1 can open the repository in <see cref="FileRepoOptions.ReadOnly"/> mode. FulcrumFS 1.0.x has no
    /// read-only mode and refuses the repository altogether.
    /// </summary>
    Version2 = 2,
}
