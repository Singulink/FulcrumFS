using System.Diagnostics.CodeAnalysis;

namespace FulcrumFS.Utilities;

/// <summary>
/// Utility class for handling file extensions.
/// </summary>
public static class FileExtension
{
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

        var file = FilePath.ParseRelative("f", PathFormat.Universal, PathOptions.None).WithExtension(extension);
        return file.Extension.ToLowerInvariant();
    }
}
