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
                    return; // Nothing to do - every listed variant is already retired or absent.

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
    /// on the same survivor. Orphaned markers (no derivable source extension, hence no real data to fork) are surfaced via the corruption event and physically
    /// removed inline; they do not block add/delete.
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

            var inv = _fs.BuildGroupInventory(fileId);

            // Surface any orphan rebase markers and physically remove them inline. Orphans cannot be rolled forward (no data to fork) and the marker file is
            // pure residue from a rebase whose final delete step was lost - deleting it here keeps subsequent inventory passes from re-observing it. The event
            // is raised purely for diagnostic visibility.
            foreach (var orphan in inv.OrphanRebaseMarkers)
            {
                await CorruptionDetected.RaiseOrphanRebaseMarkerAsync(fileId, orphan.SourceVariantId, orphan.ChosenVariantId).ConfigureAwait(false);
                orphan.Marker.TryDelete(out _);
            }

            ComputeResumeRebases(inv, out var candidateLocks);

            if (candidateLocks.Count is 0)
                return; // Nothing (left) to roll forward.

            var locks = new List<KeyLock<(FileId, string?)>>(candidateLocks.Count);

            try
            {
                foreach (string vid in candidateLocks)
                    locks.Add(await _fileSync.LockAsync((fileId, vid), Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false));

                var invLocked = _fs.BuildGroupInventory(fileId);
                var resumeRebases = ComputeResumeRebases(invLocked, out var requiredLocks);

                if (resumeRebases.Count is 0)
                    return; // Completed concurrently between the lock-free pass and lock acquisition.

                if (!requiredLocks.IsSubsetOf(candidateLocks))
                    continue; // A concurrent add widened a survivor set; retry with the wider lock set.

                foreach (var resume in resumeRebases)
                {
                    try
                    {
                        await _fs.RebaseSubtreeAsync(fileId, resume, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new FileProcessingException($"Failed to roll a pending rebase of file ID '{fileId}' forward.", ex);
                    }
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
    /// Collects the rollforward plans for every <see cref="RepoFileSystem.ResumableRebase"/> in a group inventory and the full set of variant locks executing
    /// them requires (the sources, chosen survivors, and survivors to be re-pointed). Orphan rebase markers are not included here - they carry no
    /// participant locks and are left for the cleaner to remove.
    /// </summary>
    /// <param name="inv">The group inventory whose resumable rebases are being collected.</param>
    /// <param name="participantLocks">Receives the variant IDs whose locks completing the returned rebases requires.</param>
    private static List<RepoFileSystem.SubtreeRebasePlan> ComputeResumeRebases(RepoFileSystem.GroupVariantInventory inv, out SortedSet<string> participantLocks)
    {
        participantLocks = new SortedSet<string>(StringComparer.Ordinal);
        var resumeRebases = new List<RepoFileSystem.SubtreeRebasePlan>(inv.ResumableRebases.Count);

        foreach (var resumable in inv.ResumableRebases)
        {
            var plan = resumable.Plan;

            participantLocks.Add(plan.SourceVariantId);
            participantLocks.Add(plan.ChosenVariantId);

            foreach (string r in plan.RepointVariantIds)
                participantLocks.Add(r);

            resumeRebases.Add(plan);
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

        var inv = _fs.BuildGroupInventory(fileId);

        // Whole-system invariant: no add/delete plans while a (rollforward-able) rebase marker exists. If one is present the caller rolls it forward and
        // re-plans against a settled, single-head subtree. Degenerate/orphaned markers (no real data to fork) are ignored here so they neither block nor loop.
        hasPendingRebases = ComputeResumeRebases(inv, out _).Count > 0;

        var retireListed = new List<string>();
        var aliasMarkersToDelete = new List<IAbsoluteFilePath>();
        var realDataToDelete = new List<IAbsoluteFilePath>();
        var rebases = new List<RepoFileSystem.SubtreeRebasePlan>();

        // Iterate in sorted order.
        foreach (string v in listed)
        {
            if (inv.Retired.Contains(v))
            {
                // Already retired - idempotent skip. Any unfinished physical teardown for it is owned by the cleaner.
                continue;
            }

            if (inv.DataFiles.TryGetValue(v, out var data))
            {
                // Real data file. Split its alias dependents into survivors (not listed → preserved via rebase) and listed dependents (retired separately).
                var survivors = new List<RepoFileSystem.GroupAliasEntry>();

                foreach (var a in inv.Aliases)
                {
                    if (a.SourceVariantId == v && !listed.Contains(a.VariantId))
                        survivors.Add(a);
                }

                retireListed.Add(v);

                // The source data file is always retired residue to be physically deleted later. Materialization copies (never moves) the source content into
                // the promoted survivor, so the source file must persist intact through materialization and is removed only in the deletion phase - this keeps
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

                rebases.Add(new RepoFileSystem.SubtreeRebasePlan(v, data.Extension, chosen, repoint));

                requiredLocks.Add(chosen);

                foreach (string r in repoint)
                    requiredLocks.Add(r);
            }
            else
            {
                // Not a real data file - it may be an alias marker for this variant ID.
                foreach (var a in inv.Aliases)
                {
                    if (a.VariantId == v)
                    {
                        retireListed.Add(v);
                        aliasMarkersToDelete.Add(a.Marker);
                        break;
                    }
                }

                // Otherwise the variant is absent (never added, or fully cleaned up) - idempotent skip.
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
        // Physical removal of retired residue is immediate in both Immediate and DeferredFilesOnly modes; only DeferredUntilClean defers it to the cleaner.
        // Materialization (promoting survivors) is ALWAYS performed in the foreground regardless of mode - the deferral only affects the follow-up deletion of
        // the retired source data files, alias markers and delete markers.
        bool immediate = Options.DeleteMode is DeleteMode.Immediate or DeleteMode.DeferredFilesOnly;

        // --- Pre-commit scaffolding: cleanup-dir hints. Inert on their own - a crash here is a no-op (a hint without a matching delete marker is swept away). ---
        foreach (string v in plan.RetireListed)
        {
            var hint = _fs.GetDeleteMarker(fileId, v);
            await _fs.LogToMarkerAsync(hint, "DELETE", "File variant has been marked for deletion.").ConfigureAwait(false);
        }

        DebugStepHook?.Invoke(DebugStep.DeleteHintsWritten);

        // --- Commit: in-group delete markers. After this, every listed variant is retired from the fetch perspective. A crash at/after here rolls forward. ---
        foreach (string v in plan.RetireListed)
        {
            var deleteMarker = _fs.GetInGroupDeleteMarker(fileId, v);
            await _fs.LogToMarkerAsync(deleteMarker, "DELETE", "File variant has been retired.").ConfigureAwait(false);
        }

        DebugStepHook?.Invoke(DebugStep.DeleteMarkersWritten);

        var elc = new ExceptionListCapture(ex => ex is IOException);

        // --- Materialize all rebases in the foreground (BOTH modes). Each chosen survivor is promoted by copying the retired source's content into the chosen
        //     alias marker and renaming it to a real data file; the source data file is left intact so every variant - retired or surviving - continues to
        //     resolve to real data in any backup snapshot taken mid-operation. The source residue is removed later (immediately below, or by the cleaner). ---
        foreach (var rebase in plan.Rebases)
        {
            if (!await elc.TryRunAsync(_fs.RebaseSubtreeAsync(fileId, rebase)).ConfigureAwait(false))
                break;
        }

        if (elc.HasExceptions)
        {
            // Materialization failed mid-way. The committed delete markers, hints and any rebase markers remain for the cleaner to roll forward. Record diagnostics.
            foreach (string v in plan.RetireListed)
            {
                var hint = _fs.GetDeleteMarker(fileId, v);
                await _fs.LogToOptionalMarkerAsync(hint, "DELETE ATTEMPT FAILED", elc.ResultException).ConfigureAwait(false);
            }

            return;
        }

        if (!immediate)
            return; // Deferred mode: the cleaner physically removes the retired residue (source data files, alias markers, delete markers) after the delete delay.

        DebugStepHook?.Invoke(DebugStep.DeleteResidueAboutToDelete);

        await PhysicallyDeleteRetiredResidueAsync(fileId, plan).ConfigureAwait(false);
    }

    /// <summary>
    /// Immediate-mode follow-up to <see cref="ExecuteDeletePlanAsync"/>: physically removes the retired residue once materialization has completed: the listed
    /// alias markers, the retired real source data files, and finally the delete markers and cleanup hints. Mirrors the deferred-mode teardown performed by the
    /// cleaner so both paths converge on identical on-disk state.
    /// </summary>
    private async Task PhysicallyDeleteRetiredResidueAsync(FileId fileId, VariantDeletePlan plan)
    {
        var elc = new ExceptionListCapture(ex => ex is IOException);

        // Delete the retired alias markers (listed dependents). Survivor aliases are never in this list - they were re-pointed during materialization.
        foreach (var marker in plan.AliasMarkersToDelete)
            elc.TryRun(() => marker.Delete());

        // Delete the retired real source data files. Materialization has already copied the content of any rebase source into its promoted survivor, so the
        // source data is now pure residue.
        foreach (var dataFile in plan.RealDataToDelete)
            elc.TryRun(() => dataFile.Delete());

        if (elc.HasExceptions)
        {
            // Leave the delete markers and hints in place for the cleaner to roll forward. Record the failure on each hint for diagnostics.
            foreach (string v in plan.RetireListed)
            {
                var hint = _fs.GetDeleteMarker(fileId, v);
                await _fs.LogToOptionalMarkerAsync(hint, "DELETE ATTEMPT FAILED", elc.ResultException).ConfigureAwait(false);
            }

            return;
        }

        // --- Teardown (data files now gone): delete markers first (a delete marker must outlive its data file), then cleanup hints last. ---
        foreach (string v in plan.RetireListed)
        {
            var deleteMarker = _fs.GetInGroupDeleteMarker(fileId, v);

            if (!deleteMarker.TryDelete(out var deleteMarkerEx))
            {
                var hint = _fs.GetDeleteMarker(fileId, v);
                await _fs.LogToOptionalMarkerAsync(hint, "DELETE MARKER DELETE FAILED", deleteMarkerEx).ConfigureAwait(false);
            }
        }

        foreach (string v in plan.RetireListed)
            _fs.GetDeleteMarker(fileId, v).TryDelete(out _);
    }

    private sealed class VariantDeletePlan
    {
        public required List<string> RetireListed { get; init; }

        public required List<IAbsoluteFilePath> AliasMarkersToDelete { get; init; }

        public required List<IAbsoluteFilePath> RealDataToDelete { get; init; }

        public required List<RepoFileSystem.SubtreeRebasePlan> Rebases { get; init; }
    }

    /// <summary>
    /// Deletes a file and its variants from the repository.
    /// </summary>
    private async ValueTask DeleteAsync(FileId fileId, bool immediate)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var fileLock = await _fileSync.LockAsync((fileId, null)).ConfigureAwait(false);

        var fileDir = _fs.GetFileDirectory(fileId);
        var deleteMarker = _fs.GetDeleteMarker(fileId, null);
        var indeterminateMarker = _fs.GetIndeterminateMarker(fileId);

        if (immediate)
        {
            var elc = new ExceptionListCapture(ex => ex is IOException);

            // Initial alias sweep: delete every alias marker before recursively removing the file group directory. If any alias delete fails, abort and write
            // a delete marker so the cleaner retries the whole operation. This keeps the directory contents consistent (no orphaned data files left after a
            // partial sweep) and matches the variant-delete ordering.
            try
            {
                foreach (var marker in fileDir.GetChildFiles("*" + FileRepoPaths.AliasMarkerExtension))
                    elc.TryRun(() => marker.Delete());
            }
            catch (DirectoryNotFoundException)
            {
                // File group directory is already gone; nothing to sweep.
            }

            if (elc.HasExceptions)
            {
                await _fs.LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", elc.ResultException).ConfigureAwait(false);
                return;
            }

            if (!fileDir.TryDelete(recursive: true, out var ex) || !indeterminateMarker.TryDelete(out ex) || !deleteMarker.TryDelete(out ex))
                await _fs.LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex).ConfigureAwait(false);

            return;
        }
        else
        {
            const string message = "File has been marked for deletion.";
            await _fs.LogToMarkerAsync(deleteMarker, "DELETE", message).ConfigureAwait(false);
            indeterminateMarker.TryDelete(out _);
        }
    }
}
