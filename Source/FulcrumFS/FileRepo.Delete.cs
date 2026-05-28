namespace FulcrumFS;

/// <content>
/// Contains the implementations of delete file functionality for the file repository.
/// </content>
partial class FileRepo
{
    // Bounds the lock-set discovery retry loop in DeleteVariantsCoreAsync. Convergence is guaranteed once every involved source variant's lock is held (no new
    // dependents can be added under a held source lock), so in practice this is reached in one or two attempts; the cap only guards against pathological churn.
    private const int MaxDeleteLockDiscoveryAttempts = 8;

    /// <summary>
    /// Permanently retires the specified variants of a file, automatically preserving any dependent variants the caller did not list.
    /// </summary>
    /// <param name="fileId">The ID of the file whose variants should be retired.</param>
    /// <param name="variantIds">The full set of variant IDs the caller owns and wants retired. Duplicates are ignored and ordering is irrelevant.</param>
    /// <remarks>
    /// <para>
    /// Pass <em>every</em> variant ID you want gone. Any variant that is <em>not</em> listed but depends (via an alias) on a listed variant is automatically
    /// preserved: the earliest-created surviving dependent is promoted to a standalone data file and the remaining survivors are re-pointed at it before the
    /// listed source is retired. To retire a variant together with its dependents, simply include those dependents in the list.</para>
    /// <para>
    /// This API is intended for permanent <em>retirement</em> of variant IDs. Downstream consumers, notably static file hosts serving repo files by URL,
    /// typically depend on stable variant identity, and the repository does not provide an atomic delete-then-add "replacement" story. The normal way to
    /// update a variant is to add a new variant under a new ID, transition consumers to the new ID, and then retire the old one.</para>
    /// <para>
    /// Retirement is visible to fetches immediately: <see cref="GetVariantAsync(FileId, string)"/> throws <see cref="RepoFileNotFoundException"/> against a
    /// retired variant even while the underlying data file may still linger on disk in deferred-delete mode. Within a single call, every listed variant's
    /// retirement is committed (its in-group delete marker written) before any physical teardown begins, so a concurrent fetch never observes a partially-applied
    /// subset (modulo the brief window between individual delete marker writes).</para>
    /// <para>
    /// Whether physical removal of data files is immediate or deferred is governed by <see cref="FileRepoOptions.DeleteMode"/>. The method is fully idempotent:
    /// an empty list, already-retired variants, fully cleaned-up variants, and never-existed variant IDs are all silently skipped.</para>
    /// </remarks>
    public ValueTask DeleteVariantsAsync(FileId fileId, params ReadOnlySpan<string> variantIds)
    {
        if (variantIds.Length is 0)
            return ValueTask.CompletedTask;

        // Normalize, dedup and sort up front: the outcome is invariant under input ordering and duplicate IDs, and sorted order is the basis for
        // deadlock-free lock acquisition. This must happen synchronously because a ReadOnlySpan cannot be captured by the async core.
        var listed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string variantId in variantIds)
            listed.Add(VariantId.Normalize(variantId));

        return DeleteVariantsCoreAsync(fileId, listed);
    }

    private async ValueTask DeleteVariantsCoreAsync(FileId fileId, SortedSet<string> listed)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        for (int attempt = 0; ; attempt++)
        {
            if (attempt >= MaxDeleteLockDiscoveryAttempts)
            {
                throw new IOException(
                    $"Unable to stabilize the variant lock set for deletion of file ID '{fileId}' after {MaxDeleteLockDiscoveryAttempts} attempts due to " +
                    $"concurrent variant additions or unsettled rebase state. Retry the operation.");
            }

            // Optimistic rollforward: a lock-free inventory pass tells us both the candidate lock set (the listed variants plus every survivor a rebase will
            // promote/re-point) and whether any pre-existing rebase marker is present. The whole-system invariant is that no add/delete plans while a rebase
            // marker exists, so if one is present we roll it forward to completion under its own locks (holding none of ours) and restart fresh against a
            // settled, single-head subtree.
            BuildDeletePlan(fileId, listed, out var candidateLocks, out bool hasPendingRebases);

            if (hasPendingRebases)
            {
                await RollForwardPendingRebasesAsync(fileId, default).ConfigureAwait(false);
                continue;
            }

            var locks = new List<KeyLock<(FileId, string?)>>(candidateLocks.Count);

            try
            {
                foreach (string vid in candidateLocks)
                    locks.Add(await _fileSync.LockAsync((fileId, vid)).ConfigureAwait(false));

                // Re-inventory under the acquired locks. Holding the listed (source) locks freezes the dependent sets, so the required lock set is now stable.
                var plan = BuildDeletePlan(fileId, listed, out var requiredLocks, out hasPendingRebases);

                if (hasPendingRebases)
                {
                    // A rebase marker appeared between the lock-free pass and lock acquisition (a concurrent crash recovery or cross-process retirement).
                    // Release our locks (in the finally) and restart; the next iteration's lock-free pass rolls it forward.
                    continue;
                }

                if (!requiredLocks.IsSubsetOf(candidateLocks))
                {
                    // A concurrent add introduced a new dependent between the lock-free pass and lock acquisition. Release and retry with the wider set.
                    continue;
                }

                if (plan is null)
                    return; // Nothing to do — every listed variant is already retired or absent.

                await ExecuteDeletePlanAsync(fileId, plan).ConfigureAwait(false);
                return;
            }
            finally
            {
                locks.ReverseDisposeAll();
            }
        }
    }

    /// <summary>
    /// Rolls every pending rebase in a file group forward to completion under its own per-variant locks, then returns. Shared optimistic pre-step for the add
    /// and delete paths: a mutating operation that observes a pending <c>.rebase</c> marker (a crash-interrupted, or in-progress cross-process, retirement)
    /// calls this with none of its own locks held, then restarts so it plans against a settled, single-head subtree. Idempotent and a no-op when there is
    /// nothing to roll forward. The chosen survivor pinned in each marker is honored (never recomputed) so the foreground and a concurrent cleaner converge
    /// on the same survivor. Degenerate/orphaned markers (no derivable source extension, hence no real data to fork) are left for the cleaner and do not
    /// block add/delete.
    /// </summary>
    /// <param name="fileId">The ID of the file whose pending rebases are rolled forward.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous copy operations.</param>
    private async ValueTask RollForwardPendingRebasesAsync(FileId fileId, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (attempt >= MaxDeleteLockDiscoveryAttempts)
            {
                throw new IOException(
                    $"Unable to stabilize the variant lock set to roll pending rebases of file ID '{fileId}' forward after {MaxDeleteLockDiscoveryAttempts} " +
                    $"attempts due to concurrent variant additions. Retry the operation.");
            }

            var inv = BuildGroupInventory(_filesDirectory, fileId);
            ComputeResumeRebases(inv, out var candidateLocks);

            if (candidateLocks.Count is 0)
                return; // Nothing (left) to roll forward.

            var locks = new List<KeyLock<(FileId, string?)>>(candidateLocks.Count);

            try
            {
                foreach (string vid in candidateLocks)
                    locks.Add(await _fileSync.LockAsync((fileId, vid), Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false));

                var invLocked = BuildGroupInventory(_filesDirectory, fileId);
                var resumeRebases = ComputeResumeRebases(invLocked, out var requiredLocks);

                if (resumeRebases.Count is 0)
                    return; // Completed concurrently between the lock-free pass and lock acquisition.

                if (!requiredLocks.IsSubsetOf(candidateLocks))
                    continue; // A concurrent add widened a survivor set; retry with the wider lock set.

                foreach (var resume in resumeRebases)
                {
                    var ex = await RebaseSubtreeAsync(_filesDirectory, fileId, resume, DebugStepHook, cancellationToken).ConfigureAwait(false);

                    if (ex is not null)
                        throw new FileProcessingException($"Failed to roll a pending rebase of file ID '{fileId}' forward.", ex);
                }

                return;
            }
            finally
            {
                locks.ReverseDisposeAll();
            }
        }
    }

    /// <summary>
    /// Computes the rollforward plans for every pending rebase marker in a group inventory and the full set of variant locks completing them requires. The
    /// pinned chosen survivor recorded in each marker is honored (never recomputed). Degenerate/orphaned markers (those with no derivable source extension,
    /// source data gone, chosen data gone, no surviving aliases, and hence no real data to fork) are skipped entirely and contribute no participant locks,
    /// so they neither block nor loop add/delete; the cleaner is left to remove them.
    /// </summary>
    /// <param name="inv">The group inventory to scan for rebase markers.</param>
    /// <param name="participantLocks">Receives the variant IDs whose locks completing the returned rebases requires (sources, chosen survivors and re-pointed survivors).</param>
    private static List<SubtreeRebasePlan> ComputeResumeRebases(GroupVariantInventory inv, out SortedSet<string> participantLocks)
    {
        participantLocks = new SortedSet<string>(StringComparer.Ordinal);
        var resumeRebases = new List<SubtreeRebasePlan>();

        foreach (var (_, source, chosen) in inv.RebaseMarkers)
        {
            var resumeRepoint = new List<string>();
            string? aliasSourceExtension = null;

            foreach (var a in inv.Aliases)
            {
                if (!string.Equals(a.SourceVariantId, source, StringComparison.Ordinal))
                    continue;

                aliasSourceExtension ??= a.SourceExtension;

                if (!string.Equals(a.VariantId, chosen, StringComparison.Ordinal))
                    resumeRepoint.Add(a.VariantId);
            }

            // The source extension is needed to address the data files and markers. It is the source data file's extension while the source persists as
            // residue; once the source is gone it equals the (content-identical) chosen data file's extension, and failing that the survivors' encoded source
            // extension. If none can be derived the marker is degenerate/orphaned — skip it (and add no locks) so it neither blocks nor loops the caller.
            string? sourceExtension =
                inv.DataFiles.TryGetValue(source, out var srcData) ? srcData.Extension :
                inv.DataFiles.TryGetValue(chosen, out var chosenData) ? chosenData.Extension :
                aliasSourceExtension;

            if (sourceExtension is null)
                continue;

            participantLocks.Add(source);
            participantLocks.Add(chosen);

            foreach (string r in resumeRepoint)
                participantLocks.Add(r);

            resumeRebases.Add(new SubtreeRebasePlan(source, sourceExtension, chosen, resumeRepoint));
        }

        return resumeRebases;
    }

    /// <summary>
    /// Builds the physical teardown plan for retiring <paramref name="listed"/> and computes the full set of variant locks the execution will require
    /// (the listed variants plus every survivor that will be promoted or re-pointed by a rebase). Returns <see langword="null"/> when nothing needs retiring.
    /// </summary>
    /// <param name="fileId">The ID of the file whose variants are being retired.</param>
    /// <param name="listed">The normalized, sorted set of variant IDs the caller wants retired.</param>
    /// <param name="requiredLocks">Receives the full set of variant IDs whose locks the execution requires (listed variants and rebase survivors).</param>
    /// <param name="hasPendingRebases">
    /// Receives <see langword="true"/> when the group contains a pre-existing rebase marker that can be rolled forward (a crash-interrupted, or in-progress
    /// cross-process, retirement). The caller must roll these forward to completion and re-plan before acting, so it never plans against a half-finished
    /// subtree. The returned plan and <paramref name="requiredLocks"/> are not meaningful in that case.
    /// </param>
    private VariantDeletePlan? BuildDeletePlan(
        FileId fileId, SortedSet<string> listed, out SortedSet<string> requiredLocks, out bool hasPendingRebases)
    {
        requiredLocks = new SortedSet<string>(listed, StringComparer.Ordinal);

        var inv = BuildGroupInventory(_filesDirectory, fileId);

        // Whole-system invariant: no add/delete plans while a (rollforward-able) rebase marker exists. If one is present the caller rolls it forward and
        // re-plans against a settled, single-head subtree. Degenerate/orphaned markers (no real data to fork) are ignored here so they neither block nor loop.
        hasPendingRebases = ComputeResumeRebases(inv, out _).Count > 0;

        var retireListed = new List<string>();
        var aliasMarkersToDelete = new List<IAbsoluteFilePath>();
        var realDataToDelete = new List<IAbsoluteFilePath>();
        var rebases = new List<SubtreeRebasePlan>();

        // Iterate in sorted order.
        foreach (string v in listed)
        {
            if (inv.Retired.Contains(v))
            {
                // Already retired — idempotent skip. Any unfinished physical teardown for it is owned by the cleaner.
                continue;
            }

            if (inv.DataFiles.TryGetValue(v, out var data))
            {
                // Real data file. Split its alias dependents into survivors (not listed → preserved via rebase) and listed dependents (retired separately).
                var survivors = new List<GroupAliasEntry>();

                foreach (var a in inv.Aliases)
                {
                    if (string.Equals(a.SourceVariantId, v, StringComparison.Ordinal) && !listed.Contains(a.VariantId))
                        survivors.Add(a);
                }

                retireListed.Add(v);

                // The source data file is always retired residue to be physically deleted later. Materialization copies (never moves) the source content into
                // the promoted survivor, so the source file must persist intact through materialization and is removed only in the deletion phase — this keeps
                // every variant (retired or not) resolving to real data in any backup snapshot taken mid-operation.
                realDataToDelete.Add(data.Path);

                if (survivors.Count is 0)
                    continue; // No survivors: plain real-variant retirement (its listed alias dependents, if any, are deleted via their own entries).

                // Promote the earliest survivor by (CreationTimeUtc, VariantId); VariantId is the deterministic tiebreaker so the cleaner would pick the same one.
                survivors.Sort(static (x, y) =>
                {
                    int c = x.CreationTimeUtc.CompareTo(y.CreationTimeUtc);
                    return c is not 0 ? c : string.CompareOrdinal(x.VariantId, y.VariantId);
                });

                string chosen = survivors[0].VariantId;
                var repoint = new List<string>(survivors.Count - 1);

                for (int i = 1; i < survivors.Count; i++)
                    repoint.Add(survivors[i].VariantId);

                rebases.Add(new SubtreeRebasePlan(v, data.Extension, chosen, repoint));

                requiredLocks.Add(chosen);

                foreach (string r in repoint)
                    requiredLocks.Add(r);
            }
            else
            {
                // Not a real data file — it may be an alias marker for this variant ID.
                foreach (var a in inv.Aliases)
                {
                    if (string.Equals(a.VariantId, v, StringComparison.Ordinal))
                    {
                        retireListed.Add(v);
                        aliasMarkersToDelete.Add(a.Marker);
                        break;
                    }
                }

                // Otherwise the variant is absent (never added, or fully cleaned up) — idempotent skip.
            }
        }

        if (retireListed.Count is 0)
            return null;

        return new VariantDeletePlan
        {
            RetireListed = retireListed,
            AliasMarkersToDelete = aliasMarkersToDelete,
            RealDataToDelete = realDataToDelete,
            Rebases = rebases,
        };
    }

    private async Task ExecuteDeletePlanAsync(FileId fileId, VariantDeletePlan plan)
    {
        var logging = Options.MarkerFileLogging;

        // Physical removal of retired residue is immediate in both Immediate and DeferredFilesOnly modes; only DeferredUntilClean defers it to the cleaner.
        // Materialization (promoting survivors) is ALWAYS performed in the foreground regardless of mode — the deferral only affects the follow-up deletion of
        // the retired source data files, alias markers and delete markers.
        bool immediate = Options.DeleteMode is DeleteMode.Immediate or DeleteMode.DeferredFilesOnly;

        // --- Pre-commit scaffolding: cleanup-dir hints. Inert on their own — a crash here is a no-op (a hint without a matching delete marker is swept away). ---
        foreach (string v in plan.RetireListed)
        {
            var hint = GetDeleteMarker(_cleanupDirectory, fileId, v);
            await LogToMarkerAsync(hint, "DELETE", "File variant has been marked for deletion.", logging).ConfigureAwait(false);
        }

        DebugStepHook?.Invoke(DebugStep.DeleteHintsWritten);

        // --- Commit: in-group delete markers. After this, every listed variant is retired from the fetch perspective. A crash at/after here rolls forward. ---
        foreach (string v in plan.RetireListed)
        {
            var deleteMarker = GetInGroupDeleteMarker(_filesDirectory, fileId, v);
            await LogToMarkerAsync(deleteMarker, "DELETE", "File variant has been retired.", logging).ConfigureAwait(false);
        }

        DebugStepHook?.Invoke(DebugStep.DeleteMarkersWritten);

        var elc = ExceptionListCapture.Default;

        // --- Materialize all rebases in the foreground (BOTH modes). Each chosen survivor is promoted by copying the retired source's content into the chosen
        //     alias marker and renaming it to a real data file; the source data file is left intact so every variant — retired or surviving — continues to
        //     resolve to real data in any backup snapshot taken mid-operation. The source residue is removed later (immediately below, or by the cleaner). ---
        foreach (var rebase in plan.Rebases)
        {
            var ex = await RebaseSubtreeAsync(_filesDirectory, fileId, rebase, DebugStepHook).ConfigureAwait(false);

            if (ex is not null)
            {
                elc.AddException(ex);
                break;
            }
        }

        if (elc.HasExceptions)
        {
            // Materialization failed mid-way. The committed delete markers, hints and any rebase markers remain for the cleaner to roll forward. Record diagnostics.
            foreach (string v in plan.RetireListed)
            {
                var hint = GetDeleteMarker(_cleanupDirectory, fileId, v);
                await LogToOptionalMarkerAsync(hint, "DELETE ATTEMPT FAILED", elc.ResultException, logging).ConfigureAwait(false);
            }

            return;
        }

        if (!immediate)
            return; // Deferred mode: the cleaner physically removes the retired residue (source data files, alias markers, delete markers) after the delete delay.

        DebugStepHook?.Invoke(DebugStep.DeleteResidueAboutToDelete);

        await PhysicallyDeleteRetiredResidueAsync(fileId, plan, logging).ConfigureAwait(false);
    }

    /// <summary>
    /// Immediate-mode follow-up to <see cref="ExecuteDeletePlanAsync"/>: physically removes the retired residue once materialization has completed: the listed
    /// alias markers, the retired real source data files, and finally the delete markers and cleanup hints. Mirrors the deferred-mode teardown performed by the
    /// cleaner so both paths converge on identical on-disk state.
    /// </summary>
    private async Task PhysicallyDeleteRetiredResidueAsync(FileId fileId, VariantDeletePlan plan, LoggingMode logging)
    {
        var elc = ExceptionListCapture.Default;

        // Delete the retired alias markers (listed dependents). Survivor aliases are never in this list — they were re-pointed during materialization.
        foreach (var marker in plan.AliasMarkersToDelete)
        {
            if (marker.State is EntryState.Exists && !marker.TryDelete(out var ex))
                elc.AddException(ex);
        }

        // Delete the retired real source data files. Materialization has already copied the content of any rebase source into its promoted survivor, so the
        // source data is now pure residue.
        foreach (var dataFile in plan.RealDataToDelete)
        {
            if (dataFile.State is EntryState.Exists && !dataFile.TryDelete(out var ex))
                elc.AddException(ex);
        }

        if (elc.HasExceptions)
        {
            // Leave the delete markers and hints in place for the cleaner to roll forward. Record the failure on each hint for diagnostics.
            foreach (string v in plan.RetireListed)
            {
                var hint = GetDeleteMarker(_cleanupDirectory, fileId, v);
                await LogToOptionalMarkerAsync(hint, "DELETE ATTEMPT FAILED", elc.ResultException, logging).ConfigureAwait(false);
            }

            return;
        }

        // --- Teardown (data files now gone): delete markers first (a delete marker must outlive its data file), then cleanup hints last. ---
        foreach (string v in plan.RetireListed)
        {
            var deleteMarker = GetInGroupDeleteMarker(_filesDirectory, fileId, v);

            if (!deleteMarker.TryDelete(out var deleteMarkerEx))
            {
                var hint = GetDeleteMarker(_cleanupDirectory, fileId, v);
                await LogToOptionalMarkerAsync(hint, "DELETE MARKER DELETE FAILED", deleteMarkerEx, logging).ConfigureAwait(false);
            }
        }

        foreach (string v in plan.RetireListed)
            GetDeleteMarker(_cleanupDirectory, fileId, v).TryDelete(out _);
    }

    /// <summary>
    /// Performs (or idempotently resumes) the rebase of a single subtree whose real source variant is being retired: promotes the chosen survivor to a
    /// standalone data file by copying the source's content into the chosen alias marker and renaming it to a real data file, then re-points the remaining
    /// survivors at the chosen variant. The source data file is never touched here; it is removed later as retired residue. Shared by the foreground path and
    /// the cleaner so both run identical logic. All steps are crash-safe and idempotent. Returns the first exception encountered, or <see langword="null"/> on
    /// success (an exception is returned rather than captured because the async copy cannot hold a <c>ref</c> to a <see cref="ExceptionListCapture"/>).
    /// </summary>
    /// <param name="filesDirectory">The repository files directory.</param>
    /// <param name="fileId">The file ID identifying the file group whose subtree is being rebased.</param>
    /// <param name="plan">The subtree rebase plan describing the source variant, chosen survivor and survivors to re-point.</param>
    /// <param name="debugStepHook">A test-only hook invoked after each rebase step; <see langword="null"/> in production use.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous copy operation.</param>
    internal static async Task<Exception?> RebaseSubtreeAsync(
        IAbsoluteDirectoryPath filesDirectory, FileId fileId, SubtreeRebasePlan plan, Action<DebugStep>? debugStepHook = null, CancellationToken cancellationToken = default)
    {
        var fileGroupDir = GetFileDirectory(filesDirectory, fileId);
        var rebaseMarker = GetRebaseMarker(filesDirectory, fileId, plan.SourceVariantId, plan.ChosenVariantId);
        var sourceData = GetDataFile(filesDirectory, fileId, plan.SourceExtension, plan.SourceVariantId);
        var chosenData = GetDataFile(filesDirectory, fileId, plan.SourceExtension, plan.ChosenVariantId);
        var chosenAlias = GetAliasMarker(fileGroupDir, plan.ChosenVariantId, plan.SourceVariantId, plan.SourceExtension);

        try
        {
            // 1. Pin the chosen survivor before mutating the directory. A crash-resuming cleaner reads this marker to attribute a half-finished subtree to the
            //    same chosen variant the foreground had selected.
            if (rebaseMarker.State is not EntryState.Exists)
            {
                try
                {
                    using (rebaseMarker.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                }
                catch (IOException) when (rebaseMarker.State is EntryState.Exists)
                {
                    // Concurrently created by a resuming pass — fine.
                }
            }

            debugStepHook?.Invoke(DebugStep.RebaseMarkerWritten);

            // 2. Materialize the chosen survivor: copy the source content INTO the chosen alias marker (a zero-byte file), then rename that single directory
            //    entry to the real data file. Because the alias resolves to the source's data throughout the copy, the chosen variant resolves to real content
            //    at every instant — there is never both a real data file and an alias marker for the chosen variant on disk at the same time.
            if (chosenData.State is not EntryState.Exists)
            {
                if (chosenAlias.State is not EntryState.Exists)
                {
                    return new InvalidOperationException(
                        $"Rebase of file ID '{fileId}' source variant '{plan.SourceVariantId}' onto '{plan.ChosenVariantId}' cannot proceed: neither the " +
                        $"materialized chosen data file nor the chosen alias marker is present. This indicates repository corruption.");
                }

                if (sourceData.State is not EntryState.Exists)
                {
                    return new InvalidOperationException(
                        $"Rebase of file ID '{fileId}' source variant '{plan.SourceVariantId}' onto '{plan.ChosenVariantId}' cannot proceed: the source data " +
                        $"file is missing while the chosen variant is still an unmaterialized alias. This indicates repository corruption.");
                }

                // Copy the source content INTO the chosen alias marker (overwriting the zero-byte placeholder), then rename that single directory entry to
                // the real data file. The chosen variant resolves to the source's data via the alias throughout the copy, so it always resolves to real
                // content and there is never both a real data file and an alias marker for the chosen variant on disk at once.
                await sourceData.CopyToAsync(chosenAlias, overwrite: true, cancellationToken).ConfigureAwait(false);

                chosenAlias.MoveTo(chosenData, overwrite: false);
            }

            debugStepHook?.Invoke(DebugStep.RebaseMaterialized);

            // 3. Re-point the remaining survivors from the source onto the (now-materialized) chosen variant.
            foreach (string dep in plan.RepointVariantIds)
            {
                var newMarker = GetAliasMarker(fileGroupDir, dep, plan.ChosenVariantId, plan.SourceExtension);

                if (newMarker.State is EntryState.Exists)
                    continue; // Already re-pointed.

                var oldMarker = GetAliasMarker(fileGroupDir, dep, plan.SourceVariantId, plan.SourceExtension);

                if (oldMarker.State is EntryState.Exists)
                    oldMarker.MoveTo(newMarker, overwrite: false);
            }

            debugStepHook?.Invoke(DebugStep.RebaseRepointed);

            // 4. Rebase complete — drop the pin.
            rebaseMarker.TryDelete(out _);

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Single-pass inventory of a file group directory, classifying every entry into data files, in-group delete markers (retired variants), alias dependents
    /// (with their source and creation time), and rebase markers. Used by both the delete planner and the cleaner's rebase-resume logic.
    /// </summary>
    internal static GroupVariantInventory BuildGroupInventory(IAbsoluteDirectoryPath filesDirectory, FileId fileId)
    {
        var inv = new GroupVariantInventory();
        var dir = GetFileDirectory(filesDirectory, fileId);

        try
        {
            foreach (var info in dir.GetChildFilesInfo())
            {
                var path = info.Path;
                string ext = path.Extension;

                if (ext is FileRepoPaths.DeleteMarkerExtension)
                {
                    string name = path.NameWithoutExtension;

                    if (VariantId.IsValidAndNormalized(name))
                        inv.Retired.Add(name);
                }
                else if (ext is FileRepoPaths.AliasMarkerExtension)
                {
                    if (TryParseAliasMarker(path, out string? vid, out string? src, out string? srcExt))
                        inv.Aliases.Add(new GroupAliasEntry(path, vid, src, srcExt, info.CreationTimeUtc));
                }
                else if (ext is FileRepoPaths.RebaseMarkerExtension)
                {
                    if (TryParseRebaseMarker(path, out string? src, out string? chosen))
                        inv.RebaseMarkers.Add((path, src, chosen));
                }
                else if (FileExtension.IsValidAndNormalized(ext))
                {
                    string name = path.NameWithoutExtension;

                    if (name == FileRepoPaths.MainFileName || VariantId.IsValidAndNormalized(name))
                        inv.DataFiles[name] = (path, ext);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            inv.DirectoryExists = false;
        }

        return inv;
    }

    /// <summary>
    /// A planned rebase of one subtree: the listed real <paramref name="SourceVariantId"/> is retired and its data (with <paramref name="SourceExtension"/>)
    /// is copied into <paramref name="ChosenVariantId"/> (which is promoted to a standalone data file); the survivors in <paramref name="RepointVariantIds"/>
    /// are re-pointed at the chosen variant. The source data file is left intact for later residue deletion.
    /// </summary>
    internal readonly record struct SubtreeRebasePlan(string SourceVariantId, string SourceExtension, string ChosenVariantId, List<string> RepointVariantIds);

    private sealed class VariantDeletePlan
    {
        public required List<string> RetireListed { get; init; }

        public required List<IAbsoluteFilePath> AliasMarkersToDelete { get; init; }

        public required List<IAbsoluteFilePath> RealDataToDelete { get; init; }

        public required List<SubtreeRebasePlan> Rebases { get; init; }
    }

    internal sealed class GroupVariantInventory
    {
        public Dictionary<string, (IAbsoluteFilePath Path, string Extension)> DataFiles { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Retired { get; } = new(StringComparer.Ordinal);

        public List<GroupAliasEntry> Aliases { get; } = [];

        public List<(IAbsoluteFilePath Marker, string SourceVariantId, string ChosenVariantId)> RebaseMarkers { get; } = [];

        public bool DirectoryExists { get; set; } = true;
    }

    internal sealed record GroupAliasEntry(IAbsoluteFilePath Marker, string VariantId, string? SourceVariantId, string SourceExtension, DateTime CreationTimeUtc);

    /// <summary>
    /// Deletes a file and its variants from the repository.
    /// </summary>
    private async ValueTask DeleteAsync(FileId fileId, bool immediate)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var fileLock = await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false);

        var fileDir = GetFileDirectory(_filesDirectory, fileId);
        var deleteMarker = GetDeleteMarker(_cleanupDirectory, fileId, null);
        var indeterminateMarker = GetIndeterminateMarker(_cleanupDirectory, fileId);

        if (immediate)
        {
            var elc = ExceptionListCapture.Default;

            // Initial alias sweep: delete every alias marker before recursively removing the file group directory. If any alias delete fails, abort and write
            // a delete marker so the cleaner retries the whole operation. This keeps the directory contents consistent (no orphaned data files left after a
            // partial sweep) and matches the variant-delete ordering.
            try
            {
                foreach (var marker in fileDir.GetChildFiles("*" + FileRepoPaths.AliasMarkerExtension))
                {
                    if (!marker.TryDelete(out var markerEx))
                        elc.AddException(markerEx);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // File group directory is already gone; nothing to sweep.
            }

            if (elc.HasExceptions)
            {
                await LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", elc.ResultException, Options.MarkerFileLogging).ConfigureAwait(false);
                return;
            }

            if (!fileDir.TryDelete(recursive: true, out var ex) || !indeterminateMarker.TryDelete(out ex) || !deleteMarker.TryDelete(out ex))
                await LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex, Options.MarkerFileLogging).ConfigureAwait(false);

            return;
        }
        else
        {
            const string message = "File has been marked for deletion.";
            await LogToMarkerAsync(deleteMarker, "DELETE", message, Options.MarkerFileLogging).ConfigureAwait(false);
            indeterminateMarker.TryDelete(out _);
        }
    }
}
