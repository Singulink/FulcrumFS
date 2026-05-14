namespace FulcrumFS.Utilities;

/// <summary>
/// Utility for reading the ISO Base Media File Format (ISOBMFF) <c>ftyp</c> box from the start of a stream and extracting the major brand and compatible
/// brands. Used to identify members of the ISOBMFF family (MP4, MOV, M4A, 3GP, 3G2, MJ2, HEIC, HEIF, AVIF).
/// </summary>
internal static class IsoBmffHeaderReader
{
    /// <summary>
    /// Represents the contents of the ISOBMFF <c>ftyp</c> box.
    /// </summary>
    public readonly record struct FtypInfo(string MajorBrand, IReadOnlyList<string> CompatibleBrands);

    /// <summary>
    /// Reads the <c>ftyp</c> box from the start of the stream. Returns <see langword="null"/> if the stream does not contain a valid <c>ftyp</c> box at the
    /// start.
    /// </summary>
    public static async ValueTask<FtypInfo?> ReadFtypAsync(Stream stream, CancellationToken cancellationToken)
    {
        // ftyp box: 4-byte size | 4-byte type ("ftyp") | 4-byte major_brand | 4-byte minor_version | N x 4-byte compatible_brands
        // JPEG 2000 family files (.jp2 / .mj2) prefix the ftyp box with a 12-byte JPEG 2000 signature box ("jP  "), so we may have to skip it first.
        // Read a generous header: enough for an optional signature box, the ftyp box, and a few compatible brands.
        byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 256, cancellationToken).ConfigureAwait(false);

        int offset = 0;

        // Skip a leading JPEG 2000 signature box if present: 4-byte size (0x0000000C) | "jP  " (4 bytes) | 4-byte content.
        if (header.Length >= 12
            && ReadUInt32BigEndian(header, 0) is 12
            && header.AsSpan(4, 4).SequenceEqual("jP  "u8))
        {
            offset = 12;
        }

        if (header.Length < offset + 16)
            return null;

        uint size = ReadUInt32BigEndian(header, offset);
        if (header.AsSpan(offset + 4, 4).SequenceEqual("ftyp"u8) is false)
            return null;

        // Use the smaller of the box size (if reasonable) or available header bytes.
        int boxEnd = size is >= 16 and <= 256 ? offset + (int)size : header.Length;
        if (boxEnd > header.Length)
            boxEnd = header.Length;

        string majorBrand = ReadFourCc(header, offset + 8);

        // Compatible brands start at offset 16 (relative to ftyp box start) and run to end of box, each 4 bytes.
        var compatible = new List<string>();
        for (int p = offset + 16; p + 4 <= boxEnd; p += 4)
            compatible.Add(ReadFourCc(header, p));

        return new FtypInfo(majorBrand, compatible);
    }

    /// <summary>
    /// Determines whether the major brand or any compatible brand is in the specified accepted set.
    /// </summary>
    public static bool BrandsMatch(FtypInfo info, IReadOnlySet<string> acceptedBrands)
    {
        if (acceptedBrands.Contains(info.MajorBrand))
            return true;

        foreach (string brand in info.CompatibleBrands)
        {
            if (acceptedBrands.Contains(brand))
                return true;
        }

        return false;
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
