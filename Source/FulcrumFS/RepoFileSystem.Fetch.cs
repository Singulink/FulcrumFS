namespace FulcrumFS;

/// <content>
/// Fetch-side enumeration helpers that classify the on-disk state of a single variant slot (data file/alias marker plus its companion in-group delete and
/// rebase markers) in one syscall.
/// </content>
partial class RepoFileSystem
{
    /// <summary>
    /// Returns the data file (or alias marker) for the specified variant (or main file when <paramref name="variantId"/> is <see langword="null"/>), along
    /// with two flags: <c>IsRetired</c> is <see langword="true"/> when an in-group delete marker (<c>{variantId}.del</c>) is present (the data file and the
    /// delete marker can co-exist in deferred-delete mode, so a non-<see langword="null"/> file paired with <c>IsRetired</c> = <see langword="true"/> means
    /// the variant has been retired but its underlying data file has not yet been physically removed); <c>RebaseChosenVariantId</c> is set to the chosen
    /// survivor's variant ID when a rebase marker (<c>{variantId}.{chosen}.rebase</c>) naming this variant as the rebase source is present, and
    /// <see langword="null"/> otherwise. The main file (<paramref name="variantId"/> = <see langword="null"/>) does not participate in either marker scheme.
    /// </summary>
    /// <remarks>
    /// If <see cref="CorruptionDetected"/> resolves to a non-<see langword="null"/> handler, it is invoked immediately before a
    /// <see cref="RepoCorruptedException"/> is thrown for a <see cref="RepoCorruptionKind.DuplicateVariantEntry"/> condition (more than one non-marker file
    /// maps to the variant slot).
    /// </remarks>
    public async ValueTask<(IAbsoluteFilePath? File, bool IsRetired, string? RebaseChosenVariantId)> FindDataFileAsync(FileId fileId, string? variantId)
    {
        string fileNamePart = variantId ?? FileRepoPaths.MainFileName;
        bool isRetired = false;
        string? rebaseChosenVariantId = null;
        IAbsoluteFilePath? dataFile = null;

        try
        {
            // Single enumeration: the {name}.* glob already matches the data file/alias marker, the in-group delete marker ({variantId}.del) AND any rebase marker
            // naming this variant as the source ({variantId}.{chosen}.rebase), so all three pieces of state are captured in one syscall.
            foreach (var f in GetFileDirectory(fileId).GetChildFiles(fileNamePart + ".*"))
            {
                if (f.Extension is FileRepoPaths.DeleteMarkerExtension)
                {
                    isRetired = true;
                }
                else if (f.Extension is FileRepoPaths.RebaseMarkerExtension)
                {
                    // A rebase marker ({source}.{chosen}.rebase) matches the "{source}.*" glob but is not a data file. Capture the chosen survivor so callers
                    // can roll the rebase forward, but never treat the marker as a data file (which would trip the "multiple data files" corruption guard).
                    if (TryParseRebaseMarker(f, out string? markerSource, out string? markerChosen) && markerSource == fileNamePart)
                        rebaseChosenVariantId = markerChosen;
                }
                else if (dataFile is null)
                {
                    dataFile = f;
                }
                else
                {
                    await CorruptionDetected.RaiseDuplicateVariantEntryAsync(fileId, variantId, dataFile.Name, f.Name).ConfigureAwait(false);

                    throw new RepoCorruptedException(
                        $"Multiple data files found for '{fileNamePart}' in file group of file ID '{fileId}': '{dataFile.Name}', '{f.Name}'. " +
                        $"This indicates repository corruption.");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return (null, false, null);
        }

        return (dataFile, isRetired, rebaseChosenVariantId);
    }
}
