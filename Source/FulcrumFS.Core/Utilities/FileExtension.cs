using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace FulcrumFS.Utilities;

/// <summary>
/// Utility class for handling file extensions.
/// </summary>
public static class FileExtension
{
    private static readonly IRelativeFilePath _dummyFile = FilePath.ParseRelative("f", PathFormat.Universal, PathOptions.None);

    /// <summary>
    /// Returns a validated and normalized file extension from the given extension string. Extension must either be empty or start with a dot (e.g.,
    /// <c>".jpg"</c>, <c>".png"</c>).
    /// </summary>
    /// <param name="extension">The extension to normalize. If <see langword="null"/> or empty, an empty string is returned.</param>
    [return: NotNullIfNotNull(nameof(extension))]
    public static string Normalize(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return string.Empty;

        var file = _dummyFile.WithExtension(extension);
        return file.Extension.ToLowerInvariant();
    }

    /// <summary>
    /// Checks if the given file extension is valid and normalized.
    /// </summary>
    public static bool IsValidAndNormalized(string extension) => IsValidAndNormalized(extension.AsSpan());

    /// <summary>
    /// Checks if the given file extension is valid and normalized.
    /// </summary>
    public static bool IsValidAndNormalized(ReadOnlySpan<char> extension)
    {
        if (extension.Length is 0)
            return true;

        if (extension[0] is not '.')
            return false;

        foreach (char c in extension[1..])
        {
            if (char.IsUpper(c))
                return false;
        }

        return true;
    }
}
