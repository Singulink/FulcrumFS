namespace FulcrumFS;

/// <content>
/// Group-structure operations that span multiple files within a single file group directory: inventory enumeration and crash-safe subtree rebases.
/// </content>
partial class RepoFileSystem
{
    /// <summary>
    /// Single-pass inventory of a file group directory, classifying every entry into data files, in-group delete markers (retired variants), alias dependents
    /// (with their source and creation time), and rebase markers (further partitioned into resumable plans vs orphans by a post-pass cross-check). Used by
    /// both the delete planner and the cleaner's rebase-resume logic.
    /// </summary>
    public GroupVariantInventory BuildGroupInventory(FileId fileId)
    {
        var inv = new GroupVariantInventory();
        var dir = GetFileDirectory(fileId);

        // Raw rebase markers buffered for the post-pass classification below.
        var rawRebaseMarkers = new List<(IAbsoluteFilePath Marker, string Source, string Chosen)>();

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
                        rawRebaseMarkers.Add((path, src, chosen));
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
            return inv;
        }

        // Post-pass: classify every rebase marker as either resumable (a source extension is derivable, so rollforward can proceed) or orphan (no derivable
        // source extension - source data gone, chosen data gone, no surviving aliases reference the source). Resumable entries carry the pinned chosen
        // survivor, the resolved source extension, and the survivor variants to re-point. Orphans carry only the marker path + parsed source/chosen IDs.
        foreach (var (marker, source, chosen) in rawRebaseMarkers)
        {
            var repoint = new List<string>();
            string? aliasSourceExtension = null;

            foreach (var a in inv.Aliases)
            {
                if (a.SourceVariantId != source)
                    continue;

                aliasSourceExtension ??= a.SourceExtension;

                if (a.VariantId != chosen)
                    repoint.Add(a.VariantId);
            }

            // The source extension is needed to address the data files and markers. It is the source data file's extension while the source persists as
            // residue; once the source is gone it equals the (content-identical) chosen data file's extension, and failing that the survivors' encoded source
            // extension. If none can be derived the marker is an orphan with no real data to fork.
            string? sourceExtension =
                inv.DataFiles.TryGetValue(source, out var srcData) ? srcData.Extension :
                inv.DataFiles.TryGetValue(chosen, out var chosenData) ? chosenData.Extension :
                aliasSourceExtension;

            if (sourceExtension is null)
                inv.OrphanRebaseMarkers.Add(new OrphanRebaseMarker(marker, source, chosen));
            else
                inv.ResumableRebases.Add(new ResumableRebase(marker, new SubtreeRebasePlan(source, sourceExtension, chosen, repoint)));
        }

        return inv;
    }

    /// <summary>
    /// Performs (or idempotently resumes) the rebase of a single subtree whose real source variant is being retired: promotes the chosen survivor to a
    /// standalone data file by copying the source's content into the chosen alias marker and renaming it to a real data file, then re-points the remaining
    /// survivors at the chosen variant. The source data file is never touched here; it is removed later as retired residue. Shared by the foreground path and
    /// the cleaner so both run identical logic. All steps are crash-safe and idempotent. Throws on the first failure encountered; callers are responsible for
    /// leaving the rebase marker and any cleanup hint in place so a subsequent sweep can roll forward.
    /// </summary>
    /// <param name="fileId">The file ID identifying the file group whose subtree is being rebased.</param>
    /// <param name="plan">The subtree rebase plan describing the source variant, chosen survivor and survivors to re-point.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous copy operation.</param>
    /// <remarks>
    /// If <see cref="CorruptionDetected"/> resolves to a non-<see langword="null"/> handler, it is invoked with a human-readable reason immediately before a
    /// <see cref="RepoCorruptedException"/> is thrown for a <see cref="RepoCorruptionKind.RebaseInconsistency"/> condition. <see cref="FileRepo"/> wires this
    /// to its <see cref="FileRepo.CorruptionDetected"/> event; <see cref="FileRepoCleaner"/> leaves it <see langword="null"/>.
    /// </remarks>
    public async Task RebaseSubtreeAsync(FileId fileId, SubtreeRebasePlan plan, CancellationToken cancellationToken = default)
    {
        var fileGroupDir = GetFileDirectory(fileId);
        var rebaseMarker = GetRebaseMarker(fileId, plan.SourceVariantId, plan.ChosenVariantId);
        var sourceData = GetDataFile(fileId, plan.SourceExtension, plan.SourceVariantId);
        var chosenData = GetDataFile(fileId, plan.SourceExtension, plan.ChosenVariantId);
        var chosenAlias = GetAliasMarker(fileGroupDir, plan.ChosenVariantId, plan.SourceVariantId, plan.SourceExtension);

        // 1. Pin the chosen survivor before mutating the directory. A crash-resuming cleaner reads this marker to attribute a half-finished subtree to the
        //    same chosen variant the foreground had selected.
        await LogToMarkerAsync(rebaseMarker, "REBASE", "Pinning chosen survivor variant.").ConfigureAwait(false);

        DebugStepHook?.Invoke(DebugStep.RebaseMarkerWritten);

        // 2. Materialize the chosen survivor: copy the source content INTO the chosen alias marker (a zero-byte file), then rename that single directory
        //    entry to the real data file. Because the alias resolves to the source's data throughout the copy, the chosen variant resolves to real content
        //    at every instant - there is never both a real data file and an alias marker for the chosen variant on disk at the same time.
        if (chosenData.State is not EntryState.Exists)
        {
            if (chosenAlias.State is not EntryState.Exists)
            {
                await ThrowInconsistencyAsync(fileId, plan, "neither the materialized chosen data file nor the chosen alias marker is present.").ConfigureAwait(false);
            }

            if (sourceData.State is not EntryState.Exists)
            {
                await ThrowInconsistencyAsync(fileId, plan, "the source data file is missing while the chosen variant is still an unmaterialized alias.").ConfigureAwait(false);
            }

            // Copy the source content INTO the chosen alias marker (overwriting the zero-byte placeholder), then rename that single directory entry to
            // the real data file. The chosen variant resolves to the source's data via the alias throughout the copy, so it always resolves to real
            // content and there is never both a real data file and an alias marker for the chosen variant on disk at once.
            await sourceData.CopyToAsync(chosenAlias, overwrite: true, cancellationToken).ConfigureAwait(false);

            chosenAlias.MoveTo(chosenData, overwrite: false);
        }

        DebugStepHook?.Invoke(DebugStep.RebaseMaterialized);

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

        DebugStepHook?.Invoke(DebugStep.RebaseRepointed);

        // 4. Rebase complete, drop the pin. A failure here must propagate so the cleanup hint is preserved and a subsequent sweep re-runs the (now-no-op)
        //    rebase to retry the marker delete; otherwise the orphan marker would force every future add/delete on this group through pointless rollforward.
        rebaseMarker.Delete();
    }

    private async Task ThrowInconsistencyAsync(FileId fileId, SubtreeRebasePlan plan, string reason)
    {
        await CorruptionDetected.RaiseRebaseInconsistencyAsync(fileId, plan.SourceVariantId, plan.ChosenVariantId, reason).ConfigureAwait(false);

        throw new RepoCorruptedException(
            $"Rebase of file ID '{fileId}' source variant '{plan.SourceVariantId}' onto '{plan.ChosenVariantId}' cannot proceed: {reason} " +
            $"This indicates repository corruption.");
    }

    /// <summary>
    /// A planned rebase of one subtree: the listed real <paramref name="SourceVariantId"/> is retired and its data (with <paramref name="SourceExtension"/>)
    /// is copied into <paramref name="ChosenVariantId"/> (which is promoted to a standalone data file); the survivors in <paramref name="RepointVariantIds"/>
    /// are re-pointed at the chosen variant. The source data file is left intact for later residue deletion.
    /// </summary>
    public readonly record struct SubtreeRebasePlan(string SourceVariantId, string SourceExtension, string ChosenVariantId, List<string> RepointVariantIds);

    public sealed class GroupVariantInventory
    {
        public Dictionary<string, (IAbsoluteFilePath Path, string Extension)> DataFiles { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Retired { get; } = new(StringComparer.Ordinal);

        public List<GroupAliasEntry> Aliases { get; } = [];

        /// <summary>
        /// Gets the rebase markers whose source extension is derivable from {source data, chosen data, surviving alias source extension}. Each entry carries
        /// the fully-formed <see cref="SubtreeRebasePlan"/> the foreground rollforward / cleaner resume can execute directly.
        /// </summary>
        public List<ResumableRebase> ResumableRebases { get; } = [];

        /// <summary>
        /// Gets the rebase markers with no derivable source extension - no source data, no chosen data, no surviving aliases referencing the source. Residue
        /// from a rebase whose final marker-delete step was lost. Surfaced to callers for <see cref="RepoCorruptionKind.OrphanRebaseMarker"/> event raising
        /// and for cleaner-side physical removal.
        /// </summary>
        public List<OrphanRebaseMarker> OrphanRebaseMarkers { get; } = [];

        public bool DirectoryExists { get; set; } = true;
    }

    public sealed record GroupAliasEntry(IAbsoluteFilePath Marker, string VariantId, string? SourceVariantId, string SourceExtension, DateTime CreationTimeUtc);

    /// <summary>A rebase marker paired with the executable rollforward plan derived from the surrounding inventory.</summary>
    public sealed record ResumableRebase(IAbsoluteFilePath Marker, SubtreeRebasePlan Plan);

    /// <summary>A rebase marker with no derivable source extension - nothing to fork, only the marker file itself to remove.</summary>
    public sealed record OrphanRebaseMarker(IAbsoluteFilePath Marker, string SourceVariantId, string ChosenVariantId);
}
