namespace FulcrumFS;

/// <summary>
/// A <see cref="FileProcessor"/> that validates a file's content against its declared extension using a configured set of <see cref="FileFormat"/> instances.
/// The file content is not modified; the processor either passes the file through unchanged or throws a <see cref="FileProcessingException"/> if the
/// content does not match the expected file format.
/// </summary>
public sealed class FileFormatValidationProcessor : FileProcessor
{
    private const int MaxInMemoryCopySize = 20 * 1024 * 1024;

    /// <summary>
    /// Gets the options used to configure this processor.
    /// </summary>
    public FileFormatValidationOptions Options { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<string> AllowedFileExtensions { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileFormatValidationProcessor"/> class with the specified options.
    /// </summary>
    public FileFormatValidationProcessor(FileFormatValidationOptions options)
    {
        Options = options;
        AllowedFileExtensions = [.. options.GetAllExtensions()];
    }

    /// <inheritdoc/>
    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        FileFormat fileFormat = Options.GetFormatForExtension(context.Extension)
            ?? throw new FileProcessingException($"No file format is configured to validate extension '{context.Extension}'.");

        Stream stream = await context.GetSourceAsSeekableStreamAsync(preferInMemory: true, MaxInMemoryCopySize).ConfigureAwait(false);
        stream.Position = 0;

        FileFormatValidationResult result = await fileFormat.ValidateAsync(stream, context.CancellationToken).ConfigureAwait(false);

        if (!result.IsValid)
            throw new FileProcessingException($"File content does not match the expected '{fileFormat.Name}' format: {result.ErrorMessage}");

        stream.Position = 0;
        return FileProcessingResult.Stream(stream, fileFormat.PrimaryExtension, hasChanges: false);
    }
}
