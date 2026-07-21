using System.Buffers;
using System.Text;

namespace FulcrumFS;

#pragma warning disable SA1124 // Do not use regions

/// <content>
/// Contains the factory methods for content-agnostic and text-based file formats.
/// </content>
public abstract partial class FileFormat
{
    #region Factory Methods

    /// <summary>
    /// Creates a <see cref="FileFormat"/> that accepts any content for the specified extensions. No content validation is performed beyond ensuring the file's
    /// extension is one of the specified extensions.
    /// </summary>
    /// <param name="extensions">The file extensions (including the leading dot, e.g., ".log") this file format applies to. Must contain at least one entry.
    /// The first extension is the <see cref="PrimaryExtension"/> that all other extensions are normalized to.</param>
    public static FileFormat AnyContent(params ReadOnlySpan<string> extensions) => new AnyContentFileFormat(extensions);

    /// <summary>
    /// Creates a <see cref="FileFormat"/> that validates files are valid ASCII text for the specified extensions. All bytes in the file must be in the range
    /// [0, 127] (i.e. <see cref="Ascii.IsValid(ReadOnlySpan{byte})"/> must return <see langword="true"/>).
    /// </summary>
    /// <param name="extensions">The file extensions (including the leading dot, e.g., ".txt") this file format applies to. Must contain at least one entry.
    /// The first extension is the <see cref="PrimaryExtension"/> that all other extensions are normalized to.</param>
    public static FileFormat TextAscii(params ReadOnlySpan<string> extensions) => new TextAsciiFileFormat(extensions);

    /// <summary>
    /// Creates a <see cref="FileFormat"/> that validates files are valid Unicode text in any encoding supported by <see cref="UnicodeEncodings.All"/>, for the
    /// specified extensions.
    /// </summary>
    /// <param name="extensions">The file extensions (including the leading dot, e.g., ".txt", ".md") this file format applies to. Must contain at least one entry.
    /// The first extension is the <see cref="PrimaryExtension"/> that all other extensions are normalized to.</param>
    public static FileFormat TextUnicode(params ReadOnlySpan<string> extensions) => TextUnicode(UnicodeEncodings.All, extensions);

    /// <summary>
    /// Creates a <see cref="FileFormat"/> that validates files are valid Unicode text in one of the specified encodings, for the specified extensions.
    /// </summary>
    /// <remarks>
    /// Files with a UTF-8, UTF-16 LE, UTF-16 BE, UTF-32 LE or UTF-32 BE BOM are validated against the encoding indicated by the BOM (which must be one of the
    /// <paramref name="allowedEncodings"/>). Files without a BOM are validated as UTF-8 if <see cref="UnicodeEncodings.Utf8"/> is included in <paramref
    /// name="allowedEncodings"/>; otherwise validation fails.
    /// </remarks>
    /// <param name="allowedEncodings">The Unicode encodings that are accepted. Must specify at least one encoding.</param>
    /// <param name="extensions">The file extensions (including the leading dot) this file format applies to. Must contain at least one entry.
    /// The first extension is the <see cref="PrimaryExtension"/> that all other extensions are normalized to.</param>
    public static FileFormat TextUnicode(UnicodeEncodings allowedEncodings, params ReadOnlySpan<string> extensions)
    {
        if (allowedEncodings == UnicodeEncodings.None)
            throw new ArgumentException("At least one Unicode encoding must be specified.", nameof(allowedEncodings));

        return new TextUnicodeFileFormat(allowedEncodings, extensions);
    }

    /// <summary>
    /// Creates a <see cref="FileFormat"/> that validates files decode successfully using the specified <see cref="Encoding"/>, for the specified extensions.
    /// </summary>
    /// <param name="encoding">The encoding to validate against. The encoding will be used with strict decoding enabled, so invalid byte sequences will cause
    /// validation to fail.</param>
    /// <param name="extensions">The file extensions (including the leading dot) this file format applies to. Must contain at least one entry.
    /// The first extension is the <see cref="PrimaryExtension"/> that all other extensions are normalized to.</param>
    public static FileFormat TextEncoding(Encoding encoding, params ReadOnlySpan<string> extensions)
    {
        return new TextEncodingFileFormat(encoding, extensions);
    }

    #endregion

    #region Implementations

    private abstract class FactoryFileFormat : FileFormat
    {
        public override IReadOnlyList<string> Extensions { get; }

        protected FactoryFileFormat(ReadOnlySpan<string> extensions)
        {
            if (extensions.Length is 0)
                throw new ArgumentException("At least one extension must be specified.", nameof(extensions));

            string[] list = new string[extensions.Length];
            for (int i = 0; i < extensions.Length; i++)
                list[i] = FileExtension.Normalize(extensions[i]);

            Extensions = list;
        }
    }

    private sealed class AnyContentFileFormat : FactoryFileFormat
    {
        public override string Name => "AnyContent";

        public AnyContentFileFormat(ReadOnlySpan<string> extensions) : base(extensions) { }

        public override ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FileFormatValidationResult.Success);
    }

    private sealed class TextAsciiFileFormat : FactoryFileFormat
    {
        public override string Name => "TextAscii";

        public TextAsciiFileFormat(ReadOnlySpan<string> extensions) : base(extensions) { }

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            const int BufferSize = 64 * 1024;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);

                    if (read is 0)
                        return FileFormatValidationResult.Success;

                    if (!Ascii.IsValid(buffer.AsSpan(0, read)))
                        return FileFormatValidationResult.Invalid("File contains bytes outside the valid ASCII range [0, 127].");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private sealed class TextUnicodeFileFormat : FactoryFileFormat
    {
        private readonly UnicodeEncodings _allowedEncodings;

        public override string Name => "TextUnicode";

        public TextUnicodeFileFormat(UnicodeEncodings allowedEncodings, ReadOnlySpan<string> extensions) : base(extensions)
        {
            _allowedEncodings = allowedEncodings;
        }

        private static Encoding StrictUtf8 => field ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static Encoding StrictUtf16Le => field ??= new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

        private static Encoding StrictUtf16Be => field ??= new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

        private static Encoding StrictUtf32Le => field ??= new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);

        private static Encoding StrictUtf32Be => field ??= new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            // Read up to 4 bytes to detect BOM (UTF-32 BOMs are 4 bytes).
            byte[] header = new byte[4];
            int headerRead = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

            var (detectedEncoding, bomLength, encodingFlag) = DetectBom(header.AsSpan(0, headerRead));

            if (detectedEncoding is null)
            {
                // No BOM - must validate as UTF-8 if allowed.
                if ((_allowedEncodings & UnicodeEncodings.Utf8) is 0)
                    return FileFormatValidationResult.Invalid("File does not have a BOM and UTF-8 is not in the allowed Unicode encodings.");

                stream.Position = 0;
                return await ValidateWithDecoderAsync(stream, StrictUtf8, cancellationToken).ConfigureAwait(false);
            }

            if ((_allowedEncodings & encodingFlag) is 0)
                return FileFormatValidationResult.Invalid($"File has a {DescribeEncoding(encodingFlag)} BOM which is not in the allowed Unicode encodings.");

            // Validate remaining content with strict decoder.
            stream.Position = bomLength;
            return await ValidateWithDecoderAsync(stream, detectedEncoding, cancellationToken).ConfigureAwait(false);
        }

        private static (Encoding? Encoding, int BomLength, UnicodeEncodings EncodingFlag) DetectBom(ReadOnlySpan<byte> header)
        {
            // UTF-32 LE BOM is FF FE 00 00, which starts with the UTF-16 LE BOM, so check UTF-32 BOMs first.

            if (header.Length >= 4)
            {
                if (header[0] is 0xFF && header[1] is 0xFE && header[2] is 0x00 && header[3] is 0x00)
                    return (StrictUtf32Le, 4, UnicodeEncodings.Utf32Le);

                if (header[0] is 0x00 && header[1] is 0x00 && header[2] is 0xFE && header[3] is 0xFF)
                    return (StrictUtf32Be, 4, UnicodeEncodings.Utf32Be);
            }

            if (header.Length >= 3 && header[0] is 0xEF && header[1] is 0xBB && header[2] is 0xBF)
                return (StrictUtf8, 3, UnicodeEncodings.Utf8);

            if (header.Length >= 2)
            {
                if (header[0] is 0xFF && header[1] is 0xFE)
                    return (StrictUtf16Le, 2, UnicodeEncodings.Utf16Le);

                if (header[0] is 0xFE && header[1] is 0xFF)
                    return (StrictUtf16Be, 2, UnicodeEncodings.Utf16Be);
            }

            return (null, 0, default);
        }

        private static string DescribeEncoding(UnicodeEncodings flag) => flag switch
        {
            UnicodeEncodings.Utf8 => "UTF-8",
            UnicodeEncodings.Utf16Le => "UTF-16 LE",
            UnicodeEncodings.Utf16Be => "UTF-16 BE",
            UnicodeEncodings.Utf32Le => "UTF-32 LE",
            UnicodeEncodings.Utf32Be => "UTF-32 BE",
            _ => flag.ToString(),
        };

        private static async ValueTask<FileFormatValidationResult> ValidateWithDecoderAsync(Stream stream, Encoding encoding, CancellationToken cancellationToken)
        {
            var decoder = encoding.GetDecoder();
            const int BufferSize = 16 * 1024;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            char[] charBuffer = ArrayPool<char>.Shared.Rent(BufferSize);

            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    bool flush = read is 0;
                    _ = decoder.GetChars(buffer.AsSpan(0, read), charBuffer, flush);

                    if (flush)
                        return FileFormatValidationResult.Success;
                }
            }
            catch (DecoderFallbackException ex)
            {
                return FileFormatValidationResult.Invalid($"File could not be decoded as {encoding.WebName}: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }
    }

    private sealed class TextEncodingFileFormat : FactoryFileFormat
    {
        private readonly Encoding _encoding;

        public override string Name => field ??= $"TextEncoding({_encoding.WebName})";

        public TextEncodingFileFormat(Encoding encoding, ReadOnlySpan<string> extensions) : base(extensions)
        {
            // Clone the encoding with throw-on-invalid behavior.
            _encoding = Encoding.GetEncoding(encoding.WebName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        public override async ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken)
        {
            var decoder = _encoding.GetDecoder();
            const int BufferSize = 16 * 1024;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            char[] charBuffer = ArrayPool<char>.Shared.Rent(BufferSize * 2);

            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    bool flush = read is 0;
                    _ = decoder.GetChars(buffer.AsSpan(0, read), charBuffer, flush);

                    if (flush)
                        return FileFormatValidationResult.Success;
                }
            }
            catch (DecoderFallbackException ex)
            {
                return FileFormatValidationResult.Invalid($"File could not be decoded as {_encoding.WebName}: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }
    }

    #endregion
}
