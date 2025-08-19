namespace FulcrumFS;

/// <content>
/// Contains the implementations of file add functionality for the file repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Gets an existing file from the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async ValueTask<IAbsoluteFilePath> GetAsync(FileId fileId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return FindDataFile(fileId) ?? throw new RepoFileNotFoundException($"File ID '{fileId}' was not found.");
    }

    /// <summary>
    /// Opens a file stream for an existing file in the repository.
    /// </summary>
    /// <exception cref="RepoFileNotFoundException">File with the specified <paramref name="fileId"/> does not exist.</exception>
    public async ValueTask<Stream> OpenAsync(FileId fileId)
    {
        var file = await GetAsync(fileId).ConfigureAwait(false);
        return file.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }
}
