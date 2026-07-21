namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of Matroska/WebM <see cref="FileFormat"/> instances.
/// </content>
public abstract partial class FileFormat
{
    private sealed class EbmlFileFormat(string name, string extension, string expectedDocType) : FileFormat
    {
        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = [extension];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            // Quick signature check before scanning for DocType.
            byte[] sig = await StreamSignatureUtils.ReadHeaderAsync(stream, 4, cancellationToken).ConfigureAwait(false);
            if (!StreamSignatureUtils.StartsWith(sig, [0x1A, 0x45, 0xDF, 0xA3]))
                return FileFormatValidationResult.Invalid($"File does not have a valid EBML signature (required for {Name}).");

            string? docType = await EbmlHeaderReader.ReadDocTypeAsync(stream, cancellationToken).ConfigureAwait(false);

            if (docType is null)
                return FileFormatValidationResult.Invalid($"File does not contain an EBML DocType element (required for {Name}).");

            if (docType != expectedDocType)
                return FileFormatValidationResult.Invalid($"File's EBML DocType '{docType}' does not match expected '{expectedDocType}' for {Name}.");

            return FileFormatValidationResult.Success;
        }
    }
}
