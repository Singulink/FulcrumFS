using System.Collections.Immutable;

namespace FulcrumFS;

/// <summary>
/// Represents a base class for processing files in a file repository.
/// </summary>
public abstract class FileProcessor
{
    private FileProcessPipeline? _singleProcessorPipeline;

    /// <summary>
    /// Gets the file extensions that this processor allows, including the leading dot (e.g., ".jpg", ".png"), or an empty collection if all file extensions are
    /// allowed.
    /// </summary>
    public abstract IReadOnlyList<string> AllowedFileExtensions { get; }

    internal FileProcessPipeline SingleProcessorPipeline => _singleProcessorPipeline ??= new([this]);

    /// <summary>
    /// Processes the file represented by the specified <see cref="FileProcessContext"/>.
    /// </summary>
    protected abstract Task<FileProcessResult> ProcessAsync(FileProcessContext context);

    internal async Task<FileProcessResult> CallProcessAsync(FileProcessContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (AllowedFileExtensions.Count is not 0 && !AllowedFileExtensions.Contains(context.Extension, StringComparer.Ordinal))
        {
            string allowedExtensions = string.Join(", ", AllowedFileExtensions);
            throw new FileProcessException($"Extension '{context.Extension}' is not allowed. Allowed extensions: {allowedExtensions}.");
        }

        try
        {
            return await ProcessAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not FileProcessException)
        {
            throw new FileProcessException($"An error occurred while processing the file: {ex.Message}", ex);
        }
    }
}
