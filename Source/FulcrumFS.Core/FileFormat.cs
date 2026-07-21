namespace FulcrumFS;

/// <summary>
/// Represents a file format that can be used to validate that a file's content matches its declared extension.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="FileFormat"/> represents one specific file format with a fixed set of extensions. The base library provides built-in singletons for common
/// formats accessible via static properties (<see cref="Jpeg"/>, <see cref="Png"/>, <see cref="Pdf"/>, etc.).</para>
/// <para>
/// Content-agnostic and text-based file formats can be created using the factory methods (<see cref="AnyContent(ReadOnlySpan{string})"/>, <see
/// cref="TextAscii(ReadOnlySpan{string})"/>, <see cref="TextUnicode(ReadOnlySpan{string})"/>, <see cref="TextEncoding(System.Text.Encoding,
/// ReadOnlySpan{string})"/>) where the caller specifies the applicable extensions.</para>
/// <para>
/// Custom file formats can be created by deriving from <see cref="FileFormat"/> and implementing <see cref="ValidateAsync(Stream, CancellationToken)"/>.</para>
/// <para>
/// <see cref="FileFormat"/> is independent of the FulcrumFS repository infrastructure and can be used standalone â€” for example, to validate user uploads in a
/// front-end application before sending them to a service that hosts a FulcrumFS repository.</para>
/// </remarks>
public abstract partial class FileFormat
{
    /// <summary>
    /// Gets the name of the file format (e.g., "JPEG", "PDF", "TextUnicode").
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the file extensions associated with this file format (including the leading dot, e.g., ".jpg").
    /// </summary>
    public abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Gets the primary file extension associated with this file format (including the leading dot). This is the first extension in <see cref="Extensions"/>
    /// and is the extension that files of this format are written with when added to a FulcrumFS repository.
    /// </summary>
    public string PrimaryExtension => Extensions[0];

    /// <summary>
    /// Initializes a new instance of the <see cref="FileFormat"/> class.
    /// </summary>
    protected FileFormat()
    {
    }

    /// <summary>
    /// Validates the content of the specified stream against this file format. The stream must be seekable and positioned at 0 at the start of this call.
    /// </summary>
    /// <param name="stream">A seekable stream positioned at 0, containing the file's content to validate.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="FileFormatValidationResult"/> indicating whether validation succeeded or failed (with an error message).</returns>
    /// <remarks>
    /// Implementations are free to leave the stream in any position after returning. The caller is responsible for resetting the position if needed.
    /// </remarks>
    public abstract ValueTask<FileFormatValidationResult> ValidateAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
