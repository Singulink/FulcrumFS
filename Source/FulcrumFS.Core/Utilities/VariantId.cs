namespace FulcrumFS.Utilities;

/// <summary>
/// Utility class for handling variant IDs.
/// </summary>
public static class VariantId
{
    /// <summary>
    /// Normalizes a variant ID by converting it to lowercase while ensuring it contains only valid characters.
    /// </summary>
    public static string Normalize(string variantId)
    {
        if (variantId.Length is 0)
            throw new ArgumentException("Variant ID cannot be empty.", nameof(variantId));

        foreach (char c in variantId)
        {
            if (!char.IsAsciiDigit(c) && !char.IsAsciiLetter(c) && c is not ('-' or '_'))
                throw new ArgumentException("Variant ID must contain only ASCII letters, digits, hyphens and underscores.", nameof(variantId));
        }

        return variantId.ToLowerInvariant();
    }

    /// <summary>
    /// Checks if the given variant ID is valid and normalized.
    /// </summary>
    public static bool IsValidAndNormalized(ReadOnlySpan<char> variantId)
    {
        if (variantId.Length is 0)
            return false;

        foreach (char c in variantId)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('-' or '_'))
                return false;
        }

        return true;
    }
}
