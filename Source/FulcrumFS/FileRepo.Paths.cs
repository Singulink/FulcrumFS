using System.Diagnostics;

namespace FulcrumFS;

/// <content>
/// Contains path-derivation helpers that locate data files, file directories, and marker files within the file repository.
/// </content>
partial class FileRepo
{
    internal static IAbsoluteFilePath GetDeleteMarker(IAbsoluteDirectoryPath cleanupDirectory, FileId fileId, string? variant)
    {
        string name = variant is null ? fileId.ToString() : GetFileIdAndVariantString(fileId, variant);
        return cleanupDirectory.CombineFile(name + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
    }

    /// <summary>
    /// Builds the absolute path for the in-group authoritative delete marker for a variant: <c>files/{shard}/{id}/{variantId}.del</c>.
    /// The delete marker lives in the file group directory (not the cleanup directory) and is the fail-fast signal for fetch operations that a variant has been
    /// retired, even while the data file lingers on disk before the cleaner physically removes it.
    /// </summary>
    internal static IAbsoluteFilePath GetInGroupDeleteMarker(IAbsoluteDirectoryPath filesDirectory, FileId fileId, string variantId)
    {
        Debug.Assert(VariantId.IsValidAndNormalized(variantId), "variantId must be normalized.");
        return GetFileDirectory(filesDirectory, fileId).CombineFile(variantId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
    }

    /// <summary>
    /// Builds the absolute path for a rebase marker: <c>files/{shard}/{id}/{sourceVariantId}.{chosenVariantId}.rebase</c>. The marker
    /// records the in-progress promotion of an alias dependent (<paramref name="chosenVariantId"/>) to a standalone data file while
    /// <paramref name="sourceVariantId"/> is being retired with surviving (unlisted) dependents.
    /// </summary>
    internal static IAbsoluteFilePath GetRebaseMarker(IAbsoluteDirectoryPath filesDirectory, FileId fileId, string sourceVariantId, string chosenVariantId)
    {
        Debug.Assert(VariantId.IsValidAndNormalized(sourceVariantId), "sourceVariantId must be normalized.");
        Debug.Assert(VariantId.IsValidAndNormalized(chosenVariantId), "chosenVariantId must be normalized.");
        string fileName = $"{sourceVariantId}.{chosenVariantId}{FileRepoPaths.RebaseMarkerExtension}";
        return GetFileDirectory(filesDirectory, fileId).CombineFile(fileName, PathOptions.None);
    }

    /// <summary>
    /// Attempts to parse a rebase marker filename of the form <c>{sourceVariantId}.{chosenVariantId}.rebase</c>.
    /// </summary>
    internal static bool TryParseRebaseMarker(
        IAbsoluteFilePath markerFile,
        [NotNullWhen(true)] out string? sourceVariantId,
        [NotNullWhen(true)] out string? chosenVariantId)
    {
        sourceVariantId = null;
        chosenVariantId = null;

        if (markerFile.Extension != FileRepoPaths.RebaseMarkerExtension)
            return false;

        var name = markerFile.NameWithoutExtension.AsSpan();
        int dot = name.IndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return false;

        // Reject names with more than one dot in the parsed portion (filenames are exactly two dot-separated segments).
        var sourceSpan = name[..dot];
        var chosenSpan = name[(dot + 1)..];

        if (chosenSpan.IndexOf('.') >= 0)
            return false;

        if (!VariantId.IsValidAndNormalized(sourceSpan) || !VariantId.IsValidAndNormalized(chosenSpan))
            return false;

        sourceVariantId = sourceSpan.ToString();
        chosenVariantId = chosenSpan.ToString();
        return true;
    }

    internal static IAbsoluteFilePath GetIndeterminateMarker(IAbsoluteDirectoryPath cleanupDirectory, FileId fileId)
    {
        return cleanupDirectory.CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);
    }

    internal static IAbsoluteFilePath GetDataFile(IAbsoluteDirectoryPath filesDirectory, FileId fileId, string extension, string? variantId = null)
    {
        string fileNamePart = variantId ?? FileRepoPaths.MainFileName;
        string fullFileName = fileNamePart + extension;
        return GetFileDirectory(filesDirectory, fileId).CombineFile(fullFileName, PathOptions.None);
    }

    /// <inheritdoc cref="FindDataFile(IAbsoluteDirectoryPath, FileId, string?, out bool, out string?)"/>
    internal static IAbsoluteFilePath? FindDataFile(IAbsoluteDirectoryPath filesDirectory, FileId fileId, string? variantId, out bool isRetired) =>
        FindDataFile(filesDirectory, fileId, variantId, out isRetired, out _);

    /// <summary>
    /// Returns the data file (or alias marker) for the specified variant (or main file when <paramref name="variantId"/> is <see langword="null"/>), or
    /// <see langword="null"/> if no data file is present in the file group directory. Sets <paramref name="isRetired"/> to <see langword="true"/> when an
    /// in-group delete marker (<c>{variantId}.del</c>) is present; note that the data file and the delete marker can co-exist in deferred-delete mode, so a
    /// non-<see langword="null"/> return paired with <paramref name="isRetired"/> = <see langword="true"/> means the variant has been retired but its
    /// underlying data file has not yet been physically removed. Callers that care about retirement semantics must check <paramref name="isRetired"/>.
    /// </summary>
    /// <param name="filesDirectory">The repository files directory.</param>
    /// <param name="fileId">The file ID identifying the file group.</param>
    /// <param name="variantId">The variant ID to locate, or <see langword="null"/> for the main file.</param>
    /// <param name="isRetired">Set to <see langword="true"/> when an in-group delete marker is present for the variant.</param>
    /// <param name="rebaseChosenVariantId">
    /// When a rebase marker (<c>{variantId}.{chosen}.rebase</c>) naming this variant as the rebase <em>source</em> is present, set to the chosen
    /// survivor's variant ID (the variant being promoted to replace this source); otherwise <see langword="null"/>. Because the <c>{variantId}.*</c>
    /// glob already enumerates such markers, this is captured at no additional I/O cost. A non-<see langword="null"/> value means this variant is the source of
    /// an in-flight (possibly crash-interrupted) rebase and callers about to bind to or delete it should roll that rebase forward first.
    /// </param>
    /// <remarks>
    /// The main file (<paramref name="variantId"/> = <see langword="null"/>) does not participate in the in-group delete marker scheme, so
    /// <paramref name="isRetired"/> will always be <see langword="false"/> in that case.
    /// </remarks>
    internal static IAbsoluteFilePath? FindDataFile(
        IAbsoluteDirectoryPath filesDirectory, FileId fileId, string? variantId, out bool isRetired, out string? rebaseChosenVariantId)
    {
        string fileNamePart = variantId ?? FileRepoPaths.MainFileName;
        isRetired = false;
        rebaseChosenVariantId = null;
        IAbsoluteFilePath? dataFile = null;

        try
        {
            // Single enumeration: the {name}.* glob already matches the data file/alias marker, the in-group delete marker ({variantId}.del) AND any rebase marker
            // naming this variant as the source ({variantId}.{chosen}.rebase), so all three pieces of state are captured in one syscall.
            foreach (var f in GetFileDirectory(filesDirectory, fileId).GetChildFiles(fileNamePart + ".*"))
            {
                if (f.Extension is FileRepoPaths.DeleteMarkerExtension)
                {
                    isRetired = true;
                }
                else if (f.Extension is FileRepoPaths.RebaseMarkerExtension)
                {
                    // A rebase marker ({source}.{chosen}.rebase) matches the "{source}.*" glob but is not a data file. Capture the chosen survivor so callers
                    // can roll the rebase forward, but never treat the marker as a data file (which would trip the "multiple data files" corruption guard).
                    if (TryParseRebaseMarker(f, out string? markerSource, out string? markerChosen) && string.Equals(markerSource, fileNamePart, StringComparison.Ordinal))
                        rebaseChosenVariantId = markerChosen;
                }
                else if (dataFile is null)
                {
                    dataFile = f;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Multiple data files found for '{fileNamePart}' in file group of file ID '{fileId}'. This indicates repository corruption.");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        return dataFile;
    }

    /// <summary>
    /// Returns <see langword="true"/> if an in-group delete marker (<c>{variantId}.del</c>) exists for the variant. A delete marker marks the variant as
    /// permanently retired. Prefer the <c>isRetired</c> out parameter on
    /// <see cref="FindDataFile(IAbsoluteDirectoryPath, FileId, string?, out bool)"/> when also retrieving the data file; this helper is for cases where only
    /// the delete marker state is needed (e.g. checking whether an alias's source variant has been retired) and a single-file existence probe is cheaper than a
    /// directory glob enumeration.
    /// </summary>
    internal static bool IsVariantRetired(IAbsoluteDirectoryPath filesDirectory, FileId fileId, string variantId)
    {
        Debug.Assert(VariantId.IsValidAndNormalized(variantId), "variantId must be normalized.");
        return GetInGroupDeleteMarker(filesDirectory, fileId, variantId).State is EntryState.Exists;
    }

    /// <summary>
    /// Builds the absolute path for an alias marker file. <paramref name="sourceVariantId"/> may be <see langword="null"/> to indicate the main file
    /// (encoded as the <see cref="FileRepoPaths.MainFileName"/> sentinel). <paramref name="sourceExtension"/> includes its leading dot.
    /// </summary>
    internal static IAbsoluteFilePath GetAliasMarker(IAbsoluteDirectoryPath fileGroupDir, string variantId, string? sourceVariantId, string sourceExtension)
    {
        Debug.Assert(VariantId.IsValidAndNormalized(variantId), "variantId must be normalized.");
        Debug.Assert(sourceVariantId is null || VariantId.IsValidAndNormalized(sourceVariantId), "sourceVariantId must be null or normalized.");
        Debug.Assert(sourceExtension.Length is 0 || (sourceExtension[0] is '.' && FileExtension.IsValidAndNormalized(sourceExtension)), "sourceExtension must be empty or a normalized extension with a leading dot.");

        string source = sourceVariantId ?? FileRepoPaths.MainFileName;
        string extPart = sourceExtension.Length is 0 ? string.Empty : sourceExtension[1..];
        string fileName = $"{variantId}.{source}.{extPart}{FileRepoPaths.AliasMarkerExtension}";
        return fileGroupDir.CombineFile(fileName, PathOptions.None);
    }

    /// <summary>
    /// Attempts to parse an alias marker filename of the form <c>{variantId}.{sourceVariantId}.{sourceExt}.alias</c>. The
    /// <paramref name="sourceVariantId"/> output is <see langword="null"/> when the source is the main file (sentinel <see cref="FileRepoPaths.MainFileName"/>);
    /// otherwise it is a normalized variant ID. <paramref name="sourceExtension"/> is returned with a leading dot (or empty if the source has no extension).
    /// </summary>
    internal static bool TryParseAliasMarker(
        IAbsoluteFilePath markerFile,
        [NotNullWhen(true)] out string? variantId,
        out string? sourceVariantId,
        [NotNullWhen(true)] out string? sourceExtension)
    {
        variantId = null;
        sourceVariantId = null;
        sourceExtension = null;

        if (markerFile.Extension != FileRepoPaths.AliasMarkerExtension)
            return false;

        var name = markerFile.NameWithoutExtension.AsSpan();
        int firstDot = name.IndexOf('.');
        if (firstDot <= 0)
            return false;

        int secondDot = name[(firstDot + 1)..].IndexOf('.');
        if (secondDot < 0)
            return false;

        secondDot += firstDot + 1;

        var variantIdSpan = name[..firstDot];
        var sourceVariantSpan = name[(firstDot + 1)..secondDot];
        var sourceExtSpan = name[(secondDot + 1)..];

        if (!VariantId.IsValidAndNormalized(variantIdSpan))
            return false;

        bool sourceIsMain = sourceVariantSpan.SequenceEqual(FileRepoPaths.MainFileName.AsSpan());
        if (!sourceIsMain && !VariantId.IsValidAndNormalized(sourceVariantSpan))
            return false;

        string sourceExt = sourceExtSpan.Length is 0 ? string.Empty : "." + sourceExtSpan.ToString();
        if (sourceExt.Length is not 0 && !FileExtension.IsValidAndNormalized(sourceExt))
            return false;

        variantId = variantIdSpan.ToString();
        sourceVariantId = sourceIsMain ? null : sourceVariantSpan.ToString();
        sourceExtension = sourceExt;
        return true;
    }

    // TODO: Reconsider how dangling aliases are handled. This can only happen when a repository is corrupted, not during normal operation through proper API
    // usage, but is still worth properly handling. The current approach treats them as non-existent in some cases (e.g. fetching, which has discoverability
    // issues), and existant in others (e.g. when attempting to add a new variant with the same name, which will find the alias marker and treat it as a
    // collision). We will need to decide on a consistent approach to this edge case and impplement it across the board + document the behavior.

    /// <summary>
    /// Resolves an alias marker file to the data file it points to. Returns <see langword="null"/> if the marker cannot be parsed or its source data file is
    /// missing (dangling). Aliases by invariant always reference real data files, so this method does not follow chains.
    /// </summary>
    internal static IAbsoluteFilePath? ResolveAlias(IAbsoluteDirectoryPath filesDirectory, FileId fileId, IAbsoluteFilePath markerFile)
    {
        if (!TryParseAliasMarker(markerFile, out _, out string? sourceVariantId, out string? sourceExtension))
            return null;

        var sourceFile = GetDataFile(filesDirectory, fileId, sourceExtension, sourceVariantId);
        return sourceFile.State is EntryState.Exists ? sourceFile : null;
    }

    /// <summary>
    /// Gets the directory where the main file and its variants are stored for the specified file ID.
    /// </summary>
    internal static IAbsoluteDirectoryPath GetFileDirectory(IAbsoluteDirectoryPath filesDirectory, FileId fileId) => filesDirectory.Combine(fileId.RelativeDirectory);
}
