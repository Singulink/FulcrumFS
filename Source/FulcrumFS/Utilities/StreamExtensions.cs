using System.Buffers;
using Microsoft.IO;

namespace FulcrumFS.Utilities;

internal static class StreamExtensions
{
    private const int CopySeekableBufferSize = 131072; // 128kb

    public static async Task<Stream> CopyToSeekableAsync(
        this Stream source,
        long maxInMemorySize,
        IAbsoluteFilePath fallbackFile,
        CancellationToken cancellationToken = default)
    {
        long requestedMemoryStreamSize = 0;

        if (source.CanSeek)
        {
            if (source.Length > maxInMemorySize)
                return await CopyToFallbackFileAsync(null, source, fallbackFile, cancellationToken).ConfigureAwait(false);

            requestedMemoryStreamSize = source.Length;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopySeekableBufferSize);
        var memoryStream = new RecyclableMemoryStream(FileRepo.MemoryStreamManager, "FulcrumFS", requestedMemoryStreamSize);
        long totalBytesRead = 0;

        try
        {
            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

                if (bytesRead is 0)
                    break;

                memoryStream.Write(buffer, 0, bytesRead);
                totalBytesRead += bytesRead;

                if (totalBytesRead > maxInMemorySize)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = null;

                    return await CopyToFallbackFileAsync(memoryStream, source, fallbackFile, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }

        return memoryStream;
    }

    private static async Task<Stream> CopyToFallbackFileAsync(
        RecyclableMemoryStream? memoryStream,
        Stream source,
        IAbsoluteFilePath fallbackFile,
        CancellationToken cancellationToken)
    {
        fallbackFile.ParentDirectory.Create();
        var fileStream = fallbackFile.OpenAsyncStream(FileMode.Create, FileAccess.ReadWrite, FileShare.Delete);

        if (memoryStream is not null)
        {
            // Write what we have in memory so far

            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(fileStream, CopySeekableBufferSize, cancellationToken).ConfigureAwait(false);
            memoryStream.Dispose();
        }

        // Write the rest of the source stream
        await source.CopyToAsync(fileStream, CopySeekableBufferSize, cancellationToken).ConfigureAwait(false);
        return fileStream;
    }
}
