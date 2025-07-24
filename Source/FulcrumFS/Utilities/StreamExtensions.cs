using System.Buffers;
using System.Runtime.InteropServices;
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
                return await CopyToFallbackFileAsync(null, default, source, fallbackFile, cancellationToken).ConfigureAwait(false);

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

                totalBytesRead += bytesRead;

                if (totalBytesRead <= maxInMemorySize)
                {
                    memoryStream.Write(buffer, 0, bytesRead);
                }
                else
                {
                    // CopyToFallbackFileAsync will return the buffer to the array pool.
                    var overflowRead = buffer.AsMemory(0, bytesRead);
                    buffer = null;

                    return await CopyToFallbackFileAsync(memoryStream, overflowRead, source, fallbackFile, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private static async Task<Stream> CopyToFallbackFileAsync(
        RecyclableMemoryStream? memoryStream,
        ReadOnlyMemory<byte> rentedOverflowRead,
        Stream source,
        IAbsoluteFilePath fallbackFile,
        CancellationToken cancellationToken)
    {
        FileStream fileStream = null;

        try
        {
            fallbackFile.ParentDirectory.Create();
            fileStream = fallbackFile.OpenAsyncStream(FileMode.Create, FileAccess.ReadWrite, FileShare.Delete);

            if (memoryStream is not null)
            {
                // Write what we have in memory so far

                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(fileStream, CopySeekableBufferSize, cancellationToken).ConfigureAwait(false);
                memoryStream.Dispose();
            }

            if (!rentedOverflowRead.IsEmpty)
            {
                // Write the overflow data from last read
                await fileStream.WriteAsync(rentedOverflowRead, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (MemoryMarshal.TryGetArray(rentedOverflowRead, out var segment) && segment.Array is not null)
                ArrayPool<byte>.Shared.Return(segment.Array);
        }

        // Write the rest of the source stream
        await source.CopyToAsync(fileStream, CopySeekableBufferSize, cancellationToken).ConfigureAwait(false);

        fileStream.Position = 0;
        return fileStream;
    }
}
