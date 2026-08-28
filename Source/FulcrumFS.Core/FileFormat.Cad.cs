using System.Buffers;
using FulcrumFS.Utilities;

namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of built-in CAD <see cref="FileFormat"/> instances.
/// </content>
public abstract partial class FileFormat
{
    private sealed class StepFileFormat : FileFormat
    {
        public override string Name => "STEP";

        public override IReadOnlyList<string> Extensions { get; } = [".step", ".stp"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 64, cancellationToken).ConfigureAwait(false);

            // Tolerate an optional UTF-8 BOM and leading whitespace before the ISO 10303-21 header keyword.
            var content = StreamSignatureUtils.SkipBomAndWhitespace(header);

            if (!StreamSignatureUtils.StartsWith(content, "ISO-10303-21"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid STEP (ISO 10303-21) header.");

            return FileFormatValidationResult.Success;
        }
    }

    // SolidWorks documents come in two container formats: legacy documents use OLE Compound Documents, while modern documents use a ZIP-like container with
    // a proprietary variable-length prefix (bytes 4-7 are 00 00 00 04), nibble-swapped entry names and obfuscated entry sizes (so the entry chain cannot be
    // walked). The leading entries vary by document type and version ('swXmlContents/...', 'Contents/Config-...', 'Preview'/'PreviewPNG'), but every
    // document carries 'Contents/...' entries, which can sit behind the embedded preview images (tens of KB observed), so a nibble-swapped 'Contents' is
    // searched for within the first few MB of the file (verified against real SolidWorks part, assembly and drawing files). The document types cannot be
    // reliably distinguished from each other at the container level, so they all accept either container type.
    private sealed class SolidWorksFileFormat(string name, string extension) : FileFormat
    {
        // Comfortably beyond the preview images that precede the first 'Contents' entry in real documents.
        private const int ModernContentsSearchLimit = 4 * 1024 * 1024;

        // 'Contents' in ASCII with the high/low nibbles of each byte swapped.
        private static readonly byte[] _nibbleSwappedContents = [0x34, 0xF6, 0xE6, 0x47, 0x56, 0xE6, 0x47, 0x37];

        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = [extension];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 8, cancellationToken).ConfigureAwait(false);

            // Modern container: bytes 4-7 are 00 00 00 04 and a nibble-swapped 'Contents' entry name appears within the search limit.
            if (header.Length >= 8 && header.AsSpan(4, 4).SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x04]))
            {
                if (await ContainsAsync(stream, _nibbleSwappedContents, ModernContentsSearchLimit, cancellationToken).ConfigureAwait(false))
                    return FileFormatValidationResult.Success;

                return FileFormatValidationResult.Invalid($"File has a modern SolidWorks container prefix but no 'Contents' entry was found (required for {Name}).");
            }

            // Legacy container: an OLE Compound Document whose contents do not identify it as one of the other known OLE document types.
            if (StreamSignatureUtils.StartsWith(header, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
            {
                var detected = await OleCompoundReader.DetectAsync(stream, cancellationToken).ConfigureAwait(false);

                if (detected is not OleCompoundReader.OleDocumentType.Unknown)
                    return FileFormatValidationResult.Invalid($"File is a valid OLE Compound Document but its contents identify it as {detected} rather than {Name}.");

                return FileFormatValidationResult.Success;
            }

            return FileFormatValidationResult.Invalid($"File does not have a valid {Name} (SolidWorks document) signature.");
        }

        // Streams the search window through a pooled buffer, carrying the tail of each read over so a pattern straddling two reads is still found.
        private static async ValueTask<bool> ContainsAsync(Stream stream, byte[] pattern, int searchLimit, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                stream.Position = 0;
                int carried = 0;
                int remaining = searchLimit;

                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(carried, Math.Min(buffer.Length - carried, remaining)), cancellationToken).ConfigureAwait(false);

                    if (read is 0)
                        return false;

                    int filled = carried + read;

                    if (buffer.AsSpan(0, filled).IndexOf(pattern) >= 0)
                        return true;

                    remaining -= read;
                    carried = Math.Min(pattern.Length - 1, filled);
                    buffer.AsSpan(filled - carried, carried).CopyTo(buffer);
                }

                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private sealed class DxfFileFormat : FileFormat
    {
        public override string Name => "DXF";

        public override IReadOnlyList<string> Extensions { get; } = [".dxf"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 256, cancellationToken).ConfigureAwait(false);

            // Binary DXF files start with a fixed sentinel.
            if (StreamSignatureUtils.StartsWith(header, "AutoCAD Binary DXF\r\n"u8))
                return FileFormatValidationResult.Success;

            // ASCII DXF files are group code / value line pairs. The first pair is "0" / "SECTION", optionally
            // preceded by "999" comment pairs. Group codes may be padded with leading whitespace.
            var content = StreamSignatureUtils.SkipBom(header);

            string[] lines = System.Text.Encoding.Latin1.GetString(content).Split('\n');

            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                string code = lines[i].Trim();
                string value = lines[i + 1].Trim();

                if (code is "999")
                    continue; // Comment pair.

                return code is "0" && value.Equals("SECTION", StringComparison.OrdinalIgnoreCase)
                    ? FileFormatValidationResult.Success
                    : FileFormatValidationResult.Invalid("File does not start with a valid DXF section header.");
            }

            return FileFormatValidationResult.Invalid("File does not start with a valid DXF section header.");
        }
    }

    private sealed class DwgFileFormat : FileFormat
    {
        public override string Name => "DWG";

        public override IReadOnlyList<string> Extensions { get; } = [".dwg"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 6, cancellationToken).ConfigureAwait(false);

            // DWG files start with a 6-character version string, e.g. "AC1032" (AutoCAD 2018+).
            if (header.Length < 6 || !StreamSignatureUtils.StartsWith(header, "AC1"u8) ||
                !char.IsAsciiDigit((char)header[3]) || !char.IsAsciiDigit((char)header[4]) || !char.IsAsciiDigit((char)header[5]))
            {
                return FileFormatValidationResult.Invalid("File does not have a valid DWG version signature.");
            }

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class IgesFileFormat : FileFormat
    {
        public override string Name => "IGES";

        public override IReadOnlyList<string> Extensions { get; } = [".igs", ".iges"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 80, cancellationToken).ConfigureAwait(false);

            // IGES files are fixed 80-column records with the section letter in column 73; the first record is a
            // start ('S') section record with its sequence number in columns 74-80.
            if (header.Length < 80 || header[72] is not (byte)'S')
                return FileFormatValidationResult.Invalid("File does not have a valid IGES start section record.");

            for (int i = 73; i < 80; i++)
            {
                if (header[i] is not ((byte)' ' or (>= (byte)'0' and <= (byte)'9')))
                    return FileFormatValidationResult.Invalid("File does not have a valid IGES start section record.");
            }

            return FileFormatValidationResult.Success;
        }
    }

    // eDrawings documents come in two container formats: modern documents are ZIP archives containing an 'eModel' entry, while legacy documents (and
    // some current exports, e.g. SolidWorks Simulation '.analysis.easm' results) are raw HOOPS Stream Format files, which open with a ';; HSF V<version>'
    // version comment. Both verified against real eDrawings assembly files.
    private sealed class EDrawingsFileFormat(string name, string extension) : FileFormat
    {
        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = [extension];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 9, cancellationToken).ConfigureAwait(false);

            // Legacy container: a HOOPS Stream Format file, which starts with its version comment.
            if (StreamSignatureUtils.StartsWith(header, ";; HSF V"u8) && header.Length > 8 && char.IsAsciiDigit((char)header[8]))
                return FileFormatValidationResult.Success;

            if (!StreamSignatureUtils.StartsWith(header, _zipLocalHeaderSig) && !StreamSignatureUtils.StartsWith(header, _zipEmptySig))
                return FileFormatValidationResult.Invalid($"File does not have a valid {Name} signature (expected a HOOPS Stream Format header or a ZIP container).");

            var (archive, result) = await TryOpenZipAsync(stream, Name, cancellationToken).ConfigureAwait(false);
            if (archive is null)
                return result;

            using (archive)
            {
                if (!HasEntry(archive, "eModel"))
                    return FileFormatValidationResult.Invalid($"ZIP archive is missing required 'eModel' entry (required for {Name}).");
            }

            return FileFormatValidationResult.Success;
        }
    }
}
