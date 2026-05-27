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
    /// <returns>The list of stored variants in pre-order (top-level first, then nested in declaration order). Variants whose pipelines were skipped due to
    /// <see cref="FileProcessingPipeline.SkipWhenSourceUnchanged"/> are omitted.</returns>
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
    /// declared variant ID in the tree is represented in the returned list except for those that skip due to
    /// <see cref="FileProcessingPipeline.SkipWhenSourceUnchanged"/>.
    /// </summary>
    /// <remarks>
    /// Post-call existence is not guaranteed for variants whose pipelines opt into <see cref="FileProcessingPipeline.SkipWhenSourceUnchanged"/>: when the
    /// pipeline produces no changes the variant is silently skipped, but its nested children still run against the unchanged parent source.
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

        // Locate the source file to obtain the extension used to resolve the root pipeline.
        var sourceFile = FindDataFile(_filesDirectory, fileId, sourceVariantId)
            ?? throw new RepoFileNotFoundException(
                sourceVariantId is null
                    ? $"File ID '{fileId}' was not found."
                    : $"File ID '{fileId}' or its source variant '{sourceVariantId}' was not found.");

        var rootPipeline = rootSelector.GetPipeline(sourceFile.Extension);

        // Discover every variant ID statically declared in the tree (including the user-provided root variant ID).
        var allVariantIds = new SortedSet<string>(StringComparer.Ordinal) { variantId };
        CollectVariantIds(rootPipeline, allVariantIds, variantId);

        var locks = new List<IDisposable>(allVariantIds.Count);
        var stagedNodes = new List<StagedNode>();
        FileStream? sourceFileStream = null;

        try
        {
            // Acquire per-variant locks in sorted order for deadlock-freedom.
            var lockTimeout = mode is VariantAddMode.Try ? TimeSpan.Zero : Timeout.InfiniteTimeSpan;

            foreach (string vid in allVariantIds)
            {
                try
                {
                    var lockHandle = await _fileSync.LockAsync((fileId, vid), lockTimeout, cancellationToken).ConfigureAwait(false);

#pragma warning disable RS0042 // Unsupported use of non-copyable type — boxing the lock handle here is intentional.
                    locks.Add(lockHandle);
#pragma warning restore RS0042
                }
                catch (TimeoutException) when (mode is VariantAddMode.Try)
                {
                    return null;
                }
            }

            // Re-check disk existence for every declared variant ID under the locks.
            var existingByVariantId = new Dictionary<string, IAbsoluteFilePath>(StringComparer.Ordinal);

            foreach (string vid in allVariantIds)
            {
                var existing = FindDataFile(_filesDirectory, fileId, vid);
                if (existing is not null)
                    existingByVariantId[vid] = existing;
            }

            if (existingByVariantId.Count > 0 && mode is not VariantAddMode.GetOrAdd)
            {
                if (mode is VariantAddMode.Try)
                    return null;

                // Add mode: throw naming the first colliding variant ID (in sorted enumeration order, for stability).
                string firstCollision = allVariantIds.First(existingByVariantId.ContainsKey);
                throw new InvalidOperationException($"File ID '{fileId}' variant '{firstCollision}' already exists.");
            }

            // Open the source file stream that the root pipeline will read from.
            sourceFileStream = sourceFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            // Run the tree. The context takes ownership of the stream (leaveOpen=false) and disposes it once it has materialized the source as a file.
            await RunNodeAsync(
                fileId,
                streamSource: sourceFileStream,
                streamLeaveOpen: false,
                fileSource: null,
                sourceExtension: sourceFile.Extension,
                variantId,
                rootPipeline,
                mode,
                existingByVariantId,
                stagedNodes,
                cancellationToken).ConfigureAwait(false);

            sourceFileStream = null;

            // Batch-move into files/.
            await BatchMoveVariantsAsync(fileId, stagedNodes, cancellationToken).ConfigureAwait(false);

            // Build result.
            var results = new List<RepoFileInfo>(stagedNodes.Count);
            foreach (var node in stagedNodes)
                results.Add(new RepoFileInfo(fileId, node.VariantId, node.Path));

            return results;
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

            if (sourceFileStream is not null)
            {
                try { await sourceFileStream.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }

            // Release locks in reverse acquisition order.
            for (int i = locks.Count - 1; i >= 0; i--)
                locks[i].Dispose();
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
        string sourceExtension,
        string? variantId,
        FileProcessingPipeline pipeline,
        VariantAddMode mode,
        Dictionary<string, IAbsoluteFilePath>? existingByVariantId,
        List<StagedNode> stagedNodes,
        CancellationToken cancellationToken)
    {
        Debug.Assert((streamSource is null) != (fileSource is null), "Exactly one of streamSource or fileSource must be non-null.");

        // GetOrAdd: if this variant already exists on disk, register it and recurse children against the existing file.
        if (variantId is not null && mode is VariantAddMode.GetOrAdd && existingByVariantId!.TryGetValue(variantId, out var existingFile))
        {
            stagedNodes.Add(new StagedNode
            {
                VariantId = variantId,
                Path = existingFile,
                Extension = existingFile.Extension,
                Context = null,
                IsExisting = true,
            });

            await RunChildrenAsync(fileId, existingFile, pipeline.Variants, mode, existingByVariantId, stagedNodes, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Create a context with its own temp working dir.
        var tempWorkingDir = _tempDirectory.CombineDirectory(GetFileIdAndVariantString(fileId, variantId), PathOptions.None);
        FileProcessingContext context;

        if (streamSource is not null)
            context = new FileProcessingContext(fileId, variantId, tempWorkingDir, streamSource, sourceExtension, streamLeaveOpen, cancellationToken);
        else
            context = new FileProcessingContext(fileId, variantId, tempWorkingDir, fileSource!, cancellationToken);

        IAbsoluteFilePath? stagedResult;

        try
        {
            await pipeline.ExecuteAsync(context, knownRepoFileSource: fileSource is not null).ConfigureAwait(false);

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
        catch (FileSourceUnchangedException) when (variantId is not null && pipeline.SkipWhenSourceUnchanged)
        {
            // Skip storing this variant; dispose its context and continue with the unchanged parent source for children.
            await context.DisposeAsync().ConfigureAwait(false);

            await RunChildrenAsync(
                fileId,
                fileSource ?? throw new InvalidOperationException("SkipWhenSourceUnchanged should never trigger when the source is a stream (main pipeline)."),
                pipeline.Variants,
                mode,
                existingByVariantId,
                stagedNodes,
                cancellationToken).ConfigureAwait(false);

            return;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        stagedNodes.Add(new StagedNode
        {
            VariantId = variantId,
            Path = stagedResult,
            Extension = context.Extension,
            Context = context,
            IsExisting = false,
        });

        await RunChildrenAsync(fileId, stagedResult, pipeline.Variants, mode, existingByVariantId, stagedNodes, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunChildrenAsync(
        FileId fileId,
        IAbsoluteFilePath parentSource,
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
                sourceExtension: parentSource.Extension,
                v.VariantId,
                childPipeline,
                mode,
                existingByVariantId,
                stagedNodes,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Walks the static variant tree of the specified pipeline, accumulating variant IDs into the destination set. Throws <see cref="ArgumentException"/> if a
    /// duplicate ID is encountered.
    /// </summary>
    private static void CollectVariantIds(FileProcessingPipeline pipeline, SortedSet<string> destination, string? rootIdForMessage)
    {
        foreach (var v in pipeline.Variants)
        {
            if (!destination.Add(v.VariantId))
            {
                string root = rootIdForMessage is null ? "tree" : $"tree rooted at '{rootIdForMessage}'";
                throw new ArgumentException($"Variant {root} contains duplicate variant ID '{v.VariantId}'.", nameof(pipeline));
            }

            // Variant pipelines are statically shaped (IFileProcessingPipelineProvider), so we can resolve their concrete FileProcessingPipeline without
            // knowing the source extension - which makes upfront enumeration of the entire nested variant tree possible.
            var childPipeline = v.Pipeline.GetPipeline();
            CollectVariantIds(childPipeline, destination, rootIdForMessage);
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

            var dest = GetDataFile(_filesDirectory, fileId, node.Extension, node.VariantId);

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
        CollectVariantIds(rootPipeline, declaredIds, rootIdForMessage: null);

        var stagedNodes = new List<StagedNode>();
        bool succeeded = false;
        IAsyncDisposable? markerHandle = null;

        try
        {
            // Run the main pipeline and any auto-variants. No variant locks needed: the file ID is freshly minted and not externally visible.
            await RunNodeAsync(
                fileId,
                streamSource: stream,
                streamLeaveOpen: leaveOpen,
                fileSource: null,
                sourceExtension: extension,
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

                var dest = GetDataFile(_filesDirectory, fileId, node.Extension, node.VariantId);
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
