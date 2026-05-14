namespace FulcrumFS;

/// <summary>
/// Represents configuration options for a <see cref="FileFormatValidationProcessor"/>.
/// </summary>
public sealed class FileFormatValidationOptions
{
    private readonly Dictionary<string, FileFormat> _extensionToFormat;

    /// <summary>
    /// Gets the allowed file formats for validation.
    /// </summary>
    public IReadOnlyList<FileFormat> AllowedFormats { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileFormatValidationOptions"/> class with the specified allowed file formats.
    /// </summary>
    /// <param name="allowedFormats">The file formats that are allowed by the validation processor. Each extension may only appear in one file format across the
    /// collection.</param>
    public FileFormatValidationOptions(params ReadOnlySpan<FileFormat> allowedFormats) : this((IEnumerable<FileFormat>)allowedFormats.ToArray())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileFormatValidationOptions"/> class with the specified allowed file formats.
    /// </summary>
    /// <param name="allowedFormats">The file formats that are allowed by the validation processor. Each extension may only appear in one file format across the
    /// collection.</param>
    public FileFormatValidationOptions(IEnumerable<FileFormat> allowedFormats)
    {
        var list = allowedFormats.ToArray();

        if (list.Length is 0)
            throw new ArgumentException("At least one file format must be specified.", nameof(allowedFormats));

        var extToFormat = new Dictionary<string, FileFormat>(StringComparer.Ordinal);

        foreach (FileFormat type in list)
        {
            if (type is null)
                throw new ArgumentException("File formats must not be null.", nameof(allowedFormats));

            foreach (string ext in type.Extensions)
            {
                if (extToFormat.TryGetValue(ext, out FileFormat? existing))
                    throw new ArgumentException(
                        $"Extension '{ext}' is associated with multiple file formats: '{existing.Name}' and '{type.Name}'.",
                        nameof(allowedFormats));

                extToFormat.Add(ext, type);
            }
        }

        AllowedFormats = list;
        _extensionToFormat = extToFormat;
    }

    internal FileFormat? GetFormatForExtension(string extension)
    {
        return _extensionToFormat.TryGetValue(extension, out var type) ? type : null;
    }

    internal IReadOnlyCollection<string> GetAllExtensions() => _extensionToFormat.Keys;
}
