using System.Diagnostics;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of add functionality for the file repository (main file via transaction + variants), including the recursive auto-variant
/// runner and the batch-move commit step.
/// </content>
partial class FileRepo
{
    private enum VariantAddMode
    {
        Add,
        Try,
        GetOrAdd,
    }

    private sealed class StagedNode
    {
        public required string? VariantId;
        public required IAbsoluteFilePath Path;
        public required string Extension;
        public required FileProcessingContext? Context;
        public required bool IsExisting;

        /// <summary>
        /// The variant ID of the actual on-disk data file this node resolves to (or <see cref="FileRepoPaths.MainFileName"/> for the main file). For a data
        /// node, this equals <see cref="VariantId"/> (or <c>$main</c>). For an alias node, this is inherited from the parent and identifies the data file the
        /// alias points to.
        /// </summary>
        public required string RootVariantId;

        /// <summary>
        /// The extension (with leading dot, or empty) of the actual on-disk data file this node resolves to.
        /// </summary>
        public required string RootExtension;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Public variant-add API
    // ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc cref="AddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>
    public Task<IReadOnlyList<RepoFileInfo>> AddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        return AddVariantAsync(fileId, variantId, sourceVariantId: null, pipeline, cancellationToken);
    }

    /// <summary>
    /// Adds a new file variant (and any nested auto-variants declared by the pipeline) to an existing file in the repository.
    /// </summary>
    /// <param name="fileId">The ID of the main file to which the variant will be added.</param>
    /// <param name="variantId">The ID of the top-level variant to be added.</param>
    /// <param name="sourceVariantId">The ID of the source variant, or <see langword="null"/> to use the main file as the source.</param>
    /// <param name="pipeline">The pipeline provider used to process the top-level variant.</param>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The list of stored variants in pre-order (top-level first, then nested in declaration order). Variants whose pipelines stored an alias due to
    /// <see cref="FileProcessingPipeline.AliasWhenVariantSourceUnchanged"/> are included in the list with the resolved data file path.</returns>
    /// <exception cref="InvalidOperationException">A variant in the requested tree already exists.</exception>
    public async Task<IReadOnlyList<RepoFileInfo>> AddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        var result = await RunVariantAddAsync(fileId, variantId, sourceVariantId, pipeline, VariantAddMode.Add, cancellationToken).ConfigureAwait(false);
        Debug.Assert(result is not null, "Add mode should never return null.");
        return result;
    }

    /// <inheritdoc cref="GetOrAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>
    public Task<IReadOnlyList<RepoFileInfo>> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        return GetOrAddVariantAsync(fileId, variantId, sourceVariantId: null, pipeline, cancellationToken);
    }

    /// <summary>
    /// Adds new file variants where they do not already exist, processing missing variants through their pipelines and keeping existing ones unchanged. Each
    /// declared variant ID in the tree is represented in the returned list; variants whose pipelines stored an alias due to
    /// <see cref="FileProcessingPipeline.AliasWhenVariantSourceUnchanged"/> resolve transparently to their source.
    /// </summary>
    /// <remarks>
    /// Variants whose pipelines opt into <see cref="FileProcessingPipeline.AliasWhenVariantSourceUnchanged"/> remain addressable after the add: an alias
    /// marker is stored that transparently resolves to the variant's source on fetch operations. Nested variants of the aliased variant still run against the
    /// unchanged parent source.
    /// </remarks>
    public async Task<IReadOnlyList<RepoFileInfo>> GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        var result = await RunVariantAddAsync(fileId, variantId, sourceVariantId, pipeline, VariantAddMode.GetOrAdd, cancellationToken).ConfigureAwait(false);
        Debug.Assert(result is not null, "GetOrAdd mode should never return null.");
        return result;
    }

    /// <inheritdoc cref="TryAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>
    public Task<IReadOnlyList<RepoFileInfo>?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        return TryAddVariantAsync(fileId, variantId, sourceVariantId: null, pipeline, cancellationToken);
    }

    /// <summary>
    /// Tries to add a new file variant (and any nested auto-variants declared by the pipeline) to an existing file in the repository. Returns
    /// <see langword="null"/> if any declared variant ID in the tree already exists or if any per-variant lock cannot be acquired immediately. The operation is
    /// strict and all-or-nothing: any collision aborts the entire add.
    /// </summary>
    public Task<IReadOnlyList<RepoFileInfo>?> TryAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken = default)
    {
        return RunVariantAddAsync(fileId, variantId, sourceVariantId, pipeline, VariantAddMode.Try, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Variant-add core
    // ---------------------------------------------------------------------------------------------------------------

    private const int MaxAddRebaseRollForwardAttempts = 8;

    private async Task<IReadOnlyList<RepoFileInfo>?> RunVariantAddAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector rootSelector,
        VariantAddMode mode,
        CancellationToken cancellationToken)
    {
        variantId = VariantId.Normalize(variantId);
        sourceVariantId = sourceVariantId is null ? null : VariantId.Normalize(sourceVariantId);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        IAbsoluteFilePath sourceFile = null;
        FileProcessingPipeline rootPipeline;

        if (rootSelector is IFileProcessingPipelineProvider provider)
        {
            rootPipeline = provider.GetPipeline();
        }
        else
        {
            // The root selector is not a provider, so we need to resolve the source file to obtain the extension for pipeline resolution.
            // Locate the source file to obtain the extension used to resolve the root pipeline.

            sourceFile = FindSourceFile();
            rootPipeline = rootSelector.GetPipeline(sourceFile.Extension);
        }

        // Lock-free fast path for GetOrAdd: if every declared variant already exists on disk, return them directly without acquiring any locks. The check is
        // racy (a concurrent delete could remove a variant after the check), but that is consistent with the best-effort guarantees this API already provides
        // under concurrent access. Alias markers count as "exists" and are resolved transparently.

        if (mode is VariantAddMode.GetOrAdd)
        {
            var fastPathResults = new List<RepoFileInfo>();
            if (TryCollectExistingVariants(fileId, variantId, rootPipeline, fastPathResults))
                return fastPathResults;
        }

        // A pending rebase marker observed while resolving the source forces an optimistic rollforward-and-restart (honoring the whole-system invariant that
        // no add/delete plans while a rebase marker exists — see the alias-resolution block below), so the whole add runs under a bounded restart loop.
        for (int addAttempt = 0; ; addAttempt++)
        {
            if (addAttempt >= MaxAddRebaseRollForwardAttempts)
            {
                throw new IOException(
                    $"Unable to resolve a stable source for file ID '{fileId}' after {MaxAddRebaseRollForwardAttempts} attempts due to concurrent rebase " +
                    $"activity. Retry the operation.");
            }

            // Re-resolve the source on each iteration; a prior restart may have rewritten it to a resolved data file, and a rollforward may have re-pointed it.
            sourceFile = null;

            // Discover every variant ID being created in the tree (the user-provided root variant ID plus the statically declared nested variants). These are
            // the IDs that participate in collision detection, since they are the ones the add brings into existence.

            var createdVariantIds = new SortedSet<string>(StringComparer.Ordinal) { variantId };
            AddVariantIds(rootPipeline, createdVariantIds);

            // The full lock set additionally includes the source variant (if any) so it cannot be removed concurrently while we may be writing an alias marker
            // that depends on it. The source is expected to already exist (it is a precondition, not a collision), so it is locked and gated against retirement
            // but excluded from collision detection. Note that when the source is itself an alias, the chain-compression invariant means our staged aliases
            // will reference the resolved root, which we additionally lock below once we've inspected the source file on disk.

            var allVariantIds = new SortedSet<string>(createdVariantIds, StringComparer.Ordinal);

            if (sourceVariantId is not null)
                allVariantIds.Add(sourceVariantId);

            var locks = new List<KeyLock<(FileId, string?)>>(allVariantIds.Count + 1);
            var stagedNodes = new List<StagedNode>();
            bool needRollForward = false;

            try
            {
                // Acquire per-variant locks in sorted order for deadlock-freedom.

                var lockTimeout = mode is VariantAddMode.GetOrAdd ? Timeout.InfiniteTimeSpan : TimeSpan.Zero;

                foreach (string vid in allVariantIds)
                {
                    try
                    {
                        locks.Add(await _fileSync.LockAsync((fileId, vid), lockTimeout, cancellationToken).ConfigureAwait(false));
                    }
                    catch (TimeoutException) when (mode is VariantAddMode.Try)
                    {
                        return null;
                    }
                }

                // Re-check disk existence for every declared variant ID under the locks. Also check for in-group delete markers (folded into FindDataFile's out
                // param): if a delete marker is present, the slot still has unsettled retirement state on disk and cannot accept an add until the cleaner finishes
                // physical removal. This is a state conflict (not a collision and not a transient retry case), so even TryAdd fails fast rather than swallowing
                // it. Once cleanup completes, callers may add under the same ID again — see RepoVariantPendingCleanupException remarks.

                var existingByVariantId = new Dictionary<string, IAbsoluteFilePath>(StringComparer.Ordinal);

                foreach (string vid in allVariantIds)
                {
                    var existing = FindDataFile(_filesDirectory, fileId, vid, out bool isRetired);

                    if (isRetired)
                    {
                        throw new RepoVariantPendingCleanupException(
                            $"File ID '{fileId}' variant '{vid}' has pending retirement cleanup state on disk and cannot be added under the same ID until " +
                            $"cleanup completes. Reusing a retired variant ID is an advanced scenario — consult the FileRepo.DeleteVariantsAsync " +
                            $"documentation before relying on this behavior.");
                    }

                    if (existing is not null)
                        existingByVariantId[vid] = existing;
                }

                if (mode is not VariantAddMode.GetOrAdd)
                {
                    // Add/Try mode: a created variant ID that already exists is a collision. The source variant ID is deliberately excluded; it is required to
                    // already exist to serve as a source, so its presence is never a collision.

                    string? firstCollision = createdVariantIds.FirstOrDefault(existingByVariantId.ContainsKey);

                    if (firstCollision is not null)
                    {
                        if (mode is VariantAddMode.Try)
                            return null;

                        throw new InvalidOperationException($"File ID '{fileId}' variant '{firstCollision}' already exists.");
                    }
                }

                // Open the source file stream that the root pipeline will read from. If the source on disk is an alias marker, resolve it transparently so the
                // pipeline reads from the real data file; track the resolved root for any alias markers we may write for this variant or its descendants.
                sourceFile ??= FindSourceFile();

                // Defaults are only meaningful on the staging path below; when a rollforward is needed they are left unused.
                string parentRootVariantId = FileRepoPaths.MainFileName;
                string parentRootExtension = string.Empty;

                if (sourceFile.Extension is FileRepoPaths.AliasMarkerExtension)
                {
                    if (!TryParseAliasMarker(sourceFile, out _, out string? rootVid, out string? rootExt))
                    {
                        throw new InvalidOperationException(
                            $"File ID '{fileId}' source variant '{sourceVariantId}' alias marker '{sourceFile.Name}' is malformed.");
                    }

                    // Additionally lock the resolved root if it differs from the (already-locked) sourceVariantId, so the underlying data file cannot be
                    // deleted concurrently. Acquired out of sorted order — acceptable because no other locks remain to be taken at this point in the flow.
                    if (rootVid is not null && !allVariantIds.Contains(rootVid))
                    {
                        try
                        {
                            locks.Add(await _fileSync.LockAsync((fileId, rootVid), lockTimeout, cancellationToken).ConfigureAwait(false));
                        }
                        catch (TimeoutException) when (mode is VariantAddMode.Try)
                        {
                            return null;
                        }
                    }

                    // Probe the resolved root under its lock. FindDataFile also surfaces — for free — any pending rebase naming the root as the source: a
                    // crash-interrupted (or in-progress, cross-process) retirement of the root that is being replaced by a chosen survivor. Binding the new
                    // variant to the doomed root could leave our freshly-written alias dangling once the cleaner finishes that rebase and removes the root.
                    // Honor the whole-system invariant instead: drop our locks (in the finally), roll the rebase forward to completion under its own locks,
                    // then restart and re-resolve against the settled, single-head subtree. (No locks are held across the rollforward, so no out-of-order
                    // acquisition is possible.)
                    var resolved = FindDataFile(_filesDirectory, fileId, rootVid, out _, out string? rootRebaseChosen);

                    if (rootRebaseChosen is not null)
                    {
                        if (rootVid is null)
                        {
                            throw new InvalidOperationException(
                                $"File ID '{fileId}' has a rebase marker naming the main file as a source, which is invalid. This indicates repository " +
                                $"corruption.");
                        }

                        needRollForward = true;
                    }
                    else
                    {
                        if (resolved is null || resolved.State is not EntryState.Exists)
                        {
                            throw new RepoFileNotFoundException(
                                $"File ID '{fileId}' source variant '{sourceVariantId}' alias points to missing data file.");
                        }

                        parentRootVariantId = rootVid ?? FileRepoPaths.MainFileName;
                        parentRootExtension = rootExt;

                        sourceFile = resolved;
                    }
                }
                else
                {
                    parentRootVariantId = sourceVariantId ?? FileRepoPaths.MainFileName;
                    parentRootExtension = sourceFile.Extension;
                }

                if (!needRollForward)
                {
                    // Run the tree against the resolved source data file directly. Passing the file path (rather than a pre-opened stream) lets each
                    // processor consume the source in whatever form is optimal for it: a file-based processor (e.g. one that shells out to ffmpeg) reads the
                    // path with no intermediate copy, while a stream-based processor opens it on demand. It also lets the pipeline treat the source as a known
                    // repo file and skip any defensive buffering.

                    await RunNodeAsync(
                        fileId,
                        streamSource: null,
                        streamLeaveOpen: false,
                        fileSource: sourceFile,
                        parentRootVariantId,
                        parentRootExtension,
                        variantId,
                        rootPipeline,
                        mode,
                        existingByVariantId,
                        stagedNodes,
                        cancellationToken).ConfigureAwait(false);

                    // Batch-move into files/.

                    await BatchMoveVariantsAsync(fileId, stagedNodes, cancellationToken).ConfigureAwait(false);

                    // Build result.

                    var results = new List<RepoFileInfo>(stagedNodes.Count);
                    foreach (var node in stagedNodes)
                    {
                        // Alias nodes are addressable variants but resolve transparently to their source data file, so report the resolved data path (the
                        // marker itself is never surfaced to callers).
                        var reportedPath = node.Extension is FileRepoPaths.AliasMarkerExtension ?
                            ResolveAlias(_filesDirectory, fileId, node.Path) ?? node.Path :
                            node.Path;

                        results.Add(new RepoFileInfo(fileId, node.VariantId, reportedPath));
                    }

                    return results;
                }
            }
            finally
            {
                // Always dispose contexts (cleans up temp dirs). Safe to call regardless of move outcome since context Dispose doesn't touch files/.
                foreach (var node in stagedNodes)
                {
                    if (node.Context is not null)
                    {
                        try { await node.Context.DisposeAsync().ConfigureAwait(false); }
                        catch { }
                    }
                }

                locks.ReverseDisposeAll();
            }

            // Our locks are released. Roll the observed pending rebase forward to completion (under its own locks) and restart, so the next iteration resolves
            // the source against a settled, single-head subtree.
            await RollForwardPendingRebasesAsync(fileId, cancellationToken).ConfigureAwait(false);
        }

        IAbsoluteFilePath FindSourceFile()
        {
            // Retirement of sourceVariantId is already ruled out: the add path has gated every declared variant ID (including sources) against delete markers
            // under locks before reaching the staging runner.
            return FindDataFile(_filesDirectory, fileId, sourceVariantId, out _) ??
                throw new RepoFileNotFoundException(
                    sourceVariantId is null ?
                        $"File ID '{fileId}' was not found." :
                        $"File ID '{fileId}' or its source variant '{sourceVariantId}' was not found.");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Recursive runner
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Executes a pipeline node and all of its nested variants. The node may be the top-level variant (when called from the variant-add path), a nested
    /// variant in a pipeline's <see cref="FileProcessingPipeline.Variants"/>, or the main pipeline of a transactional add.
    /// </summary>
    /// <remarks>
    /// Exactly one of <paramref name="streamSource"/> or <paramref name="fileSource"/> must be non-null. The runner appends to <paramref name="stagedNodes"/>;
    /// the caller is responsible for disposing all node contexts in the accumulated list (the runner does not roll back on failure).
    /// </remarks>
    private async Task RunNodeAsync(
        FileId fileId,
        Stream? streamSource,
        bool streamLeaveOpen,
        IAbsoluteFilePath? fileSource,
        string parentRootVariantId,
        string parentRootExtension,
        string? variantId,
        FileProcessingPipeline pipeline,
        VariantAddMode mode,
        Dictionary<string, IAbsoluteFilePath>? existingByVariantId,
        List<StagedNode> stagedNodes,
        CancellationToken cancellationToken)
    {
        Debug.Assert((streamSource is null) != (fileSource is null), "Exactly one of streamSource or fileSource must be non-null.");

        // GetOrAdd: if this variant already exists on disk, register it and recurse children against the existing file (resolving an alias marker if needed).
        if (variantId is not null && mode is VariantAddMode.GetOrAdd && existingByVariantId!.TryGetValue(variantId, out var existingFile))
        {
            IAbsoluteFilePath childSource;
            string childRootVariantId;
            string childRootExtension;

            if (existingFile.Extension is FileRepoPaths.AliasMarkerExtension)
            {
                if (!TryParseAliasMarker(existingFile, out _, out string? aliasSourceVid, out string? aliasSourceExt))
                {
                    throw new InvalidOperationException(
                        $"File ID '{fileId}' variant '{variantId}' alias marker '{existingFile.Name}' is malformed.");
                }

                var resolved = GetDataFile(_filesDirectory, fileId, aliasSourceExt, aliasSourceVid);
                if (resolved.State is not EntryState.Exists)
                {
                    // Defensive: alias resolution failed under the variant lock. Treat as non-existing so it can be properly recreated.
                    existingByVariantId.Remove(variantId);
                }
                else
                {
                    stagedNodes.Add(new StagedNode
                    {
                        VariantId = variantId,
                        Path = resolved,
                        Extension = aliasSourceExt,
                        Context = null,
                        IsExisting = true,
                        RootVariantId = aliasSourceVid ?? FileRepoPaths.MainFileName,
                        RootExtension = aliasSourceExt,
                    });

                    childSource = resolved;
                    childRootVariantId = aliasSourceVid ?? FileRepoPaths.MainFileName;
                    childRootExtension = aliasSourceExt;

                    await RunChildrenAsync(fileId, childSource, childRootVariantId, childRootExtension, pipeline.Variants, mode, existingByVariantId, stagedNodes, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                stagedNodes.Add(new StagedNode
                {
                    VariantId = variantId,
                    Path = existingFile,
                    Extension = existingFile.Extension,
                    Context = null,
                    IsExisting = true,
                    RootVariantId = variantId,
                    RootExtension = existingFile.Extension,
                });

                await RunChildrenAsync(fileId, existingFile, variantId, existingFile.Extension, pipeline.Variants, mode, existingByVariantId, stagedNodes, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Create a context with its own temp working dir.
        var tempWorkingDir = _tempDirectory.CombineDirectory(GetFileIdAndVariantString(fileId, variantId), PathOptions.None);
        FileProcessingContext context;

        if (streamSource is not null)
            context = new FileProcessingContext(fileId, variantId, tempWorkingDir, streamSource, parentRootExtension, streamLeaveOpen, cancellationToken);
        else
            context = new FileProcessingContext(fileId, variantId, tempWorkingDir, fileSource!, cancellationToken);

        IAbsoluteFilePath? stagedResult;

        try
        {
            await pipeline.ExecuteAsync(context, knownRepoFileSource: fileSource is not null, isMainPipeline: variantId is null).ConfigureAwait(false);

            stagedResult = await context.GetSourceAsFileAsync().ConfigureAwait(false);

            // Make sure the staged result is inside the temp working dir so the batch-move is a same-volume rename.
            if (!context.IsTempWorkingFile(stagedResult))
            {
                var workFile = context.GetNewWorkFile(context.Extension);
                workFile.ParentDirectory.Create();
                await stagedResult.CopyToAsync(workFile, cancellationToken).ConfigureAwait(false);
                stagedResult = workFile;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (FileSourceUnchangedException) when (variantId is not null && pipeline.AliasWhenVariantSourceUnchanged)
        {
            // Variant pipeline produced no changes: instead of storing a duplicate data file, stage a zero-byte alias marker that points to the resolved root
            // data file for the parent. The marker becomes a first-class addressable variant on disk and lets future GetOrAdd fast-paths succeed for this
            // variant. Children still run against the unchanged parent source.

            tempWorkingDir.Create();
            string aliasName = BuildAliasFileName(variantId, parentRootVariantId, parentRootExtension);
            var aliasStaged = tempWorkingDir.CombineFile(aliasName, PathOptions.None);

            using (var stream = aliasStaged.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Zero-byte marker; nothing to write.
            }

            // Children run against the unchanged parent data. For a file-sourced node that is fileSource directly; for a stream-sourced node (the top-level
            // variant-add path) the parent data must be materialized from the context. Either way the context owns tempWorkingDir (which holds the staged
            // alias marker, and possibly the materialized source), so it must not be disposed here — it is handed to the staged node and disposed in the outer
            // finally only after the batch-move has relocated the marker out of the temp dir.
            var childSource = fileSource ?? await context.GetSourceAsFileAsync().ConfigureAwait(false);

            stagedNodes.Add(new StagedNode
            {
                VariantId = variantId,
                Path = aliasStaged,
                Extension = FileRepoPaths.AliasMarkerExtension,
                Context = context,
                IsExisting = false,
                RootVariantId = parentRootVariantId,
                RootExtension = parentRootExtension,
            });

            await RunChildrenAsync(fileId, childSource, parentRootVariantId, parentRootExtension, pipeline.Variants, mode, existingByVariantId, stagedNodes, cancellationToken).ConfigureAwait(false);

            return;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        string thisRootVariantId = variantId ?? FileRepoPaths.MainFileName;
        string thisRootExtension = context.Extension;

        stagedNodes.Add(new StagedNode
        {
            VariantId = variantId,
            Path = stagedResult,
            Extension = context.Extension,
            Context = context,
            IsExisting = false,
            RootVariantId = thisRootVariantId,
            RootExtension = thisRootExtension,
        });

        await RunChildrenAsync(fileId, stagedResult, thisRootVariantId, thisRootExtension, pipeline.Variants, mode, existingByVariantId, stagedNodes, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunChildrenAsync(
        FileId fileId,
        IAbsoluteFilePath parentSource,
        string parentRootVariantId,
        string parentRootExtension,
        IReadOnlyList<FileProcessingVariant> variants,
        VariantAddMode mode,
        Dictionary<string, IAbsoluteFilePath>? existingByVariantId,
        List<StagedNode> stagedNodes,
        CancellationToken cancellationToken)
    {
        if (variants.Count is 0)
            return;

        foreach (var v in variants)
        {
            var childPipeline = v.Pipeline.GetPipeline();

            await RunNodeAsync(
                fileId,
                streamSource: null,
                streamLeaveOpen: false,
                fileSource: parentSource,
                parentRootVariantId,
                parentRootExtension,
                v.VariantId,
                childPipeline,
                mode,
                existingByVariantId,
                stagedNodes,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds an alias marker filename of the form <c>{variantId}.{sourceVariantId}.{sourceExt}.alias</c>.
    /// </summary>
    private static string BuildAliasFileName(string variantId, string sourceVariantId, string sourceExtension)
    {
        string extPart = sourceExtension.Length is 0 ? string.Empty : sourceExtension[1..];
        return $"{variantId}.{sourceVariantId}.{extPart}{FileRepoPaths.AliasMarkerExtension}";
    }

    /// <summary>
    /// Walks the static variant tree in pre-order, attempting to locate every declared variant on disk. Returns <see langword="true"/> if every variant in the
    /// tree (including the root) was found and appended to <paramref name="results"/>; otherwise returns <see langword="false"/> with the partial results left
    /// in an undefined state (the caller should discard them). Alias markers are resolved transparently; the returned <see cref="RepoFileInfo.Path"/> always
    /// references a real data file. A dangling alias (source data file missing) aborts the fast path so the locked path can recreate the variant.
    /// </summary>
    private bool TryCollectExistingVariants(FileId fileId, string rootVariantId, FileProcessingPipeline rootPipeline, List<RepoFileInfo> results)
    {
        if (!TryAppend(rootVariantId))
            return false;

        foreach (var v in rootPipeline.AllVariants)
        {
            if (!TryAppend(v.VariantId))
                return false;
        }

        return true;

        bool TryAppend(string variantId)
        {
            var existing = FindDataFile(_filesDirectory, fileId, variantId, out bool isRetired);

            // Pending retirement cleanup: abort fast path so the locked path can throw RepoVariantPendingCleanupException under the lock.
            if (isRetired)
                return false;

            if (existing is null)
                return false;

            if (existing.Extension is FileRepoPaths.AliasMarkerExtension)
            {
                var resolved = ResolveAlias(_filesDirectory, fileId, existing);
                if (resolved is null)
                    return false;

                results.Add(new RepoFileInfo(fileId, variantId, resolved));
            }
            else
            {
                results.Add(new RepoFileInfo(fileId, variantId, existing));
            }

            return true;
        }
    }

    /// <summary>
    /// Adds the variant IDs of every entry in the pipeline's pre-validated flat variant tree to <paramref name="destination"/>. Throws
    /// <see cref="ArgumentException"/> if any of them collides with an ID already in the set (typically the caller-supplied root variant ID).
    /// </summary>
    private static void AddVariantIds(FileProcessingPipeline pipeline, SortedSet<string> destination)
    {
        foreach (var v in pipeline.AllVariants)
        {
            if (!destination.Add(v.VariantId))
                throw new ArgumentException($"Pipeline variants processing tree contains duplicate variant ID '{v.VariantId}'.", nameof(pipeline));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Batch move (variant path, non-transactional)
    // ---------------------------------------------------------------------------------------------------------------

    private async Task BatchMoveVariantsAsync(FileId fileId, List<StagedNode> stagedNodes, CancellationToken cancellationToken)
    {
        var fileDir = GetFileDirectory(_filesDirectory, fileId);
        var failed = new List<(StagedNode Node, Exception Ex)>();
        var moved = new List<StagedNode>();

        foreach (var node in stagedNodes)
        {
            if (node.IsExisting)
            {
                moved.Add(node);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var dest = GetStagedDestination(node, fileDir, fileId);

            try
            {
                node.Path.MoveTo(dest, overwrite: false);
                node.Path = dest;
                moved.Add(node);
            }
            catch (DirectoryNotFoundException) when (fileDir.State is EntryState.ParentExists)
            {
                // The file directory was deleted concurrently. Treat the variant as successfully added; cleanup of the (already-deleted) file group has
                // already taken the file with it.
                node.Path = dest;
                moved.Add(node);
            }
            catch (Exception ex)
            {
                failed.Add((node, ex));
            }
        }

        if (failed.Count is 0)
            return;

        string movedIds = string.Join(", ", moved.Where(m => !m.IsExisting).Select(m => m.VariantId ?? "(main)"));
        string failedIds = string.Join(", ", failed.Select(f => f.Node.VariantId ?? "(main)"));

        throw new AggregateException(
            $"Failed to move {failed.Count} variant(s) into the repository (succeeded: [{movedIds}]; failed: [{failedIds}]).",
            failed.Select(f => f.Ex));
    }

    private IAbsoluteFilePath GetStagedDestination(StagedNode node, IAbsoluteDirectoryPath fileDir, FileId fileId)
    {
        if (node.Extension is FileRepoPaths.AliasMarkerExtension)
        {
            Debug.Assert(node.VariantId is not null, "Alias staged nodes must have a variant ID (main file is never aliased).");
            string? sourceVariantIdOrNull = node.RootVariantId == FileRepoPaths.MainFileName ? null : node.RootVariantId;
            return GetAliasMarker(fileDir, node.VariantId, sourceVariantIdOrNull, node.RootExtension);
        }

        return GetDataFile(_filesDirectory, fileId, node.Extension, node.VariantId);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Transactional main + variants (called by TxnAddAsync)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds a new main file (and any auto-variants declared by the pipeline) to the repository. Creates an indeterminate marker for the file and keeps an
    /// exclusive handle on it open so that an active transaction can be detected by cleanup operations. The returned marker handle must be disposed by the
    /// caller when the transaction commits or rolls back.
    /// </summary>
    internal async Task<(RepoFileGroupInfo Group, IAsyncDisposable IndeterminateMarker)> AddTransactionalAsyncCore(
        FileId fileId,
        Stream stream,
        string extension,
        bool leaveOpen,
        IFileProcessingPipelineSelector rootSelector,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        extension = FileExtension.Normalize(extension);
        var rootPipeline = rootSelector.GetPipeline(extension);

        // Validate variant id uniqueness in the static tree. Main itself has no variant id, so we just walk children.
        var declaredIds = new SortedSet<string>(StringComparer.Ordinal);
        AddVariantIds(rootPipeline, declaredIds);

        var stagedNodes = new List<StagedNode>();
        bool succeeded = false;
        IAsyncDisposable? markerHandle = null;

        try
        {
            // Run the main pipeline and any auto-variants. No variant locks needed: the file ID is freshly minted and not externally visible. The main file is
            // its own root for any child aliases.
            await RunNodeAsync(
                fileId,
                streamSource: stream,
                streamLeaveOpen: leaveOpen,
                fileSource: null,
                parentRootVariantId: FileRepoPaths.MainFileName,
                parentRootExtension: extension,
                variantId: null,
                rootPipeline,
                VariantAddMode.Add,
                existingByVariantId: null,
                stagedNodes,
                cancellationToken).ConfigureAwait(false);

            // Batch-move under indeterminate marker.
            markerHandle = await BatchMoveTransactionalAsync(fileId, stagedNodes, cancellationToken).ConfigureAwait(false);

            succeeded = true;

            // Build group result. The first staged node is always the main file (variant id null).
            Debug.Assert(stagedNodes.Count >= 1 && stagedNodes[0].VariantId is null, "Main file must be the first staged node in a transactional add.");

            var mainNode = stagedNodes[0];
            var mainInfo = new RepoFileInfo(fileId, variantId: null, mainNode.Path);

            var variantInfos = stagedNodes.Skip(1).Select(n => new RepoFileInfo(fileId, n.VariantId!, n.Path));

            return (new RepoFileGroupInfo(mainInfo, variantInfos), markerHandle);
        }
        finally
        {
            foreach (var node in stagedNodes)
            {
                if (node.Context is not null)
                {
                    try { await node.Context.DisposeAsync().ConfigureAwait(false); }
                    catch { }
                }
            }

            if (!succeeded && markerHandle is not null)
            {
                try { await markerHandle.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private async Task<IAsyncDisposable> BatchMoveTransactionalAsync(FileId fileId, List<StagedNode> stagedNodes, CancellationToken cancellationToken)
    {
        Debug.Assert(stagedNodes.Count >= 1 && stagedNodes[0].VariantId is null, "Transactional add must produce a main file as the first staged node.");

        var indeterminateMarker = GetIndeterminateMarker(_cleanupDirectory, fileId);
        var fileDir = GetFileDirectory(_filesDirectory, fileId);

        const string message = "A transaction has tentatively added this file.";
        var markerHandle = await CreateExclusiveMarkerAsync(indeterminateMarker, "TRANSACTION PENDING ADD", message, Options.MarkerFileLogging).ConfigureAwait(false);

        try
        {
            Debug.Assert(fileDir.State is EntryState.ParentExists or EntryState.ParentDoesNotExist, "File group dir is in an unexpected state.");
            fileDir.Create();

            foreach (var node in stagedNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Debug.Assert(!node.IsExisting, "Transactional add should never produce pre-existing staged nodes.");

                var dest = GetStagedDestination(node, fileDir, fileId);
                node.Path.MoveTo(dest, overwrite: false);
                node.Path = dest;
            }

            return markerHandle;
        }
        catch
        {
            await markerHandle.DisposeAsync().ConfigureAwait(false);

            if (!fileDir.TryDelete(recursive: true, out var ex) || !indeterminateMarker.TryDelete(out ex))
                await LogToOptionalMarkerAsync(indeterminateMarker, "ABORTED ADD CLEANUP FAILED", ex!, Options.MarkerFileLogging).ConfigureAwait(false);

            throw;
        }
    }
}
