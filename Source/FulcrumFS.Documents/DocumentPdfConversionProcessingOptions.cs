namespace FulcrumFS.Documents;

#pragma warning disable SA1513 // Closing brace should be followed by blank line (fires erroneously on property initializers after init accessors)
#pragma warning disable SA1623 // Property summary documentation should match accessors

/// <summary>
/// <para>
/// Specifies the options for converting documents to PDF with a <see cref="DocumentPdfConversionProcessor" />.</para>
/// <para>
/// The processor accepts source files with extensions belonging to the formats specified in <see cref="SourceFormats" /> and produces a PDF rendering of the
/// document.</para>
/// </summary>
public sealed record DocumentPdfConversionProcessingOptions
{
    /// <summary>
    /// Gets an options instance with standard document PDF conversion settings - accepts all the default source formats.
    /// </summary>
    public static DocumentPdfConversionProcessingOptions Standard { get; } = new();

    /// <summary>
    /// <para>
    /// Gets or initializes the document formats that are accepted as conversion sources, which determines the allowed source file extensions.</para>
    /// <para>
    /// Default is the full set of built-in word processing, spreadsheet and presentation formats: <see cref="FileFormat.Doc" />, <see cref="FileFormat.Docx"
    /// />, <see cref="FileFormat.Xls" />, <see cref="FileFormat.Xlsx" />, <see cref="FileFormat.Xlsm" />, <see cref="FileFormat.Ppt" />, <see
    /// cref="FileFormat.Pptx" />, <see cref="FileFormat.Odt" />, <see cref="FileFormat.Ods" />, <see cref="FileFormat.Odp" /> and <see cref="FileFormat.Rtf"
    /// />.</para>
    /// <para>
    /// Note: this does not validate the content of source files - chain a <see cref="FileFormatValidationProcessor" /> before this processor if source
    /// content validation is needed.</para>
    /// </summary>
    public IReadOnlyList<FileFormat> SourceFormats
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Count is 0)
                throw new ArgumentException("At least one source format must be specified.", nameof(value));

            field = [.. value];
        }
    } = [
        FileFormat.Doc,
        FileFormat.Docx,
        FileFormat.Xls,
        FileFormat.Xlsx,
        FileFormat.Xlsm,
        FileFormat.Ppt,
        FileFormat.Pptx,
        FileFormat.Odt,
        FileFormat.Ods,
        FileFormat.Odp,
        FileFormat.Rtf,
    ];
}
