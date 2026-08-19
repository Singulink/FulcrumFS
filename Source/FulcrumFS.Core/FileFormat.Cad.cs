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
            var content = header.AsSpan();

            // Tolerate an optional UTF-8 BOM and leading whitespace before the ISO 10303-21 header keyword.
            if (StreamSignatureUtils.StartsWith(content, [0xEF, 0xBB, 0xBF]))
                content = content[3..];

            while (content.Length > 0 && content[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                content = content[1..];

            if (!StreamSignatureUtils.StartsWith(content, "ISO-10303-21"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid STEP (ISO 10303-21) header.");

            return FileFormatValidationResult.Success;
        }
    }

    // SolidWorks documents come in two container formats: legacy documents use OLE Compound Documents, while modern documents use a ZIP-like container with
    // a proprietary 20-byte prefix (bytes 4-7 are 00 00 00 04) and nibble-swapped entry names, with the characteristic nibble-swapped 'swXmlContents' entry
    // name appearing near the start of the file (verified against real SolidWorks part and assembly files). Part and assembly documents cannot be reliably
    // distinguished from each other at the container level, so both accept either container type.
    private sealed class SolidWorksFileFormat(string name, string extension) : FileFormat
    {
        // 'swXmlContents' in ASCII with the high/low nibbles of each byte swapped.
        private static readonly byte[] _nibbleSwappedSwXmlContents = [0x37, 0x77, 0x85, 0xD6, 0xC6, 0x34, 0xF6, 0xE6, 0x47, 0x56, 0xE6, 0x47, 0x37];

        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = [extension];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 4096, cancellationToken).ConfigureAwait(false);

            // Modern container: bytes 4-7 are 00 00 00 04 and a nibble-swapped 'swXmlContents' entry name appears near the start of the file.
            if (header.Length >= 8 && header.AsSpan(4, 4).SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x04]) &&
                header.AsSpan().IndexOf(_nibbleSwappedSwXmlContents) >= 0)
            {
                return FileFormatValidationResult.Success;
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
    }

    // Modern eDrawings documents are ZIP archives containing an 'eModel' entry (verified against real eDrawings assembly files).
    private sealed class EDrawingsFileFormat(string name, string extension) : FileFormat
    {
        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = [extension];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
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
