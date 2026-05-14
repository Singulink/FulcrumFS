using System.Collections.Frozen;

namespace FulcrumFS;

#pragma warning disable SA1601 // Partial elements should be documented

/// <content>
/// Contains the implementations of ISO Base Media File Format (ISOBMFF) <see cref="FileFormat"/> instances. These types share a common <c>ftyp</c> box
/// structure and are differentiated by the major brand and compatible brands listed in that box.
/// </content>
public abstract partial class FileFormat
{
    private sealed class IsoBmffFileFormat(string name, IReadOnlyList<string> extensions, ReadOnlySpan<string> acceptedBrands) : FileFormat
    {
        private readonly FrozenSet<string> _acceptedBrands = acceptedBrands.ToArray().ToFrozenSet(StringComparer.Ordinal);

        public override string Name { get; } = name;

        public override IReadOnlyList<string> Extensions { get; } = extensions;

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            IsoBmffHeaderReader.FtypInfo? ftyp = await IsoBmffHeaderReader.ReadFtypAsync(stream, cancellationToken).ConfigureAwait(false);

            if (ftyp is null)
                return FileFormatValidationResult.Invalid($"File does not contain a valid ISOBMFF 'ftyp' box (required for {Name}).");

            if (!IsoBmffHeaderReader.BrandsMatch(ftyp.Value, _acceptedBrands))
            {
                return FileFormatValidationResult.Invalid(
                    $"File's major brand '{ftyp.Value.MajorBrand}' and compatible brands do not match any accepted brand for {Name}.");
            }

            return FileFormatValidationResult.Success;
        }
    }
}
