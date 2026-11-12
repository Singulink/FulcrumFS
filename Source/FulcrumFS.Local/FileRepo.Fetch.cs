namespace FulcrumFS;

/// <content>
/// Contains the implementations of file fetch functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Gets information about an existing file and all of its variants in the repository.
    /// </summary>
    /// <remarks>
    /// A variant alias whose source data file is missing is not listed in <see cref="RepoFileGroupInfo.VariantFiles"/> but in
    /// <see cref="RepoFileGroupInfo.DanglingAliases"/>, and it is reported through <see cref="CorruptionDetected"/>. Before that, the group is listed again
    /// for as long as the alias markers that cannot be resolved change from one listing to the next, so an alias that a retirement of its source has
    /// promoted or re-pointed while the group was being listed is listed with the content it resolves to and is not reported. Only an alias that cannot be
    /// resolved in two listings in a row is reported, whether or not its source is retired.
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The specified file ID does not exist in the repository.</exception>
    public async ValueTask<RepoFileGroupInfo> GetGroupAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        // The names of the alias markers that the previous listing could not resolve, if it had any.
        HashSet<string>? previousUnresolved = null;

        while (true)
        {
            var listing = ListGroup(fileId);

            // A listing is not atomic, and a retirement renames the marker of every alias that survives it (to a data file, or to a marker that names the
            // promoted survivor) before it removes the source's data file. An alias marker whose source data file is not in the same listing is therefore
            // either a name that such a retirement has replaced since, which a new listing shows resolved, or a dangling alias, which a new listing shows
            // unchanged. So the group is listed again until every marker that cannot be resolved was already unresolvable, under the same name, in the
            // listing before: only something that changes the group between every two listings keeps this going, so there is no bound on it. A listing in
            // which every alias resolves, which is every listing of a healthy group that is not being changed, is the result at once.
            bool isSteady = true;

            if (listing.AliasIssues is not null)
            {
                foreach (var issue in listing.AliasIssues)
                {
                    if (issue.Dangling is not null && (previousUnresolved is null || !previousUnresolved.Contains(issue.MarkerName)))
                    {
                        isSteady = false;
                        break;
                    }
                }
            }

            if (!isSteady)
            {
                previousUnresolved = new HashSet<string>(StringComparer.Ordinal);

                foreach (var issue in listing.AliasIssues!)
                {
                    if (issue.Dangling is not null)
                        previousUnresolved.Add(issue.MarkerName);
                }

                DebugStepHook?.Invoke(DebugStep.GroupAliasSourceMissing);
                continue;
            }

            // The result is built from this listing alone, and only now is anything reported, once: a listing that was abandoned raised nothing.
            List<DanglingAliasInfo>? danglingAliases = null;

            if (listing.AliasIssues is not null)
            {
                foreach (var issue in listing.AliasIssues)
                {
                    if (issue.Dangling is null)
                    {
                        // Malformed marker: treat as nonexistent. Fire the corruption event so monitoring/repair tooling sees it, but do not surface it
                        // through VariantFiles or DanglingAliases.
                        await CorruptionDetected.RaiseMalformedAliasAsync(fileId, issue.MarkerName).ConfigureAwait(false);
                    }
                    else
                    {
                        // Dangling alias: omitted from VariantFiles, surfaced via DanglingAliases, and reported through the corruption event so
                        // monitoring/repair tooling can react.
                        (danglingAliases ??= []).Add(issue.Dangling);
                        await CorruptionDetected.RaiseDanglingAliasAsync(fileId, issue.Dangling.VariantId, issue.Dangling.SourceVariantId).ConfigureAwait(false);
                    }
                }
            }

            return new RepoFileGroupInfo(new RepoFileInfo(fileId, variantId: null, listing.MainFile), listing.Variants, danglingAliases);
        }
    }

    /// <summary>
    /// Lists a file group once and resolves its variants from that listing alone. Raises no events: what is wrong with the alias markers in the listing is
    /// returned for the caller to report, or to drop if it lists the group again.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The file group or its main file is not there.</exception>
    private GroupListing ListGroup(FileId fileId)
    {
        var fileDir = _fs.GetFileDirectory(fileId);

        IAbsoluteFilePath? mainFile = null;
        var variants = new List<RepoFileInfo>();
        List<IAbsoluteFilePath>? aliasMarkers = null;
        Dictionary<string, IAbsoluteFilePath>? dataByVariantId = null;
        HashSet<string>? retiredVariantIds = null;

        try
        {
            foreach (var file in fileDir.GetChildFiles())
            {
                if (file.Extension is FileRepoPaths.AliasMarkerExtension)
                {
                    (aliasMarkers ??= []).Add(file);
                    continue;
                }

                if (file.Extension is FileRepoPaths.DeleteMarkerExtension)
                {
                    // In-group delete marker: {variantId}.del. The variant is retired; omit it from the listing even if its data file still exists.
                    string deleteMarkerName = file.NameWithoutExtension;
                    if (VariantId.IsValidAndNormalized(deleteMarkerName))
                        (retiredVariantIds ??= new(StringComparer.Ordinal)).Add(deleteMarkerName);
                    continue;
                }

                if (file.Extension is FileRepoPaths.RebaseMarkerExtension)
                    continue;

                if (!FileExtension.IsValidAndNormalized(file.Extension))
                    continue;

                string name = file.NameWithoutExtension;

                if (name == FileRepoPaths.MainFileName)
                {
                    mainFile = file;
                }
                else if (VariantId.IsValidAndNormalized(name))
                {
                    variants.Add(new RepoFileInfo(fileId, name, file));
                    (dataByVariantId ??= new(StringComparer.Ordinal))[name] = file;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
        }

        if (mainFile is null)
            throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");

        // Filter out retired data-file variants from the listing.
        if (retiredVariantIds is not null)
            variants.RemoveAll(v => v.VariantId is not null && retiredVariantIds.Contains(v.VariantId));

        List<AliasIssue>? aliasIssues = null;

        if (aliasMarkers is not null)
        {
            foreach (var marker in aliasMarkers)
            {
                if (!RepoFileSystem.TryParseAliasMarker(marker, out string? variantId, out string? sourceVariantId, out string? sourceExtension))
                {
                    // Malformed marker: not a variant, and nothing that a new listing would show differently.
                    (aliasIssues ??= []).Add(new AliasIssue(marker.Name, Dangling: null));
                    continue;
                }

                // Skip aliases whose own variant ID has been retired.
                if (retiredVariantIds is not null && retiredVariantIds.Contains(variantId))
                    continue;

                IAbsoluteFilePath? sourceFile;

                if (sourceVariantId is null)
                {
                    sourceFile = mainFile;
                }
                else if (dataByVariantId is not null && dataByVariantId.TryGetValue(sourceVariantId, out var found))
                {
                    // The source's data file is in the listing, which is all an alias needs, whether or not the source has been retired (only the variants
                    // list had the retired data files removed above, not this lookup). An alias that is not retired itself but whose source is, is a survivor
                    // of the source's retirement that has not been promoted or re-pointed yet: the retirement commits the source's delete marker first, and
                    // removes the source's data file only once no survivor aliases it. Until then the survivor is a live variant whose bytes are the
                    // source's, also after a crash at that point, so it is listed. The data file comes from the same listing as the alias marker.
                    sourceFile = found;
                }
                else
                {
                    // The source's data file is not in the listing, whether or not the source has been retired: a retired source keeps its data file for as
                    // long as an alias that is not retired names it. The caller decides whether this is a dangling alias or a name that has been replaced.
                    (aliasIssues ??= []).Add(new AliasIssue(marker.Name, new DanglingAliasInfo(variantId, sourceVariantId, sourceExtension)));
                    continue;
                }

                variants.Add(new RepoFileInfo(fileId, variantId, sourceFile));
            }
        }

        return new GroupListing(mainFile, variants, aliasIssues);
    }

    /// <summary>
    /// One listing of a file group: its main file, the variants that the listing resolves, and what is wrong with its other alias markers, in the order
    /// they were listed.
    /// </summary>
    private sealed record GroupListing(IAbsoluteFilePath MainFile, List<RepoFileInfo> Variants, List<AliasIssue>? AliasIssues);

    /// <summary>
    /// An alias marker of a listing that is not a variant of it: a marker that could not be resolved because its source data file was not in the listing
    /// (<paramref name="Dangling"/> describes it), or a malformed one (<paramref name="Dangling"/> is <see langword="null"/>).
    /// </summary>
    private readonly record struct AliasIssue(string MarkerName, DanglingAliasInfo? Dangling);

    /// <summary>
    /// Gets information about an existing file in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async ValueTask<RepoFileInfo> GetAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var (file, _, _) = await _fs.FindDataFileAsync(fileId, variantId: null).ConfigureAwait(false);
        if (file is null)
            throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
        return new RepoFileInfo(fileId, variantId: null, file);
    }

    /// <summary>
    /// Opens a file stream for an existing file in the repository using the recommended sharing options.
    /// </summary>
    /// <param name="fileId">The ID of the file.</param>
    /// <param name="accessPattern">How the caller is going to read the stream. The file is opened with the matching caching hint, as
    /// <see cref="RepoFileInfo.Open(FileAccessPattern)"/> describes.</param>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async ValueTask<FileStream> OpenAsync(FileId fileId, FileAccessPattern accessPattern)
    {
        var info = await GetAsync(fileId).ConfigureAwait(false);
        return info.Open(accessPattern);
    }

    /// <summary>
    /// Gets information about an existing file variant in the repository.
    /// </summary>
    /// <remarks>
    /// A variant stored as an alias resolves to the data file of its source. If that file is missing, the variant is read again before anything is
    /// reported, for as long as each read finds a different alias marker: a retirement of the source that promoted or re-pointed the alias in the meantime
    /// is followed to the file the variant resolves to now. Only an alias that is found unchanged with its source data file still missing is reported, as
    /// a <see cref="DanglingAliasException"/> and through <see cref="CorruptionDetected"/>, whether or not its source is retired.
    /// </remarks>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    /// <exception cref="DanglingAliasException">The variant is an alias whose source data file is missing (a subtype of
    /// <see cref="RepoFileNotFoundException"/>).</exception>
    public async ValueTask<RepoFileInfo> GetVariantAsync(FileId fileId, string variantId)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        var slot = await ReadVariantSlotAsync(fileId, variantId).ConfigureAwait(false);

        switch (slot.Kind)
        {
            case VariantSlotKind.Data:
            case VariantSlotKind.Alias:
                return new RepoFileInfo(fileId, variantId, slot.DataFile!);

            case VariantSlotKind.MalformedAlias:
                // Marker is malformed: treat as nonexistent. Fire the corruption event and surface a plain not-found to the caller.
                await CorruptionDetected.RaiseMalformedAliasAsync(fileId, slot.File!.Name).ConfigureAwait(false);
                break;

            case VariantSlotKind.DanglingAlias:
                await CorruptionDetected.RaiseDanglingAliasAsync(fileId, variantId, slot.SourceVariantId).ConfigureAwait(false);
                throw new DanglingAliasException(fileId, variantId, slot.SourceVariantId, slot.SourceExtension!);
        }

        // The slot is empty, or the variant is retired: a retired variant must fail-fast as not-found even if its data file still lingers on disk in
        // deferred-delete mode.
        throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
    }

    /// <summary>
    /// Reads a variant's slot (its data file or alias marker, and its in-group delete marker) until what it holds is steady, and says what that is. An
    /// alias marker whose source data file is missing is not steady until the slot has been read again and still holds the same marker with its source
    /// still missing; anything else is steady at once, so a slot that is not such an alias costs one read, and an alias that resolves one read and one
    /// look at its source's data file. Shared by the fetch of a variant and the add path, which must agree on what a slot holds. Raises no events and takes
    /// no locks: reporting a malformed or dangling alias is left to the caller.
    /// </summary>
    /// <param name="fileId">The ID of the file.</param>
    /// <param name="variantId">The normalized ID of the variant.</param>
    /// <param name="unresolvedMarkerName">The name of an alias marker that the caller has read from the slot itself and found without its source data file,
    /// if it has: the first read here is then the repeat of the caller's, so that the same marker with its source still missing is dangling at once.</param>
    private async ValueTask<VariantSlot> ReadVariantSlotAsync(FileId fileId, string variantId, string? unresolvedMarkerName = null)
    {
        if (unresolvedMarkerName is not null)
            DebugStepHook?.Invoke(DebugStep.VariantAliasSourceMissing);

        while (true)
        {
            // Single enumeration captures both the data file/alias marker and the in-group delete marker state.
            var (file, isRetired, _) = await _fs.FindDataFileAsync(fileId, variantId).ConfigureAwait(false);

            if (isRetired)
                return new VariantSlot(VariantSlotKind.Retired, file);

            if (file is null)
                return new VariantSlot(VariantSlotKind.Missing);

            if (file.Extension is not FileRepoPaths.AliasMarkerExtension)
                return new VariantSlot(VariantSlotKind.Data, file, file);

            if (!RepoFileSystem.TryParseAliasMarker(file, out _, out string? srcVid, out string? srcExt))
                return new VariantSlot(VariantSlotKind.MalformedAlias, file);

            DebugStepHook?.Invoke(DebugStep.VariantAliasMarkerRead);

            // An alias resolves to its source's data file for as long as that file exists, whether or not the source has been retired. This alias is not
            // retired itself (that was returned above), so if its source is, it is a survivor of the source's retirement that has not been promoted or
            // re-pointed yet: the retirement commits the source's delete marker first, and removes the source's data file only once no survivor aliases it.
            // Until then the survivor is a live variant whose bytes are the source's, also after a crash at that point, so it must stay readable.
            var resolved = _fs.ResolveAlias(fileId, file);

            if (resolved is not null)
                return new VariantSlot(VariantSlotKind.Alias, file, resolved, srcVid, srcExt);

            // The source's data file is missing. Because a retirement renames the marker of every alias that survives it (to a data file, or to a marker
            // that names the promoted survivor) before it removes the source's data file, a marker read before those renames is stale by the time the
            // source is found missing, and reading the slot again shows what replaced it. The foreground does that under the variant locks, but the
            // cleaner takes none, so this holds for a caller that has the variant's lock as it does for one that has not. Only when the slot still holds
            // the same marker as the read before, with its source still missing, has nothing replaced it: that is a dangling alias, whether or not its
            // source is retired (a retired source keeps its data file for as long as an alias that is not retired names it). It is dangling only then and
            // not on the first read, so that the stale marker of a retirement is never reported. Every repeat means that the slot changed between two
            // reads, so there is no bound on them: only something that changes the slot without end keeps this going.
            if (file.Name == unresolvedMarkerName)
                return new VariantSlot(VariantSlotKind.DanglingAlias, file, DataFile: null, srcVid, srcExt);

            unresolvedMarkerName = file.Name;
            DebugStepHook?.Invoke(DebugStep.VariantAliasSourceMissing);
        }
    }

    /// <summary>
    /// What a variant's slot holds once <see cref="ReadVariantSlotAsync"/> has found it steady.
    /// </summary>
    private enum VariantSlotKind
    {
        /// <summary>Nothing: the variant does not exist.</summary>
        Missing,

        /// <summary>An in-group delete marker: the variant is retired, whatever else is still in the slot.</summary>
        Retired,

        /// <summary>A data file.</summary>
        Data,

        /// <summary>An alias marker whose source data file exists.</summary>
        Alias,

        /// <summary>An alias marker whose name cannot be parsed.</summary>
        MalformedAlias,

        /// <summary>An alias marker whose source data file is missing, and was when the slot held the same marker the read before.</summary>
        DanglingAlias,
    }

    /// <summary>
    /// A variant's slot as <see cref="ReadVariantSlotAsync"/> found it.
    /// </summary>
    /// <param name="Kind">What the slot holds.</param>
    /// <param name="File">The data file or alias marker in the slot, or <see langword="null"/> if it holds neither.</param>
    /// <param name="DataFile">The data file the variant resolves to: its own, or its source's for an alias that resolves. <see langword="null"/> for every
    /// other kind.</param>
    /// <param name="SourceVariantId">For an alias that resolves or dangles, the variant ID of its source, or <see langword="null"/> for the main file.</param>
    /// <param name="SourceExtension">For an alias that resolves or dangles, the extension of its source data file.</param>
    private readonly record struct VariantSlot(
        VariantSlotKind Kind,
        IAbsoluteFilePath? File = null,
        IAbsoluteFilePath? DataFile = null,
        string? SourceVariantId = null,
        string? SourceExtension = null);

    /// <summary>
    /// Opens a file stream for an existing file variant in the repository using the recommended sharing options.
    /// </summary>
    /// <param name="fileId">The ID of the main file.</param>
    /// <param name="variantId">The ID of the variant.</param>
    /// <param name="accessPattern">How the caller is going to read the stream. The file is opened with the matching caching hint, as
    /// <see cref="RepoFileInfo.Open(FileAccessPattern)"/> describes.</param>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async ValueTask<FileStream> OpenVariantAsync(FileId fileId, string variantId, FileAccessPattern accessPattern)
    {
        var info = await GetVariantAsync(fileId, variantId).ConfigureAwait(false);
        return info.Open(accessPattern);
    }
}
