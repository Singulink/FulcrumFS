using System.IO.Compression;

namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of ZIP-based <see cref="FileFormat"/> instances. All of these formats are ZIP archives distinguished by the presence (or
/// content) of specific entries.
/// </content>
public abstract partial class FileFormat
{
    private static readonly byte[] _zipLocalHeaderSig = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] _zipEmptySig = [0x50, 0x4B, 0x05, 0x06];

    private static async ValueTask<(ZipArchive? Archive, FileFormatValidationResult Result)> TryOpenZipAsync(Stream stream, string typeName, CancellationToken cancellationToken)
    {
        byte[] sig = await StreamSignatureUtils.ReadHeaderAsync(stream, 4, cancellationToken).ConfigureAwait(false);

        if (!StreamSignatureUtils.StartsWith(sig, _zipLocalHeaderSig) && !StreamSignatureUtils.StartsWith(sig, _zipEmptySig))
            return (null, FileFormatValidationResult.Invalid($"File does not have a valid ZIP signature (required for {typeName})."));

        stream.Position = 0;

        try
        {
            return (new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true), FileFormatValidationResult.Success);
        }
        catch (InvalidDataException ex)
        {
            return (null, FileFormatValidationResult.Invalid($"File is not a valid ZIP archive (required for {typeName}): {ex.Message}"));
        }
    }

    private static bool HasEntry(ZipArchive archive, string entryName)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed class ZipFileFormat : FileFormat
    {
        public override string Name => "ZIP";

        public override IReadOnlyList<string> Extensions { get; } = [".zip"];

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            var (archive, result) = await TryOpenZipAsync(stream, Name, cancellationToken).ConfigureAwait(false);
            if (archive is null)
                return result;

            using (archive)
            {
                // Accessing Entries forces central directory parse, validating archive structure.
                _ = archive.Entries.Count;
            }

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class OpenXmlFileFormat(string name, string extension, string requiredEntry, string? requiredContentType = null) : FileFormat
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
                var contentTypesEntry = archive.GetEntry("[Content_Types].xml");

                if (contentTypesEntry is null)
                    return FileFormatValidationResult.Invalid($"ZIP archive is missing required '[Content_Types].xml' entry (required for {Name}).");

                if (!HasEntry(archive, requiredEntry))
                    return FileFormatValidationResult.Invalid($"ZIP archive is missing required '{requiredEntry}' entry (required for {Name}).");

                if (requiredContentType is not null)
                {
                    string contentTypes;
                    var entryStream = contentTypesEntry.Open();
                    await using (entryStream.ConfigureAwait(false))
                    using (var reader = new StreamReader(entryStream, System.Text.Encoding.UTF8))
                    {
                        contentTypes = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (!contentTypes.Contains(requiredContentType, StringComparison.OrdinalIgnoreCase))
                        return FileFormatValidationResult.Invalid($"ZIP archive's '[Content_Types].xml' does not declare content type '{requiredContentType}' (required for {Name}).");
                }
            }

            return FileFormatValidationResult.Success;
        }
    }

    private sealed class MimetypeZipFileFormat(string name, string extension, string expectedMimetype) : FileFormat
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
                var mimetypeEntry = archive.GetEntry("mimetype");

                if (mimetypeEntry is null)
                    return FileFormatValidationResult.Invalid($"ZIP archive is missing required 'mimetype' entry (required for {Name}).");

                string mimetype;
                var entryStream = mimetypeEntry.Open();
                await using (entryStream.ConfigureAwait(false))
                using (var reader = new StreamReader(entryStream, System.Text.Encoding.ASCII))
                {
                    mimetype = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
                }

                if (mimetype != expectedMimetype)
                    return FileFormatValidationResult.Invalid($"File's mimetype '{mimetype}' does not match expected '{expectedMimetype}' for {Name}.");
            }

            return FileFormatValidationResult.Success;
        }
    }
}
