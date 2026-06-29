using System.Collections.Frozen;

namespace FulcrumFS.Utilities;

/// <summary>
/// Utility for validating the ISO Base Media File Format (ISOBMFF) <c>ftyp</c> box from the start of a stream and extracting the major brand and compatible
/// brands. Used to identify members of the ISOBMFF family (MP4, MOV, M4A, 3GP, 3G2, MJ2, HEIC, HEIF, AVIF).
/// </summary>
internal static class IsoBmffHeaderValidator
{
    /// <summary>
    /// Reads the <c>ftyp</c> box from the start of the stream. Returns <see cref="FileFormatValidationResult.Invalid(string)"/> if the stream does not contain
    /// a valid <c>ftyp</c> box at the start.
    /// </summary>
    public static async ValueTask<FileFormatValidationResult> CheckFtypAsync(string name, FrozenSet<string> acceptedBrands, Stream stream, CancellationToken cancellationToken)
    {
        // Read in chunks of up to 256 bytes at a time, re-using the same buffer, so that 'ftyp' boxes with many compatible brands can be scanned past the
        // first 256 bytes without ever holding more than one chunk in memory.
        const int ChunkSize = 256;

        // Maximum number of bytes of the 'ftyp' box to scan; a sane upper bound that avoids reading an unbounded amount for a corrupt or malicious box size.
        const int MaxFtypBoxSize = 4096;

        // ftyp box: 4-byte size | 4-byte type ("ftyp") | 4-byte major_brand | 4-byte minor_version | N x 4-byte compatible_brands
        // JPEG 2000 family files (.jp2 / .mj2) prefix the ftyp box with a 12-byte JPEG 2000 signature box ("jP  "), so we may have to skip it first.
        byte[] buffer = new byte[ChunkSize];

        stream.Position = 0;
        int length = await ReadChunkAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

        int offset = 0;

        // Skip a leading JPEG 2000 signature box if present: 4-byte size (0x0000000C) | "jP  " (4 bytes) | 4-byte content.
        if (length >= 12
            && ReadUInt32BigEndian(buffer, 0) is 12
            && buffer.AsSpan(4, 4).SequenceEqual("jP  "u8))
        {
            offset = 12;
        }

        if (length < offset + 16)
            return FileFormatValidationResult.Invalid($"File does not contain a valid ISOBMFF 'ftyp' box (required for {name}).");

        uint size = ReadUInt32BigEndian(buffer, offset);
        if (buffer.AsSpan(offset + 4, 4).SequenceEqual("ftyp"u8) is false)
            return FileFormatValidationResult.Invalid($"File does not contain a valid ISOBMFF 'ftyp' box (required for {name}).");

        string majorBrand = ReadFourCc(buffer, offset + 8);
        if (acceptedBrands.Count == 0 || acceptedBrands.Contains(majorBrand))
            return FileFormatValidationResult.Success;

        // Compatible brands start at offset 16 (relative to the ftyp box start at 'offset') and run to the end of the box, each 4 bytes. Determine the
        // absolute end position of the box, capping it at a sane maximum.
        if (size < 16)
            return FileFormatValidationResult.Invalid($"File does not contain a valid ISOBMFF 'ftyp' box (required for {name}).");
        int boxEnd = offset + (int)Math.Min(size, MaxFtypBoxSize);

        int chunkStart = 0;            // Absolute position (from the start of the stream) of buffer[0] for the current chunk.
        int brandOffset = offset + 16; // Buffer-relative offset of the first compatible brand in the current chunk.

        while (true)
        {
            // Scan complete 4-byte compatible brands that fall within both the current chunk and the box bounds.
            for (int p = brandOffset; p + 4 <= length && chunkStart + p + 4 <= boxEnd; p += 4)
            {
                if (acceptedBrands.Contains(ReadFourCc(buffer, p)))
                    return FileFormatValidationResult.Success;
            }

            chunkStart += length;

            // Stop once the whole box has been covered, or the end of the stream is reached (a short read means there is no more data).
            if (chunkStart >= boxEnd || length < ChunkSize)
                break;

            length = await ReadChunkAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            brandOffset = 0; // Subsequent chunks begin exactly on a 4-byte brand boundary, since ChunkSize is a multiple of 4.

            if (length is 0)
                break;
        }

        return FileFormatValidationResult.Invalid(
            $"File's major brand '{majorBrand}' and compatible brands do not match any accepted brand for {name}.");
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> from the current position of the stream, looping until it is full or the end of the stream is reached.
    /// </summary>
    /// <returns>The number of bytes read, which is less than the buffer length only when the end of the stream is reached.</returns>
    private static async ValueTask<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read is 0)
                break;
            totalRead += read;
        }

        return totalRead;
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
    }

    private static string ReadFourCc(byte[] buffer, int offset)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
            chars[i] = (char)buffer[offset + i];
        return new string(chars);
    }
}
