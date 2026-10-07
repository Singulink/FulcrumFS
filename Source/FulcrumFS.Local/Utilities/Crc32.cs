namespace FulcrumFS.Utilities;

/// <summary>
/// Computes CRC-32 checksums as defined by IEEE 802.3 (reflected polynomial <c>0xEDB88320</c>, initial value <c>0xFFFFFFFF</c>, final XOR <c>0xFFFFFFFF</c>),
/// used to detect damaged delete journals.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] _table = CreateTable();

    /// <summary>
    /// Computes the CRC-32 checksum of the specified data.
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
            crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] CreateTable()
    {
        uint[] table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;

            for (int bit = 0; bit < 8; bit++)
                entry = (entry & 1) is not 0 ? (entry >> 1) ^ 0xEDB88320 : entry >> 1;

            table[i] = entry;
        }

        return table;
    }
}
