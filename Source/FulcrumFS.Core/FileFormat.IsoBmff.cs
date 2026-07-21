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
            return await IsoBmffHeaderValidator.CheckFtypAsync(Name, _acceptedBrands, stream, cancellationToken).ConfigureAwait(false);
        }
    }
}
