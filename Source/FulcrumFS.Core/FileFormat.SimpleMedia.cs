namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of signature-based audio and simple media <see cref="FileFormat"/> instances.
/// </content>
public abstract partial class FileFormat
{
    private sealed class WavFileFormat : FileFormat
    {
        public override string Name => "WAV";

        public override IReadOnlyList<string> Extensions { get; } = [".wav"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 12, cancellationToken).ConfigureAwait(false);

            if (header.Length < 12 || !StreamSignatureUtils.StartsWith(header, "RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid WAV (RIFF/WAVE) signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class Mp3FileFormat : FileFormat
    {
        public override string Name => "MP3";

        public override IReadOnlyList<string> Extensions { get; } = [".mp3"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 3, cancellationToken).ConfigureAwait(false);

            // "ID3" tag at start, or MPEG audio frame sync (FF Ex/Fx where the next 3 bits are 11).
            if (StreamSignatureUtils.StartsWith(header, "ID3"u8))
                return FileFormatValidationResult.Success;

            if (header.Length >= 2 && header[0] is 0xFF && (header[1] & 0xE0) is 0xE0)
                return FileFormatValidationResult.Success;

            return FileFormatValidationResult.Invalid("File does not have a valid MP3 signature (missing ID3 tag or MPEG audio frame sync).");
        }
    }

    private sealed class FlacFileFormat : FileFormat
    {
        public override string Name => "FLAC";

        public override IReadOnlyList<string> Extensions { get; } = [".flac"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 4, cancellationToken).ConfigureAwait(false);

            if (!StreamSignatureUtils.StartsWith(header, "fLaC"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid FLAC signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class OggFileFormat : FileFormat
    {
        public override string Name => "Ogg";

        public override IReadOnlyList<string> Extensions { get; } = [".ogg", ".oga", ".ogv", ".opus"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 4, cancellationToken).ConfigureAwait(false);

            if (!StreamSignatureUtils.StartsWith(header, "OggS"u8))
                return FileFormatValidationResult.Invalid("File does not have a valid Ogg signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class AviFileFormat : FileFormat
    {
        public override string Name => "AVI";

        public override IReadOnlyList<string> Extensions { get; } = [".avi"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 12, cancellationToken).ConfigureAwait(false);

            if (header.Length < 12 || !StreamSignatureUtils.StartsWith(header, "RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("AVI "u8))
                return FileFormatValidationResult.Invalid("File does not have a valid AVI (RIFF/AVI) signature.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class MpegFileFormat : FileFormat
    {
        public override string Name => "MPEG-PS";

        public override IReadOnlyList<string> Extensions { get; } = [".mpg", ".mpeg"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 4, cancellationToken).ConfigureAwait(false);

            // MPEG-PS pack header start code: 00 00 01 BA. Also accept video sequence start (00 00 01 B3).
            if (!StreamSignatureUtils.StartsWith(header, [0x00, 0x00, 0x01, 0xBA]) && !StreamSignatureUtils.StartsWith(header, [0x00, 0x00, 0x01, 0xB3]))
                return FileFormatValidationResult.Invalid("File does not have a valid MPEG-PS signature.");

            return FileFormatValidationResult.Success;
        }
    }
}
