using Singulink.IO;

namespace FulcrumFS.Videos;

/// <summary>
/// Reads the EBML DocType element from a Matroska/WebM file. Returns a value such as "matroska" or "webm".
/// </summary>
internal static class EbmlHeaderReader
{
    private const int HeaderReadSize = 64;

    public static async Task<string?> ReadDocTypeAsync(IAbsoluteFilePath filePath, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[HeaderReadSize];
        int totalRead = 0;
        var stream = filePath.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read);
        await using (stream.ConfigureAwait(false))
        {
            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                totalRead += read;
            }
        }

        return TryReadDocType(buffer.AsSpan(0, totalRead));
    }

    private static string? TryReadDocType(ReadOnlySpan<byte> data)
    {
        // EBML file starts with the EBML header element (ID 0x1A45DFA3), within which is the DocType element (ID 0x4282).
        // Scan for the DocType ID near the start (it's always within the first ~40 bytes of a valid Matroska/WebM file).
        for (int i = 0; i + 3 <= data.Length; i++)
        {
            if (data[i] != 0x42 || data[i + 1] != 0x82)
                continue;

            // The element data length follows as a VINT. For DocType strings ("matroska", "webm") the length is small, so a 1-byte VINT (0x81-0x8F) is expected.
            byte lenByte = data[i + 2];
            if (lenByte < 0x81 || lenByte > 0x8F)
                continue;

            int length = lenByte & 0x7F;
            int valueStart = i + 3;
            if (valueStart + length > data.Length)
                return null;

            for (int j = 0; j < length; j++)
            {
                byte b = data[valueStart + j];
                if (b == 0)
                {
                    length = j;
                    break;
                }

                // DocType is restricted to printable ASCII.
                if (b < 0x20 || b > 0x7E)
                    return null;
            }

            return System.Text.Encoding.ASCII.GetString(data.Slice(valueStart, length));
        }

        return null;
    }
}
