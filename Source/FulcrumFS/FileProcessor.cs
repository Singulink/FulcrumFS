using System.Collections.Immutable;

namespace FulcrumFS;

/// <summary>
/// Represents a base class for processing files in a file repository.
/// </summary>
public abstract class FileProcessor
{
    /// <summary>
    /// Gets the file extensions that this processor allows, including the leading dot (e.g., ".jpg", ".png"), or an empty collection if all file extensions are
    /// allowed.
    /// </summary>
    public abstract IReadOnlyList<string> AllowedFileExtensions { get; }

    /// <summary>
    /// Creates a <see cref="FileProcessingPipeline"/> that contains this processor.
    /// </summary>
    /// <param name="sourceBufferingMode">The mode to use for buffering source streams to temporary repository work files.</param>
    /// <param name="throwWhenSourceUnchanged">
    /// Indicates whether <see cref="FileSourceUnchangedException"/> should be thrown when the source file remains unchanged after being processed through the
    /// pipeline. See <see cref="FileProcessingPipeline.ThrowWhenSourceUnchanged"/> for details.
    /// </param>
    public FileProcessingPipeline ToPipeline(SourceBufferingMode sourceBufferingMode = SourceBufferingMode.Auto, bool throwWhenSourceUnchanged = false)
    {
        return new FileProcessingPipeline([this])
        {
            SourceBufferingMode = sourceBufferingMode,
            ThrowWhenSourceUnchanged = throwWhenSourceUnchanged,
        };
    }

    /// <summary>
    /// Processes the file represented by the specified <see cref="FileProcessingContext"/>.
    /// </summary>
    protected abstract Task<FileProcessingResult> ProcessAsync(FileProcessingContext context);

    internal async Task<FileProcessingResult> CallProcessAsync(FileProcessingContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (AllowedFileExtensions.Count is not 0 && !AllowedFileExtensions.Contains(context.Extension, StringComparer.Ordinal))
        {
            string allowedExtensions = string.Join(", ", AllowedFileExtensions);
            throw new FileProcessingException($"Extension '{context.Extension}' is not allowed. Allowed extensions: {allowedExtensions}");
        }

        try
        {
            return await ProcessAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not FileProcessingException)
        {
            throw new FileProcessingException($"An error occurred while processing the file: {ex.Message}", ex);
        }
    }
}
