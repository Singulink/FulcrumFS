namespace FulcrumFS;

/// <summary>
/// Describes a file group found by <see cref="IFileRepo.EnumerateFilesAsync(CancellationToken)"/>.
/// </summary>
public sealed class RepoFileEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileEntry"/> class.
    /// </summary>
    /// <param name="fileId">The file ID of the file group.</param>
    /// <param name="creationTimeUtc">The creation time of the file group, with <see cref="DateTimeKind.Utc"/> as its <see cref="DateTime.Kind"/>.</param>
    /// <param name="isPendingDeletion">The value of <see cref="IsPendingDeletion"/>.</param>
    /// <param name="isIndeterminate">The value of <see cref="IsIndeterminate"/>.</param>
    /// <remarks>
    /// This constructor exists for <see cref="IFileRepo"/> implementations and is not meant for application code.
    /// </remarks>
    /// <exception cref="ArgumentException">The <see cref="DateTime.Kind"/> of <paramref name="creationTimeUtc"/> is not <see cref="DateTimeKind.Utc"/>.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public RepoFileEntry(FileId fileId, DateTime creationTimeUtc, bool isPendingDeletion, bool isIndeterminate)
    {
        if (creationTimeUtc.Kind is not DateTimeKind.Utc)
            throw new ArgumentException("Creation time must be in UTC.", nameof(creationTimeUtc));

        FileId = fileId;
        CreationTimeUtc = creationTimeUtc;
        IsPendingDeletion = isPendingDeletion;
        IsIndeterminate = isIndeterminate;
    }

    /// <summary>
    /// Gets the file ID of the file group.
    /// </summary>
    public FileId FileId { get; }

    /// <summary>
    /// Gets the creation time of the file group's directory in Coordinated Universal Time (UTC).
    /// </summary>
    public DateTime CreationTimeUtc { get; }

    /// <summary>
    /// Gets a value indicating whether the file had been deleted and was waiting for a cleanup pass to physically remove it when the enumeration started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deletion committed in <see cref="DeleteMode.DeferredUntilClean"/> or <see cref="DeleteMode.DeferredFilesOnly"/> mode (or an immediate deletion that
    /// could not complete) leaves the file group on disk until a cleanup pass removes it. Such a file can still be deleted by another transaction:
    /// <see cref="IFileRepoTransaction.DeleteAsync"/> succeeds, <see cref="IFileRepoTransaction.TryDeleteAsync"/> returns <see langword="true"/>, and both
    /// deletions converge.</para>
    /// <para>
    /// On a repository with delete journals, a deletion committed by a <see cref="IFileRepoStagedTransaction"/>, or by a <see cref="IFileRepoTransaction"/>
    /// that committed two or more, is recorded in a journal rather than for each file, and the files it lists are reported as pending deletion too. A staged
    /// transaction that has not committed yet does not make its files pending
    /// deletion; they are indeterminate until it does (see <see cref="IsIndeterminate"/>). A file that another transaction deletes again while its deletion is
    /// pending can be reported as both pending deletion and indeterminate.</para>
    /// </remarks>
    public bool IsPendingDeletion { get; }

    /// <summary>
    /// Gets a value indicating whether the file was in the indeterminate state when the enumeration started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file is indeterminate while a transaction that adds or deletes it is still open, and afterwards if that transaction's commit or rollback failed or
    /// the process ended before the transaction completed. Indeterminate files left behind by a failed or interrupted transaction are resolved by the
    /// callback passed to <see cref="IFileRepoCleaner.CleanAsync(TimeSpan, Func{FileId, Task{IndeterminateResolution}}?, CancellationToken)"/>.</para>
    /// <para>
    /// This includes the files whose deletion a <see cref="IFileRepoStagedTransaction"/> has staged and not committed yet, whether the repository records them
    /// in a staged delete journal or, on a version 1 repository, with per-file markers. The deletions it has only recorded before staging are not on disk yet
    /// and do not make a file indeterminate.</para>
    /// </remarks>
    public bool IsIndeterminate { get; }
}
