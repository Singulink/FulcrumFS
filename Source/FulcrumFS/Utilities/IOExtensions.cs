using System.Diagnostics.CodeAnalysis;

internal static class IOExtensions
{
    public static async ValueTask CreateAsync(this IAbsoluteFilePath file, bool overwrite)
    {
        var fileMode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var stream = file.OpenAsyncStream(fileMode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1).ConfigureAwait(false);
    }

    public static async Task CopyToAsync(this IAbsoluteFilePath source, IAbsoluteFilePath destination, CancellationToken cancellationToken = default)
    {
        var sourceStream = source.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        await using (sourceStream.ConfigureAwait(false))
        {
            var destinationStream = destination.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.Delete);

            await using (destinationStream.ConfigureAwait(false))
            {
                await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            }
        }
    }

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

    public static bool TryDelete(this IAbsoluteDirectoryPath directory, [NotNullWhen(false)] out Exception? exception)
    {
        return TryDelete(directory, recursive: false, out exception);
    }

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
}
