using System.Runtime.CompilerServices;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of repository enumeration for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Enumerates every file group in the repository. Intended for recovery and audit tooling.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the enumeration.</param>
    /// <remarks>
    /// <para>
    /// This is for rare, deliberate operations such as reconciling the repository with an application database that was restored to an earlier point in
    /// time, or auditing that the repository and the application agree. It reads every shard directory of the repository, which on a large repository is a
    /// long operation, so it is not meant for routine use.</para>
    /// <para>
    /// The cleanup state is read once when the enumeration starts and the file groups are walked afterwards. <see cref="RepoFileEntry.IsPendingDeletion"/>
    /// and <see cref="RepoFileEntry.IsIndeterminate"/> reflect that starting point, and files added or deleted while the enumeration is in progress may or
    /// may not appear. No locks are taken and no other repository operation is blocked.</para>
    /// <para>
    /// The order of the results is unspecified. Only directories that are well-formed file groups are reported: a directory named by a lowercase file ID
    /// under the two shard directory levels its ID maps to. Anything else under the files directory is skipped. The contents of a file group directory are
    /// not inspected, since a group may be in the middle of being added or deleted; the <see cref="RepoFileEntry.IsPendingDeletion"/> and
    /// <see cref="RepoFileEntry.IsIndeterminate"/> flags and the cleaner are the authority on its state.</para>
    /// </remarks>
    public async IAsyncEnumerable<RepoFileEntry> EnumerateFilesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var entry in _fs.EnumerateFileGroupsAsync(cancellationToken).ConfigureAwait(false))
            yield return entry;
    }
}
