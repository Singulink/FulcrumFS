namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of built-in image <see cref="FileFormat"/> instances.
/// </content>
public abstract partial class FileFormat
{
    private sealed class JpegFileFormat : FileFormat
    {
        public override string Name => "JPEG";

        public override IReadOnlyList<string> Extensions { get; } = [".jpg", ".jpeg", ".jfif"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 3, cancellationToken).ConfigureAwait(false);

            // SOI marker (FF D8) + start of next marker (FF).
            if (!StreamSignatureUtils.StartsWith(header, [0xFF, 0xD8, 0xFF]))
                return FileFormatValidationResult.Invalid("File does not have a valid JPEG signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class PngFileFormat : FileFormat
    {
        public override string Name => "PNG";

        public override IReadOnlyList<string> Extensions { get; } = [".png", ".apng"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 8, cancellationToken).ConfigureAwait(false);

            if (!StreamSignatureUtils.StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
                return FileFormatValidationResult.Invalid("File does not have a valid PNG signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class GifFileFormat : FileFormat
    {
        public override string Name => "GIF";

        public override IReadOnlyList<string> Extensions { get; } = [".gif"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 6, cancellationToken).ConfigureAwait(false);

            // "GIF87a" or "GIF89a".
            if (!StreamSignatureUtils.StartsWith(header, "GIF87a"u8) && !StreamSignatureUtils.StartsWith(header, "GIF89a"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid GIF signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class WebPFileFormat : FileFormat
    {
        public override string Name => "WebP";

        public override IReadOnlyList<string> Extensions { get; } = [".webp"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 12, cancellationToken).ConfigureAwait(false);

            // "RIFF" + 4-byte size + "WEBP".
            if (header.Length < 12 || !StreamSignatureUtils.StartsWith(header, "RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid WebP signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class BmpFileFormat : FileFormat
    {
        public override string Name => "BMP";

        public override IReadOnlyList<string> Extensions { get; } = [".bmp", ".bm", ".dip"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 2, cancellationToken).ConfigureAwait(false);

            if (!StreamSignatureUtils.StartsWith(header, "BM"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid BMP signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class TiffFileFormat : FileFormat
    {
        public override string Name => "TIFF";

        public override IReadOnlyList<string> Extensions { get; } = [".tif", ".tiff"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 4, cancellationToken).ConfigureAwait(false);

            // Little-endian (II 2A 00) or big-endian (MM 00 2A).
            if (!StreamSignatureUtils.StartsWith(header, [0x49, 0x49, 0x2A, 0x00]) && !StreamSignatureUtils.StartsWith(header, [0x4D, 0x4D, 0x00, 0x2A]))
                return FileFormatValidationResult.Invalid("File does not have a valid TIFF signature.");

            return FileFormatValidationResult.Success;
        }
    }
}
