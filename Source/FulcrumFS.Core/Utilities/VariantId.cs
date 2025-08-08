using System.ComponentModel;

namespace FulcrumFS.Utilities;

/// <summary>
/// Utility class for handling variant IDs.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class VariantId
{
    /// <summary>
    /// Normalizes a variant ID by converting it to lowercase while ensuring it contains only valid characters.
    /// </summary>
    public static string Normalize(string variantId)
    {
        if (variantId.Length is 0)
            throw new ArgumentException("Variant ID cannot be empty.", nameof(variantId));

        if (variantId.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetter(c) && c is not ('-' or '_')))
            throw new ArgumentException("Variant ID must contain only ASCII letters, digits, hyphens and underscores.", nameof(variantId));

        return variantId.ToLowerInvariant();
    }
}
