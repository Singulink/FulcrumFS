namespace FulcrumFS;

/// <summary>
/// Specifies the Unicode encodings accepted by <see cref="FileFormat.TextUnicode(UnicodeEncodings, ReadOnlySpan{string})"/>.
/// </summary>
[Flags]
public enum UnicodeEncodings
{
    /// <summary>
    /// No encodings.
    /// </summary>
    None = 0,

    /// <summary>
    /// UTF-8 encoding (with or without BOM). When BOM is absent, the file is validated as UTF-8.
    /// </summary>
    Utf8 = 1,

    /// <summary>
    /// UTF-16 little-endian encoding (BOM required).
    /// </summary>
    Utf16Le = 1 << 1,

    /// <summary>
    /// UTF-16 big-endian encoding (BOM required).
    /// </summary>
    Utf16Be = 1 << 2,

    /// <summary>
    /// UTF-32 little-endian encoding (BOM required).
    /// </summary>
    Utf32Le = 1 << 3,

    /// <summary>
    /// UTF-32 big-endian encoding (BOM required).
    /// </summary>
    Utf32Be = 1 << 4,

    /// <summary>
    /// Both UTF-16 little-endian and big-endian encodings.
    /// </summary>
    AllUtf16 = Utf16Le | Utf16Be,

    /// <summary>
    /// Both UTF-32 little-endian and big-endian encodings.
    /// </summary>
    AllUtf32 = Utf32Le | Utf32Be,

    /// <summary>
    /// All supported Unicode encodings.
    /// </summary>
    All = Utf8 | AllUtf16 | AllUtf32,
}
