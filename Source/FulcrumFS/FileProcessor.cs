using System.Collections.Immutable;

namespace FulcrumFS;

/// <summary>
/// Represents a base class for processing files in a file repository.
/// </summary>
public abstract class FileProcessor
{
    private readonly ImmutableArray<string> _allowedFileExtensions;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessor"/> class with the specified allowed file extensions.
    /// </summary>
    /// <param name="allowedFileExtensions">The file extensions that this processor can handle, including the leading dot (e.g., ".jpg", ".png"), or an empty
    /// collection to allow all file extensions.</param>
    protected FileProcessor(IEnumerable<string> allowedFileExtensions)
    {
        _allowedFileExtensions = allowedFileExtensions
            .Select(FileRepo.NormalizeExtension)
            .ToImmutableArray();
    }

    /// <summary>
    /// Processes the file represented by the specified <see cref="FileProcessContext"/> asynchronously.
    /// </summary>
    protected abstract Task<FileProcessResult> ProcessAsync(FileProcessContext context, CancellationToken cancellationToken);

    internal Task<FileProcessResult> ProcessAsyncInternal(FileProcessContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_allowedFileExtensions.Length is 0 || _allowedFileExtensions.Contains(context.Extension, StringComparer.Ordinal))
            return ProcessAsync(context, cancellationToken);
        else
            throw new InvalidOperationException($"Extension '{context.Extension}' is not allowed by this processor.");
    }
}
