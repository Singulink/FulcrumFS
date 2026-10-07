using System.Runtime.CompilerServices;

namespace FulcrumFS;

/// <content>
/// Repository-wide enumeration of file groups for recovery and audit tooling. It needs nothing beyond the on-disk layout, so an offline tool can use it
/// without a live <see cref="FileRepo"/>.
/// </content>
partial class RepoFileSystem
{
    /// <summary>
    /// Enumerates every well-formed file group directory (<c>files/{shard}/{shard}/{fileId}</c>) along with whether a main-file delete marker or a committed
    /// delete journal listing it, or an indeterminate marker or a staged delete journal listing it, existed for it when the enumeration started. The cleanup
    /// directory is read once up front and the files tree is walked afterwards, taking no locks. Entries that do not fit the layout are skipped silently, and
    /// the contents of group directories are not inspected.
    /// </summary>
    public async IAsyncEnumerable<RepoFileEntry> EnumerateFileGroupsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingDeletion = new HashSet<FileId>();
        var indeterminate = new HashSet<FileId>();

        foreach (var marker in CleanupDirectory.GetChildFiles())
        {
            if (marker.Extension is FileRepoPaths.DeleteMarkerExtension)
            {
                // Variant delete hints ({fileId} {variantId}.del) are skipped: only a main-file delete marker means the whole file is pending deletion.
                if (TryParseFileIdAndVariant(marker.NameWithoutExtension, out var fileId, out string? variantId) && variantId is null)
                    pendingDeletion.Add(fileId);
            }
            else if (marker.Extension is FileRepoPaths.IndeterminateMarkerExtension)
            {
                if (FileId.TryParseUnsafe(marker.NameWithoutExtension, out var fileId))
                    indeterminate.Add(fileId);
            }
            else if (marker.Extension is FileRepoPaths.CommittedJournalExtension && IsJournalName(marker.NameWithoutExtension))
            {
                // A committed journal is the delete record of every file it lists. A journal that cannot be read is skipped: it is either damaged (which the
                // cleaner reports) or being applied by a cleaner that holds it exclusively, in which case its files are disappearing anyway.
                try
                {
                    if (TryReadJournal(marker, out var fileIds))
                        pendingDeletion.UnionWith(fileIds);
                }
                catch (IOException) { }
            }
            else if (marker.Extension is FileRepoPaths.StagedJournalExtension && IsJournalName(marker.NameWithoutExtension))
            {
                // Whether a staged journal commits is not known yet, held or not, so its files are indeterminate: the same state the per-file markers of an
                // ordinary transaction, or of a staged one on a version 1 repository, report. A torn journal was never staged, so its files are not. A journal
                // that cannot be read was committed or resolved since the listing, or is held exclusively by a cleaner resolving it; it is skipped.
                try
                {
                    if (TryReadJournal(marker, out var fileIds))
                        indeterminate.UnionWith(fileIds);
                }
                catch (IOException) { }
            }
        }

        // A missing files directory throws rather than yielding nothing: a recovery tool would otherwise conclude that every file is gone. Each listing is
        // materialized before anything is yielded so no directory handle stays open while the caller processes entries.
        var firstLevelShards = FilesDirectory.GetChildDirectories().Where(d => IsShardName(d.Name)).ToList();

        foreach (var shard1 in firstLevelShards)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var shard2 in GetChildShardDirectories(shard1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The walk is synchronous I/O over up to 65,536 shard directories, so yield once per shard to avoid monopolizing a thread. ForceYielding
                // without ContinueOnCapturedContext is Task.Yield without capturing the caller's synchronization context.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

                foreach (var (fileId, creationTimeUtc) in GetChildGroupDirectories(shard2))
                    yield return new RepoFileEntry(fileId, creationTimeUtc, pendingDeletion.Contains(fileId), indeterminate.Contains(fileId));
            }
        }
    }

    private static List<IAbsoluteDirectoryPath> GetChildShardDirectories(IAbsoluteDirectoryPath shard1)
    {
        try
        {
            return shard1.GetChildDirectories().Where(d => IsShardName(d.Name)).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            // Removed while the walk was in progress; nothing in it can be reported.
            return [];
        }
    }

    /// <summary>
    /// Lists the group directories in a second-level shard that are exactly where the repository looks for their file IDs: a lowercase file ID in the shard
    /// its ID maps to (path comparison is case-sensitive). A group directory anywhere else is unreachable by every other repository operation, so reporting
    /// it would hand the caller a file ID that the repository then reports as not found.
    /// </summary>
    private List<(FileId FileId, DateTime CreationTimeUtc)> GetChildGroupDirectories(IAbsoluteDirectoryPath shard2)
    {
        var groups = new List<(FileId FileId, DateTime CreationTimeUtc)>();

        try
        {
            foreach (var info in shard2.GetChildDirectoriesInfo())
            {
                if (FileId.TryParseUnsafe(info.Path.Name, out var fileId) && info.Path.Equals(GetFileDirectory(fileId)))
                    groups.Add((fileId, info.CreationTimeUtc));
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Removed while the walk was in progress; nothing in it can be reported.
        }

        return groups;
    }

    private static bool IsShardName(string name) => name.Length is 2 && char.IsAsciiHexDigitLower(name[0]) && char.IsAsciiHexDigitLower(name[1]);
}
