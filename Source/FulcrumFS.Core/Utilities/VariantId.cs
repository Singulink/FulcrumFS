using System.Buffers;

namespace FulcrumFS.Utilities;

/// <summary>
/// Utility class for handling variant IDs.
/// </summary>
public static class VariantId
{
    /// <summary>
    /// The set of valid characters for a variant ID.
    /// </summary>
    private static readonly SearchValues<char> _validVariantIdChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");

    /// <summary>
    /// The set of valid characters for a normalized variant ID.
    /// </summary>
    private static readonly SearchValues<char> _validNormalizedVariantIdChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>
    /// Normalizes a variant ID by converting it to lowercase while ensuring it contains only valid characters.
    /// </summary>
    public static string Normalize(string variantId)
    {
        if (variantId.Length is 0)
            throw new ArgumentException("Variant ID cannot be empty.", nameof(variantId));

        if (variantId.ContainsAnyExcept(_validVariantIdChars))
            throw new ArgumentException("Variant ID must contain only ASCII letters, digits, hyphens and underscores.", nameof(variantId));

        return variantId.ToLowerInvariant();
    }

    /// <summary>
    /// Checks if the given variant ID is valid and normalized.
    /// </summary>
    public static bool IsValidAndNormalized(ReadOnlySpan<char> variantId)
    {
        if (variantId.Length is 0)
            return false;

        return !variantId.ContainsAnyExcept(_validNormalizedVariantIdChars);
    }
}
