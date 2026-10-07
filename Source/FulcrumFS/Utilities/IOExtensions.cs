namespace FulcrumFS.Utilities;

/// <summary>
/// Provides file and directory helpers for <see cref="IFileRepo"/> implementations.
/// </summary>
/// <remarks>
/// This class exists for <see cref="IFileRepo"/> implementations and is not meant for application code.
/// </remarks>
// Hidden as a whole, unlike the types that have only some members for implementations: nothing in it is meant for application code.
[EditorBrowsable(EditorBrowsableState.Never)]
public static class IOExtensions
{
    /// <summary>
    /// Copies the source file to a new destination file.
    /// </summary>
    /// <param name="source">The file to copy, opened for reading while sharing reading and deletion.</param>
    /// <param name="destination">The file to create. It must not exist.</param>
    /// <param name="cancellationToken">A token that cancels the copy.</param>
    /// <remarks>
    /// This method exists for <see cref="IFileRepo"/> implementations and is not meant for application code.
    /// </remarks>
    /// <exception cref="IOException"><paramref name="destination"/> already exists, or the copy failed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task CopyToAsync(this IAbsoluteFilePath source, IAbsoluteFilePath destination, CancellationToken cancellationToken = default)
    {
        return source.CopyToAsync(destination, overwrite: false, cancellationToken);
    }

    /// <summary>
    /// Copies the source file to the destination file, replacing the destination if it exists and <paramref name="overwrite"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="source">The file to copy, opened for reading while sharing reading and deletion.</param>
    /// <param name="destination">The file to create or replace.</param>
    /// <param name="overwrite"><see langword="true"/> to replace <paramref name="destination"/> if it exists; <see langword="false"/> to fail instead.</param>
    /// <param name="cancellationToken">A token that cancels the copy.</param>
    /// <remarks>
    /// This method exists for <see cref="IFileRepo"/> implementations and is not meant for application code.
    /// </remarks>
    /// <exception cref="IOException"><paramref name="overwrite"/> is <see langword="false"/> and <paramref name="destination"/> already exists, or the copy
    /// failed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static async Task CopyToAsync(this IAbsoluteFilePath source, IAbsoluteFilePath destination, bool overwrite, CancellationToken cancellationToken = default)
    {
        var sourceStream = source.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        await using (sourceStream.ConfigureAwait(false))
        {
            var destinationStream = destination.OpenAsyncStream(overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Delete);

            await using (destinationStream.ConfigureAwait(false))
            {
                await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tries to delete the file, returning <see langword="false"/> with the exception instead of throwing an <see cref="IOException"/>.
    /// </summary>
    /// <param name="file">The file to delete.</param>
    /// <param name="exception">When this method returns <see langword="false"/>, the <see cref="IOException"/> that the deletion threw; otherwise
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the deletion succeeded; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// This method exists for <see cref="IFileRepo"/> implementations and is not meant for application code. Exceptions other than <see cref="IOException"/>
    /// are not caught.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool TryDelete(this IAbsoluteFilePath file, [NotNullWhen(false)] out Exception? exception)
    {
        try
        {
            file.Delete();
            exception = null;
            return true;
        }
        catch (IOException ex)
        {
            exception = ex;
            return false;
        }
    }

    /// <summary>
    /// Tries to delete the directory, returning <see langword="false"/> with the exception instead of throwing an <see cref="IOException"/>.
    /// </summary>
    /// <param name="directory">The directory to delete.</param>
    /// <param name="recursive"><see langword="true"/> to delete the directory with everything in it; <see langword="false"/> to delete it only if it is
    /// empty.</param>
    /// <param name="exception">When this method returns <see langword="false"/>, the <see cref="IOException"/> that the deletion threw; otherwise
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the deletion succeeded; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// This method exists for <see cref="IFileRepo"/> implementations and is not meant for application code. Exceptions other than <see cref="IOException"/>
    /// are not caught.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool TryDelete(this IAbsoluteDirectoryPath directory, bool recursive, [NotNullWhen(false)] out Exception? exception)
    {
        try
        {
            directory.Delete(recursive);
            exception = null;
            return true;
        }
        catch (IOException ex)
        {
            exception = ex;
            return false;
        }
    }

    internal static bool TryDelete(this IAbsoluteDirectoryPath directory, [NotNullWhen(false)] out Exception? exception)
    {
        return TryDelete(directory, recursive: false, out exception);
    }
}
