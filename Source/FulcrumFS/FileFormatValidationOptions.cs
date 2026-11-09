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
    /// <exception cref="ArgumentException"><paramref name="allowedFormats"/> is empty, contains <see langword="null"/>, or has an extension in more than one
    /// file format. The message lists every problem.</exception>
    public FileFormatValidationOptions(IEnumerable<FileFormat> allowedFormats)
    {
        var list = allowedFormats.ToArray();

        if (list.Length is 0)
            throw new ArgumentException("The value is empty.", nameof(allowedFormats));

        var extToFormat = new Dictionary<string, FileFormat>(StringComparer.Ordinal);
        List<string> problems = [];

        if (list.Any(f => f is null))
            problems.Add("The value contains null.");

        foreach (FileFormat type in list)
        {
            if (type is null)
                continue;

            foreach (string ext in type.Extensions)
            {
                if (extToFormat.TryGetValue(ext, out FileFormat? existing))
                    problems.Add($"The value contains the extension '{ext}' in more than one file format: '{existing.Name}' and '{type.Name}'.");
                else
                    extToFormat.Add(ext, type);
            }
        }

        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(allowedFormats));

        AllowedFormats = list;
        _extensionToFormat = extToFormat;
    }

    internal FileFormat? GetFormatForExtension(string extension)
    {
        return _extensionToFormat.TryGetValue(extension, out var type) ? type : null;
    }

    internal IReadOnlyCollection<string> GetAllExtensions() => _extensionToFormat.Keys;
}
