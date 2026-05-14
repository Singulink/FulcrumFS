namespace FulcrumFS.Utilities;

/// <summary>
/// Utility methods for reading file signatures and headers from streams.
/// </summary>
internal static class StreamSignatureUtils
{
    /// <summary>
    /// Reads up to <paramref name="count"/> bytes from the start of the stream into a new buffer. Resets the stream position to 0 before reading.
    /// </summary>
    /// <returns>A buffer of length equal to the number of bytes actually read (may be less than <paramref name="count"/> if the stream is shorter).</returns>
    public static async ValueTask<byte[]> ReadHeaderAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] buffer = new byte[count];
        int totalRead = 0;

        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);
            if (read is 0)
                break;
            totalRead += read;
        }

        if (totalRead == count)
            return buffer;

        byte[] result = new byte[totalRead];
        Buffer.BlockCopy(buffer, 0, result, 0, totalRead);
        return result;
    }

    /// <summary>
    /// Checks whether the specified header bytes start with the specified signature.
    /// </summary>
    public static bool StartsWith(ReadOnlySpan<byte> header, ReadOnlySpan<byte> signature) =>
        header.Length >= signature.Length && header[..signature.Length].SequenceEqual(signature);
}
