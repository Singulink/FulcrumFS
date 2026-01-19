using System.Diagnostics.CodeAnalysis;
using System.Text;

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

        if (!PathFormat.Universal.IsValidExtension(extension))
            throw new ArgumentException("Invalid file extension.", nameof(extension));

        return extension.ToLowerInvariant();
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
        if (!PathFormat.Universal.IsValidExtension(extension)) return false;
        if (extension.ContainsAnyInRange('A', 'Z')) return false;

        int nonAsciiIndex = extension.IndexOfAnyInRange((char)0x0080, (char)0xFFFF);
        while (nonAsciiIndex >= 0)
        {
            if (char.IsUpper(extension[nonAsciiIndex])) return false;
            extension = extension[(nonAsciiIndex + 1)..];
            nonAsciiIndex = extension.IndexOfAnyInRange((char)0x0080, (char)0xFFFF);
        }

        return true;
    }
}
