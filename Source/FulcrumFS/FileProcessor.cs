using System.Collections.Immutable;

namespace FulcrumFS;

/// <summary>
/// Represents a base class for processing files in a file repository.
/// </summary>
public abstract class FileProcessor
{
    private readonly ImmutableArray<string> _allowedFileExtensions;

    /// <summary>
    /// Gets the file extensions that this processor allows, including the leading dot (e.g., ".jpg", ".png"), or an empty collection if all file extensions are
    /// allowed.
    /// </summary>
    public IList<string> AllowedFileExtensions => _allowedFileExtensions;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessor"/> class with the specified allowed file extensions.
    /// </summary>
    /// <param name="allowedFileExtensions">The file extensions that this processor can handle, including the leading dot (e.g., ".jpg", ".png"), or an empty
    /// collection to allow all file extensions.</param>
    protected FileProcessor(IEnumerable<string> allowedFileExtensions)
    {
        _allowedFileExtensions = allowedFileExtensions
            .Select(FileExtension.Normalize)
            .ToImmutableArray();
    }

    /// <summary>
    /// Processes the file represented by the specified <see cref="FileProcessContext"/>.
    /// </summary>
    protected abstract Task<FileProcessResult> ProcessAsync(FileProcessContext context, CancellationToken cancellationToken);

    internal Task<FileProcessResult> ProcessAsyncInternal(FileProcessContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_allowedFileExtensions.Length is not 0 && !_allowedFileExtensions.Contains(context.Extension, StringComparer.Ordinal))
        {
            string allowedExtensions = string.Join(", ", _allowedFileExtensions);
            throw new FileProcessException($"Extension '{context.Extension}' is not allowed. Allowed extensions: {allowedExtensions}.");
        }

        try
        {
            return ProcessAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not FileProcessException)
        {
            throw new FileProcessException("An error occurred while processing the image.", ex);
        }
    }
}
