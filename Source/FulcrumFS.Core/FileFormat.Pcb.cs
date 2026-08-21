using System.Text;
using FulcrumFS.Utilities;

namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of built-in PCB manufacturing <see cref="FileFormat"/> instances. These are loose
/// text formats without hard signatures, so validation checks for the mandatory commands/structure their
/// specifications require near the start of the file.
/// </content>
public abstract partial class FileFormat
{
    private sealed class GerberFileFormat : FileFormat
    {
        public override string Name => "Gerber";

        public override IReadOnlyList<string> Extensions { get; } = [".gbr"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 4096, cancellationToken).ConfigureAwait(false);
            string text = Encoding.Latin1.GetString(header);

            // RS-274X requires a format specification (%FS) and unit mode (%MO) command in the file header.
            if (!text.Contains("%FS", StringComparison.Ordinal) || !text.Contains("%MO", StringComparison.Ordinal))
                return FileFormatValidationResult.Invalid("File does not contain the mandatory Gerber (RS-274X) format specification and unit mode commands.");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class GerberJobFileFormat : FileFormat
    {
        public override string Name => "Gerber Job";

        public override IReadOnlyList<string> Extensions { get; } = [".gbrjob"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 4096, cancellationToken).ConfigureAwait(false);
            var content = StreamSignatureUtils.SkipBomAndWhitespace(header);

            // Gerber job files are JSON objects with a mandatory top-level "Header" object.
            if (content.Length is 0 || content[0] is not (byte)'{' || !Encoding.Latin1.GetString(content).Contains("\"Header\"", StringComparison.Ordinal))
                return FileFormatValidationResult.Invalid("File is not a valid Gerber job file (JSON object with a 'Header' section).");

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class ExcellonFileFormat : FileFormat
    {
        public override string Name => "Excellon";

        public override IReadOnlyList<string> Extensions { get; } = [".drl"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await StreamSignatureUtils.ReadHeaderAsync(stream, 1024, cancellationToken).ConfigureAwait(false);
            string[] lines = Encoding.Latin1.GetString(header).Split('\n');

            // Excellon drill files begin their header with an M48 command, optionally preceded by ';' comment lines.
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                if (line.Length is 0 || line.StartsWith(';'))
                    continue;

                return line.StartsWith("M48", StringComparison.OrdinalIgnoreCase)
                    ? FileFormatValidationResult.Success
                    : FileFormatValidationResult.Invalid("File does not start with a valid Excellon drill file header (M48).");
            }

            return FileFormatValidationResult.Invalid("File does not start with a valid Excellon drill file header (M48).");
        }
    }
}
