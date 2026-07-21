namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of MPEG-TS <see cref="FileFormat"/> instances. Detection works by verifying the MPEG-TS sync byte (0x47) appears at the
/// expected packet boundaries for the relevant packet size (188 bytes for .ts; 192 bytes for .m2ts/.mts which have a 4-byte timing prefix).
/// </content>
public abstract partial class FileFormat
{
    private const int MpegTsPacketCheckCount = 4;

    private sealed class MpegTsFileFormat(string name, IReadOnlyList<string> extensions, int packetSize, int syncOffset) : FileFormat
    {
        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = extensions;

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, packetSize * MpegTsPacketCheckCount, cancellationToken).ConfigureAwait(false);

            if (!ValidatePattern(header))
                return FileFormatValidationResult.Invalid($"File does not have a valid {Name} sync byte pattern.");

            return FileFormatValidationResult.Success;
        }

        private bool ValidatePattern(ReadOnlySpan<byte> header)
        {
            for (int i = 0; i < MpegTsPacketCheckCount; i++)
            {
                int offset = (i * packetSize) + syncOffset;
                if (offset >= header.Length)
                    return i > 0;
                if (header[offset] != 0x47)
                    return false;
            }

            return true;
        }
    }
}
