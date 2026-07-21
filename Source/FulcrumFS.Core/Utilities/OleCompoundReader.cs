using System.Buffers.Binary;
using System.Text;

namespace FulcrumFS.Utilities;

/// <summary>
/// Reads and identifies the contents of an OLE Compound File Binary (CFB) document by walking its directory entries.
/// </summary>
internal static class OleCompoundReader
{
    private const int HeaderSize = 512;
    private const int DirectoryEntrySize = 128;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const int MaxChainSectors = 32;

    private static readonly byte[] _signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public enum OleDocumentType
    {
        Unknown,
        Doc,
        Xls,
        Ppt,
    }

    /// <summary>
    /// Inspects an OLE Compound Document stream and identifies the contained document type by inspecting well-known stream names in the root directory.
    /// The stream must be positioned at the start of the CFB file. The stream position is undefined when this method returns.
    /// </summary>
    /// <returns>The detected document type, or <see cref="OleDocumentType.Unknown"/> if the stream is not a valid CFB file or none of the recognized streams
    /// are present.</returns>
    public static async ValueTask<OleDocumentType> DetectAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderSize];
        stream.Position = 0;
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return OleDocumentType.Unknown;

        if (!header.AsSpan(0, 8).SequenceEqual(_signature))
            return OleDocumentType.Unknown;

        ushort sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
        if (sectorShift is not 9 and not 12)
            return OleDocumentType.Unknown;

        int sectorSize = 1 << sectorShift;
        uint firstDirSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48, 4));

        // DIFAT: first 109 entries are embedded in the header at offset 76.
        uint[] difat = new uint[109];
        for (int i = 0; i < 109; i++)
            difat[i] = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76 + (i * 4), 4));

        byte[] sectorBuffer = new byte[sectorSize];
        byte[] fatBuffer = new byte[sectorSize];
        uint currentSector = firstDirSector;
        int sectorsVisited = 0;

        while (currentSector < 0xFFFFFFF0 && sectorsVisited < MaxChainSectors)
        {
            long sectorOffset = (long)(currentSector + 1) * sectorSize;
            stream.Position = sectorOffset;
            if (!await ReadExactAsync(stream, sectorBuffer, cancellationToken).ConfigureAwait(false))
                return OleDocumentType.Unknown;

            int entriesPerSector = sectorSize / DirectoryEntrySize;
            for (int e = 0; e < entriesPerSector; e++)
            {
                int entryOffset = e * DirectoryEntrySize;
                ushort nameByteLength = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuffer.AsSpan(entryOffset + 64, 2));

                if (nameByteLength is 0 or > 64)
                    continue;

                // Name length is in bytes including the UTF-16 null terminator.
                int nameCharCount = (nameByteLength / 2) - 1;
                if (nameCharCount <= 0)
                    continue;

                string name = Encoding.Unicode.GetString(sectorBuffer, entryOffset, nameCharCount * 2);

                switch (name)
                {
                    case "WordDocument":
                        return OleDocumentType.Doc;
                    case "Workbook" or "Book":
                        return OleDocumentType.Xls;
                    case "PowerPoint Document":
                        return OleDocumentType.Ppt;
                }
            }

            // Follow the FAT chain to find the next directory sector.
            int entriesPerFatSector = sectorSize / 4;
            int fatIndex = (int)(currentSector / entriesPerFatSector);
            int offsetInFatSector = (int)(currentSector % entriesPerFatSector) * 4;

            if (fatIndex >= 109)
                return OleDocumentType.Unknown; // Would require walking the extended DIFAT chain.

            uint fatSectorLocation = difat[fatIndex];
            if (fatSectorLocation >= 0xFFFFFFF0)
                return OleDocumentType.Unknown;

            stream.Position = (long)(fatSectorLocation + 1) * sectorSize;
            if (!await ReadExactAsync(stream, fatBuffer, cancellationToken).ConfigureAwait(false))
                return OleDocumentType.Unknown;

            uint nextSector = BinaryPrimitives.ReadUInt32LittleEndian(fatBuffer.AsSpan(offsetInFatSector, 4));
            if (nextSector == EndOfChain || nextSector == FreeSect)
                break;

            currentSector = nextSector;
            sectorsVisited++;
        }

        return OleDocumentType.Unknown;
    }

    private static async ValueTask<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read is 0)
                return false;
            totalRead += read;
        }

        return true;
    }
}
