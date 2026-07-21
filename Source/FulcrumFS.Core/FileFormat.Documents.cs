namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of built-in document <see cref="FileFormat"/> instances.
/// </content>
public abstract partial class FileFormat
{
    private sealed class PdfFileFormat : FileFormat
    {
        public override string Name => "PDF";

        public override IReadOnlyList<string> Extensions { get; } = [".pdf"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 5, cancellationToken).ConfigureAwait(false);

            // "%PDF-"
            if (!StreamSignatureUtils.StartsWith(header, "%PDF-"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid PDF signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class RtfFileFormat : FileFormat
    {
        public override string Name => "RTF";

        public override IReadOnlyList<string> Extensions { get; } = [".rtf"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 5, cancellationToken).ConfigureAwait(false);

            // "{\rtf"
            if (!StreamSignatureUtils.StartsWith(header, "{\\rtf"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid RTF signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class OleCompoundFileFormat(string name, string extension, OleCompoundReader.OleDocumentType expectedType) : FileFormat
    {
        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = [extension];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 8, cancellationToken).ConfigureAwait(false);

            // Compound File Binary Format signature: D0 CF 11 E0 A1 B1 1A E1.
            if (!StreamSignatureUtils.StartsWith(header, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
                return FileFormatValidationResult.Invalid($"File does not have a valid {Name} (OLE Compound Document) signature.");

            var detected = await OleCompoundReader.DetectAsync(stream, cancellationToken).ConfigureAwait(false);

            if (detected != expectedType)
            {
                return FileFormatValidationResult.Invalid(
                    detected is OleCompoundReader.OleDocumentType.Unknown
                        ? $"File is a valid OLE Compound Document but its contents could not be identified as {Name}."
                        : $"File is a valid OLE Compound Document but its contents identify it as {detected} rather than {Name}.");
            }

            return FileFormatValidationResult.Success;
        }
    }
}
