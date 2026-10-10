namespace FulcrumFS;

/// <summary>
/// Represents a transactional file repository. Code written against this interface rather than the local <c>FileRepo</c> keeps working when another
/// implementation, such as a repository served remotely, replaces the local directory one.
/// </summary>
/// <remarks>
/// <para>
/// The interface covers everything an application does with a repository once it exists: transactions that add and delete main files, fetching files and
/// variants, adding and retiring variants, enumeration, cleanup and the events that report failures and corruption. Creating, configuring and opening a
/// repository are not part of it, since they depend on the implementation, and nothing on it exposes a local path: file content is read through
/// <see cref="IRepoFile.OpenReadAsync(FileAccessPattern, CancellationToken)"/>, <see cref="OpenAsync(FileId, FileAccessPattern)"/> and
/// <see cref="OpenVariantAsync(FileId, string, FileAccessPattern)"/>.</para>
/// <para>
/// Fetches do not read file sizes. Finding a file takes only its name, and a size would cost a separate metadata read for every fetched file and every member
/// of a group, which a remote implementation would pay for on every request whether or not anyone wants it. A size is obtained on demand instead, through
/// <see cref="IRepoFile.GetLengthAsync(CancellationToken)"/>, which costs nothing until it is called and is cached by the instance it is called on. The files
/// an add returns know their sizes already, since the add has just written them. The streams that the open methods return are seekable, so
/// <see cref="Stream.Length"/> on an opened stream is the size of the file too.</para>
/// <para>
/// Implementations must be safe for concurrent use, so that one instance can serve a whole application. They must keep the commit model described on
/// <see cref="IFileRepoTransaction"/>, including leaving a file whose commit or rollback failed accessible until a cleanup pass resolves it.</para>
/// <para>
/// An implementation that generates file IDs in more than one place that is never coordinated with the others, such as a replicated implementation whose
/// primary and secondary can both generate IDs after a fail-over, must keep enough random bits in each ID that the generators cannot collide. The version 7
/// IDs that <see cref="FileIdMode.Sequential"/> generates keep 70 random bits for this reason, and the version 4 IDs of <see cref="FileIdMode.Secure"/> are
/// random apart from their version and variant bits.</para>
/// </remarks>
public interface IFileRepo : IFileRepoCleaner
{
    /// <summary>
    /// Occurs when a transaction-completion step (commit or rollback) fails. The handler receives a <see cref="RepoTransactionCompletionFailureInfo"/> carrying
    /// the operation that failed and the underlying exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event fires only for failures of the final commit or rollback step itself, not for exceptions raised while operating on the transaction, which
    /// surface directly to the calling code. If several errors occurred during the step, <see cref="RepoTransactionCompletionFailureInfo.Exception"/> is an
    /// <see cref="AggregateException"/>.</para>
    /// <para>
    /// Completion failures are not thrown, because the affected files remain accessible afterwards and are resolved by the next cleanup pass. Implementations
    /// raise the event from the commit or rollback call and await each handler in turn, so a handler that throws makes the exception propagate from that
    /// call, and keeps the handlers subscribed after it from being called.</para>
    /// <para>
    /// The event is raised once the commit or rollback has done all it can: every file of the transaction has been committed (or rolled back) or left
    /// indeterminate by then. An exception from a handler therefore reaches the caller after the fact and undoes nothing. A caller that sees one from
    /// <see cref="IFileRepoTransaction.CommitAsync(CancellationToken)"/> or <see cref="IFileRepoStage.CommitAsync(CancellationToken)"/> must not conclude
    /// that the transaction was not committed or was rolled back: its files are accessible, and a cleanup pass resolves those left indeterminate.</para>
    /// </remarks>
    event RepoTransactionCompletionFailedHandler? TransactionCompletionFailed;

    /// <summary>
    /// Occurs when repository corruption is detected during a fetch or add operation, for example a variant alias whose source data file is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler receives a <see cref="RepoCorruptionInfo"/> describing the corruption. The event fires every time corruption is encountered, in addition to
    /// any exception or other handling the operation applies, so monitoring and repair tooling can observe it.</para>
    /// <para>
    /// Implementations await each handler, and handler exceptions propagate out of the operation that detected the corruption.</para>
    /// </remarks>
    event RepoCorruptionHandler? CorruptionDetected;

    /// <summary>
    /// Gets the mode in which deletions take physical effect.
    /// </summary>
    /// <remarks>
    /// Applications can decide behavior on it, for example whether a deleted main file can still be read until a cleanup pass removes it, as it can in the
    /// deferred modes.
    /// </remarks>
    DeleteMode DeleteMode { get; }

    /// <summary>
    /// Gets the algorithm the repository uses to generate file IDs.
    /// </summary>
    /// <remarks>
    /// IDs generated in <see cref="FileIdMode.Sequential"/> mode reveal when they were created, so applications that expose IDs to untrusted parties can check
    /// this before doing so.
    /// </remarks>
    FileIdMode FileIdMode { get; }

    /// <summary>
    /// Gets a value indicating whether the repository is read-only.
    /// </summary>
    /// <remarks>
    /// A read-only repository can be fetched from and enumerated, and its events are raised, but every member that would write to it throws an
    /// <see cref="InvalidOperationException"/>: <see cref="BeginTransactionAsync"/>, <see cref="BeginStagedTransactionAsync"/>, the methods that add
    /// variants (<see cref="GetOrAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/> only when a variant does not
    /// exist yet), <see cref="DeleteVariantsAsync(FileId, ReadOnlySpan{string})"/> and the clean methods.
    /// </remarks>
    bool IsReadOnly { get; }

    /// <summary>
    /// Begins a new transaction, in which main files are added and deleted and which is committed or rolled back as a whole.
    /// </summary>
    ValueTask<IFileRepoTransaction> BeginTransactionAsync();

    /// <summary>
    /// Begins a new staged transaction, for application transactions that delete many files. It is committed in two calls,
    /// <see cref="IFileRepoStagedTransaction.StageAsync(CancellationToken)"/> before the application commits its database and
    /// <see cref="IFileRepoStage.CommitAsync(CancellationToken)"/> after.
    /// </summary>
    ValueTask<IFileRepoStagedTransaction> BeginStagedTransactionAsync();

    /// <summary>
    /// Gets an existing main file.
    /// </summary>
    /// <param name="fileId">The ID of the file.</param>
    /// <remarks>
    /// A deleted main file can still be fetched until a cleanup pass removes it, unless the repository deletes files immediately (see
    /// <see cref="DeleteMode"/>).
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file does not exist.</exception>
    ValueTask<IRepoFile> GetAsync(FileId fileId);

    /// <summary>
    /// Gets an existing file together with all of its variants.
    /// </summary>
    /// <param name="fileId">The ID of the file.</param>
    /// <remarks>
    /// A variant alias whose source data file is missing is not listed in <see cref="IRepoFileGroup.VariantFiles"/> but in
    /// <see cref="IRepoFileGroup.DanglingAliases"/>, and it is reported through <see cref="CorruptionDetected"/>.
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file does not exist.</exception>
    ValueTask<IRepoFileGroup> GetGroupAsync(FileId fileId);

    /// <summary>
    /// Gets an existing variant of a file.
    /// </summary>
    /// <param name="fileId">The ID of the main file.</param>
    /// <param name="variantId">The ID of the variant. Variant IDs consist of ASCII letters, digits, hyphens and underscores and are case-insensitive.</param>
    /// <remarks>
    /// A variant stored as an alias resolves to the content of its source. A retired variant is not found, even while its content has not been physically
    /// removed yet.
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file or the variant does not exist.</exception>
    /// <exception cref="DanglingAliasException">The variant is an alias whose source data file is missing (a subtype of
    /// <see cref="RepoFileNotFoundException"/>).</exception>
    /// <exception cref="ArgumentException"><paramref name="variantId"/> is empty or contains characters that are not allowed.</exception>
    ValueTask<IRepoFile> GetVariantAsync(FileId fileId, string variantId);

    /// <summary>
    /// Opens a read-only, seekable stream over the content of an existing main file. The caller owns the stream and must dispose it.
    /// </summary>
    /// <param name="fileId">The ID of the file.</param>
    /// <param name="accessPattern">How the caller is going to read the stream: from start to end, or by seeking within it. This is a hint that the
    /// implementation may use to choose how it reads the content; the stream can be read either way.</param>
    /// <remarks>
    /// This is equivalent to <see cref="GetAsync(FileId)"/> followed by <see cref="IRepoFile.OpenReadAsync(FileAccessPattern, CancellationToken)"/>, and
    /// the stream behaves the same way: it is seekable, may support only asynchronous reads, and the access pattern is only a hint.
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file does not exist.</exception>
    ValueTask<Stream> OpenAsync(FileId fileId, FileAccessPattern accessPattern);

    /// <summary>
    /// Opens a read-only, seekable stream over the content of an existing variant of a file. The caller owns the stream and must dispose it.
    /// </summary>
    /// <param name="fileId">The ID of the main file.</param>
    /// <param name="variantId">The ID of the variant.</param>
    /// <param name="accessPattern">How the caller is going to read the stream: from start to end, or by seeking within it. This is a hint that the
    /// implementation may use to choose how it reads the content; the stream can be read either way.</param>
    /// <remarks>
    /// This is equivalent to <see cref="GetVariantAsync(FileId, string)"/> followed by
    /// <see cref="IRepoFile.OpenReadAsync(FileAccessPattern, CancellationToken)"/>, and the stream behaves the same way: it is seekable, may support only
    /// asynchronous reads, and the access pattern is only a hint.
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The file or the variant does not exist.</exception>
    /// <exception cref="DanglingAliasException">The variant is an alias whose source data file is missing (a subtype of
    /// <see cref="RepoFileNotFoundException"/>).</exception>
    /// <exception cref="ArgumentException"><paramref name="variantId"/> is empty or contains characters that are not allowed.</exception>
    ValueTask<Stream> OpenVariantAsync(FileId fileId, string variantId, FileAccessPattern accessPattern);

    /// <inheritdoc cref="AddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>
    TaskWithProgress<IReadOnlyList<IRepoFile>> AddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new variant (and any nested variants declared by the pipeline) to an existing file. Variants are not transactional: the add is committed by the
    /// time it returns.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant is added.</param>
    /// <param name="variantId">The ID of the top-level variant to add.</param>
    /// <param name="sourceVariantId">The ID of the variant to process, or <see langword="null"/> to process the main file.</param>
    /// <param name="pipeline">The processing pipeline for the top-level variant, or the selector that chooses one by extension.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The stored variants in pre-order (the top-level variant first, then nested variants in declaration order). A variant stored as an alias (see
    /// <see cref="FileProcessingPipeline.AliasWhenVariantSourceUnchanged"/>) is included and resolves to the content of its source.</returns>
    /// <remarks>
    /// The returned variants know their sizes without storage access or a network round trip (see <see cref="IRepoFile.GetLengthAsync(CancellationToken)"/>).
    /// </remarks>
    /// <exception cref="InvalidOperationException">A variant in the requested tree already exists.</exception>
    /// <exception cref="RepoVariantPendingCleanupException">A variant in the requested tree was retired and has not been cleaned up yet (a subtype of
    /// <see cref="InvalidOperationException"/>).</exception>
    /// <exception cref="RepoFileNotFoundException">The file or the source variant does not exist.</exception>
    /// <exception cref="FileProcessingException">A processor in the pipeline rejected or failed to process the source.</exception>
    TaskWithProgress<IReadOnlyList<IRepoFile>> AddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="TryAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>
    TaskWithProgress<IReadOnlyList<IRepoFile>?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to add a new variant (and any nested variants declared by the pipeline) to an existing file. The operation is all-or-nothing: it returns
    /// <see langword="null"/> without adding anything if any variant ID in the tree already exists or is being added concurrently.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant is added.</param>
    /// <param name="variantId">The ID of the top-level variant to add.</param>
    /// <param name="sourceVariantId">The ID of the variant to process, or <see langword="null"/> to process the main file.</param>
    /// <param name="pipeline">The processing pipeline for the top-level variant, or the selector that chooses one by extension.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The stored variants in the same order as
    /// <see cref="AddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>, or <see langword="null"/> if nothing was
    /// added.</returns>
    /// <remarks>
    /// The returned variants know their sizes without storage access or a network round trip (see <see cref="IRepoFile.GetLengthAsync(CancellationToken)"/>).
    /// </remarks>
    /// <exception cref="RepoVariantPendingCleanupException">A variant in the requested tree was retired and has not been cleaned up yet.</exception>
    /// <exception cref="RepoFileNotFoundException">The file or the source variant does not exist.</exception>
    /// <exception cref="FileProcessingException">A processor in the pipeline rejected or failed to process the source.</exception>
    TaskWithProgress<IReadOnlyList<IRepoFile>?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="GetOrAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>
    TaskWithProgress<IReadOnlyList<IRepoFile>> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the variants of a tree (a top-level variant and any nested variants declared by the pipeline) that do not exist yet, and returns every variant in
    /// the tree, whether it was added now or existed already.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variants are added.</param>
    /// <param name="variantId">The ID of the top-level variant.</param>
    /// <param name="sourceVariantId">The ID of the variant to process, or <see langword="null"/> to process the main file.</param>
    /// <param name="pipeline">The processing pipeline for the top-level variant, or the selector that chooses one by extension.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>Every variant in the tree in the same order as
    /// <see cref="AddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>.</returns>
    /// <remarks>
    /// <para>
    /// Existing variants are returned unchanged and their pipelines do not run. This is the method to use for variants that are generated on demand.</para>
    /// <para>
    /// The variants this call added know their sizes without storage access or a network round trip, while those that already existed may read theirs on
    /// demand (see <see cref="IRepoFile.GetLengthAsync(CancellationToken)"/>).</para>
    /// </remarks>
    /// <exception cref="RepoVariantPendingCleanupException">A variant in the requested tree was retired and has not been cleaned up yet.</exception>
    /// <exception cref="RepoFileNotFoundException">The file or the source variant does not exist.</exception>
    /// <exception cref="FileProcessingException">A processor in the pipeline rejected or failed to process the source.</exception>
    TaskWithProgress<IReadOnlyList<IRepoFile>> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently retires the specified variants of a file, preserving any variant the caller did not list that depends on a listed one.
    /// </summary>
    /// <param name="fileId">The ID of the file whose variants are retired.</param>
    /// <param name="variantIds">The full set of variant IDs to retire. Duplicates are ignored and ordering is irrelevant.</param>
    /// <returns>
    /// The normalized IDs of the variants this call retired, in ordinal order, or an empty list if none needed retiring. Listed IDs that were already retired,
    /// have been cleaned up or never existed are not included, and neither are unlisted variants that were preserved.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Pass every variant ID to retire. A variant that is not listed but is an alias of a listed variant is preserved with its content: the earliest such
    /// variant is promoted to a data file of its own and the others become aliases of it. Retirement is visible to fetches immediately, and whether the
    /// content is removed immediately or by a later cleanup pass depends on <see cref="DeleteMode"/>.</para>
    /// <para>
    /// Retirement is not part of a transaction: it commits on its own as it runs, so it cannot be ordered with an application's database commit. An
    /// application that records variant IDs in its database must tolerate a recorded variant being gone, for example by regenerating it on demand with
    /// <see cref="GetOrAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>, rather than rely on the retirement
    /// and its database update committing together.</para>
    /// <para>
    /// The method is idempotent: an empty list, variants that are already retired and variant IDs that never existed are skipped and left out of the
    /// returned list.</para>
    /// </remarks>
    ValueTask<IReadOnlyList<string>> DeleteVariantsAsync(FileId fileId, params ReadOnlySpan<string> variantIds);

    /// <summary>
    /// Enumerates every file group in the repository. Intended for recovery and audit tooling.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the enumeration.</param>
    /// <remarks>
    /// <para>
    /// This is for rare, deliberate operations such as reconciling the repository with an application database restored to an earlier point in time. It
    /// reads the whole repository, which on a large one is a long operation, so it is not meant for routine use.</para>
    /// <para>
    /// The order of the results is unspecified, and files added or deleted while the enumeration is in progress may or may not appear.</para>
    /// </remarks>
    IAsyncEnumerable<RepoFileEntry> EnumerateFilesAsync(CancellationToken cancellationToken = default);
}
