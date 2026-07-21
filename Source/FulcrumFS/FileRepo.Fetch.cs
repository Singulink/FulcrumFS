namespace FulcrumFS;

/// <content>
/// Contains the implementations of file fetch functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Gets information about an existing file and all of its variants in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The specified file ID does not exist in the repository.</exception>
    public async ValueTask<RepoFileGroupInfo> GetGroupAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
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

        List<DanglingAliasInfo>? danglingAliases = null;

        if (aliasMarkers is not null)
        {
            foreach (var marker in aliasMarkers)
            {
                if (!RepoFileSystem.TryParseAliasMarker(marker, out string? variantId, out string? sourceVariantId, out string? sourceExtension))
                {
                    // Malformed marker: treat as nonexistent. Fire the corruption event so monitoring/repair tooling sees it, but do not surface it through
                    // VariantFiles or DanglingAliases.
                    await CorruptionDetected.RaiseMalformedAliasAsync(fileId, marker.Name).ConfigureAwait(false);
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
                else if (retiredVariantIds is not null && retiredVariantIds.Contains(sourceVariantId))
                {
                    // Source variant is retired but cleanup has not yet removed this alias. Transient retirement state, not corruption - omit silently and
                    // let the cleaner sweep.
                    continue;
                }
                else if (dataByVariantId is not null && dataByVariantId.TryGetValue(sourceVariantId, out var found))
                {
                    sourceFile = found;
                }
                else
                {
                    // Dangling alias: source data file is missing from the listing. Omit from VariantFiles, surface via DanglingAliases, and notify the
                    // corruption event so monitoring/repair tooling can react.
                    (danglingAliases ??= []).Add(new DanglingAliasInfo(variantId, sourceVariantId, sourceExtension));
                    await CorruptionDetected.RaiseDanglingAliasAsync(fileId, variantId, sourceVariantId).ConfigureAwait(false);
                    continue;
                }

                variants.Add(new RepoFileInfo(fileId, variantId, sourceFile));
            }
        }

        return new RepoFileGroupInfo(new RepoFileInfo(fileId, variantId: null, mainFile), variants, danglingAliases);
    }

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
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async ValueTask<FileStream> OpenAsync(FileId fileId)
    {
        var info = await GetAsync(fileId).ConfigureAwait(false);
        return info.Open();
    }

    /// <summary>
    /// Gets information about an existing file variant in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async ValueTask<RepoFileInfo> GetVariantAsync(FileId fileId, string variantId)
    {
        variantId = VariantId.Normalize(variantId);
        await EnsureInitializedAsync().ConfigureAwait(false);

        // Single enumeration captures both the data file/alias marker and the in-group delete marker state. A retired variant must fail-fast even if its data
        // file still lingers on disk in deferred-delete mode.
        var (file, isRetired, _) = await _fs.FindDataFileAsync(fileId, variantId).ConfigureAwait(false);

        // A retired variant must fail-fast as not-found even if its data file still lingers on disk in deferred-delete mode.
        if (isRetired || file is null)
            throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");

        if (file.Extension is FileRepoPaths.AliasMarkerExtension)
        {
            if (!RepoFileSystem.TryParseAliasMarker(file, out _, out string? srcVid, out string? srcExt))
            {
                // Marker is malformed: treat as nonexistent. Fire the corruption event and surface a plain not-found to the caller.
                await CorruptionDetected.RaiseMalformedAliasAsync(fileId, file.Name).ConfigureAwait(false);
                throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");
            }

            // If the alias's source has been retired, the alias is transient retirement residue (cleanup has not yet swept it). Surface as plain not-found
            // since the variant is conceptually gone - this is not corruption, just retirement in flight. The source check is a cheap single-file probe
            // (vs. a directory glob enumeration), so we use IsVariantRetired directly rather than another FindDataFile call.
            if (srcVid is not null && _fs.IsVariantRetired(fileId, srcVid))
                throw new RepoFileNotFoundException($"File ID '{fileId}' or its variant '{variantId}' was not found.");

            var resolved = _fs.ResolveAlias(fileId, file);

            if (resolved is null)
            {
                await CorruptionDetected.RaiseDanglingAliasAsync(fileId, variantId, srcVid).ConfigureAwait(false);
                throw new DanglingAliasException(fileId, variantId, srcVid, srcExt);
            }

            file = resolved;
        }

        return new RepoFileInfo(fileId, variantId, file);
    }

    /// <summary>
    /// Opens a file stream for an existing file variant in the repository using the recommended sharing options.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">The variant was not found.</exception>
    public async ValueTask<FileStream> OpenVariantAsync(FileId fileId, string variantId)
    {
        var info = await GetVariantAsync(fileId, variantId).ConfigureAwait(false);
        return info.Open();
    }
}
