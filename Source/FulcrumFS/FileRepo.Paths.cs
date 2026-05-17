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

    internal static IAbsoluteFilePath? FindDataFile(IAbsoluteDirectoryPath filesDirectory, FileId fileId, string? variantId = null)
    {
        string fileNamePart = variantId ?? FileRepoPaths.MainFileName;

        try
        {
            return GetFileDirectory(filesDirectory, fileId).GetChildFiles(fileNamePart + ".*").SingleOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the directory where the main file and its variants are stored for the specified file ID.
    /// </summary>
    internal static IAbsoluteDirectoryPath GetFileDirectory(IAbsoluteDirectoryPath filesDirectory, FileId fileId) => filesDirectory.Combine(fileId.RelativeDirectory);
}
