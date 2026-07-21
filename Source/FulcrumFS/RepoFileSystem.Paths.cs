using System.Diagnostics;

namespace FulcrumFS;

/// <content>
/// Path-derivation helpers that locate data files, file group directories, and marker files within the file repository, plus filename parsers that need no
/// repository state.
/// </content>
partial class RepoFileSystem
{
    /// <summary>
    /// Gets the directory where the main file and its variants are stored for the specified file ID.
    /// </summary>
    public IAbsoluteDirectoryPath GetFileDirectory(FileId fileId) => FilesDirectory.Combine(fileId.RelativeDirectory);

    public IAbsoluteFilePath GetDataFile(FileId fileId, string extension, string? variantId = null)
    {
        string fileNamePart = variantId ?? FileRepoPaths.MainFileName;
        string fullFileName = fileNamePart + extension;
        return GetFileDirectory(fileId).CombineFile(fullFileName, PathOptions.None);
    }

    public IAbsoluteFilePath GetDeleteMarker(FileId fileId, string? variant)
    {
        string name = variant is null ? fileId.ToString() : GetFileIdAndVariantString(fileId, variant);
        return CleanupDirectory.CombineFile(name + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
    }

    /// <summary>
    /// Builds the absolute path for the in-group authoritative delete marker for a variant: <c>files/{shard}/{id}/{variantId}.del</c>.
    /// The delete marker lives in the file group directory (not the cleanup directory) and is the fail-fast signal for fetch operations that a variant has been
    /// retired, even while the data file lingers on disk before the cleaner physically removes it.
    /// </summary>
    public IAbsoluteFilePath GetInGroupDeleteMarker(FileId fileId, string variantId)
    {
        Debug.Assert(VariantId.IsValidAndNormalized(variantId), "variantId must be normalized.");
        return GetFileDirectory(fileId).CombineFile(variantId + FileRepoPaths.DeleteMarkerExtension, PathOptions.None);
    }

    /// <summary>
    /// Builds the absolute path for a rebase marker: <c>files/{shard}/{id}/{sourceVariantId}.{chosenVariantId}.rebase</c>. The marker
    /// records the in-progress promotion of an alias dependent (<paramref name="chosenVariantId"/>) to a standalone data file while
    /// <paramref name="sourceVariantId"/> is being retired with surviving (unlisted) dependents.
    /// </summary>
    public IAbsoluteFilePath GetRebaseMarker(FileId fileId, string sourceVariantId, string chosenVariantId)
    {
        Debug.Assert(VariantId.IsValidAndNormalized(sourceVariantId), "sourceVariantId must be normalized.");
        Debug.Assert(VariantId.IsValidAndNormalized(chosenVariantId), "chosenVariantId must be normalized.");
        string fileName = $"{sourceVariantId}.{chosenVariantId}{FileRepoPaths.RebaseMarkerExtension}";
        return GetFileDirectory(fileId).CombineFile(fileName, PathOptions.None);
    }

    public IAbsoluteFilePath GetIndeterminateMarker(FileId fileId) =>
        CleanupDirectory.CombineFile(fileId + FileRepoPaths.IndeterminateMarkerExtension, PathOptions.None);

    /// <summary>
    /// Builds the absolute path for an alias marker file. <paramref name="sourceVariantId"/> may be <see langword="null"/> to indicate the main file
    /// (encoded as the <see cref="FileRepoPaths.MainFileName"/> sentinel). <paramref name="sourceExtension"/> includes its leading dot.
    /// </summary>
    public IAbsoluteFilePath GetAliasMarker(IAbsoluteDirectoryPath fileGroupDir, string variantId, string? sourceVariantId, string sourceExtension)
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
    /// Resolves an alias marker file to the data file it points to. Returns <see langword="null"/> if the marker cannot be parsed or its source data file is
    /// missing (dangling). Aliases by invariant always reference real data files, so this method does not follow chains.
    /// </summary>
    public IAbsoluteFilePath? ResolveAlias(FileId fileId, IAbsoluteFilePath markerFile)
    {
        if (!TryParseAliasMarker(markerFile, out _, out string? sourceVariantId, out string? sourceExtension))
            return null;

        var sourceFile = GetDataFile(fileId, sourceExtension, sourceVariantId);
        return sourceFile.State is EntryState.Exists ? sourceFile : null;
    }

    /// <summary>
    /// Returns <see langword="true"/> if an in-group delete marker (<c>{variantId}.del</c>) exists for the variant. A delete marker marks the variant as
    /// permanently retired. Prefer the <c>IsRetired</c> flag returned by <c>FindDataFileAsync</c> when also retrieving the data file; this helper is for
    /// cases where only the delete marker state is needed (e.g. checking whether an alias's source variant has been retired) and a single-file existence
    /// probe is cheaper than a directory glob enumeration.
    /// </summary>
    public bool IsVariantRetired(FileId fileId, string variantId)
    {
        Debug.Assert(VariantId.IsValidAndNormalized(variantId), "variantId must be normalized.");
        return GetInGroupDeleteMarker(fileId, variantId).State is EntryState.Exists;
    }

    /// <summary>
    /// Attempts to parse a rebase marker filename of the form <c>{sourceVariantId}.{chosenVariantId}.rebase</c>.
    /// </summary>
    public static bool TryParseRebaseMarker(
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

    /// <summary>
    /// Attempts to parse an alias marker filename of the form <c>{variantId}.{sourceVariantId}.{sourceExt}.alias</c>. The
    /// <paramref name="sourceVariantId"/> output is <see langword="null"/> when the source is the main file (sentinel <see cref="FileRepoPaths.MainFileName"/>);
    /// otherwise it is a normalized variant ID. <paramref name="sourceExtension"/> is returned with a leading dot (or empty if the source has no extension).
    /// </summary>
    public static bool TryParseAliasMarker(
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

    public static bool TryParseFileIdAndVariant(string entryName, [MaybeNullWhen(false)] out FileId fileId, out string? variantId)
    {
        int separatorIndex = entryName.IndexOf(' ');

        if (separatorIndex < 0)
        {
            variantId = null;
            return FileId.TryParseUnsafe(entryName, out fileId);
        }

        var fileIdChars = entryName.AsSpan()[..separatorIndex];
        var variantIdChars = entryName.AsSpan()[(separatorIndex + 1)..];

        if (FileId.TryParse(fileIdChars, out fileId) && VariantId.IsValidAndNormalized(variantIdChars))
        {
            variantId = variantIdChars.ToString();
            return true;
        }

        fileId = null;
        variantId = null;
        return false;
    }

    /// <summary>
    /// Gets a unique entry name for a given file ID and variant ID which is used to name temp work directories or cleanup files associated with the file.
    /// </summary>
    public static string GetFileIdAndVariantString(FileId fileId, string? variantId) => variantId is null ? fileId.ToString() : $"{fileId} {variantId}";
}
