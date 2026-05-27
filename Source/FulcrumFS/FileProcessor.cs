using System.Collections.Immutable;

namespace FulcrumFS;

/// <summary>
/// Represents a base class for processing files in a file repository.
/// </summary>
public abstract class FileProcessor : IFileProcessingPipelineProvider, IFileProcessingPipelineSelector
{
    private FileProcessingPipeline? _selfPipeline;

    /// <summary>
    /// Gets the file extensions that this processor allows, including the leading dot (e.g., ".jpg", ".png"), or an empty collection if all file extensions are
    /// allowed.
    /// </summary>
    public abstract IReadOnlyList<string> AllowedFileExtensions { get; }

    /// <summary>
    /// Creates a <see cref="FileProcessingPipeline"/> that contains this processor.
    /// </summary>
    /// <param name="sourceBufferingMode">The mode to use for buffering source streams to temporary repository work files.</param>
    /// <param name="skipWhenSourceUnchanged">
    /// Indicates how an unchanged source file is handled after running through the pipeline. See
    /// <see cref="FileProcessingPipeline.SkipWhenSourceUnchanged"/> for details.
    /// </param>
    public FileProcessingPipeline ToPipeline(SourceBufferingMode sourceBufferingMode = SourceBufferingMode.Auto, bool skipWhenSourceUnchanged = false)
    {
        return new FileProcessingPipeline([this])
        {
            SourceBufferingMode = sourceBufferingMode,
            SkipWhenSourceUnchanged = skipWhenSourceUnchanged,
        };
    }

    /// <summary>
    /// Returns a new <see cref="FileProcessingPipeline"/> containing this processor with the specified variant appended. Sugar for
    /// <c>ToPipeline().WithVariant(variantId, pipeline)</c>.
    /// </summary>
    /// <param name="variantId">The variant ID. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="pipeline">The pipeline used to process the variant.</param>
    public FileProcessingPipeline WithVariant(string variantId, IFileProcessingPipelineProvider pipeline) => ToPipeline().WithVariant(variantId, pipeline);

    /// <inheritdoc/>
    FileProcessingPipeline IFileProcessingPipelineProvider.GetPipeline() => _selfPipeline ??= ToPipeline();

    /// <inheritdoc/>
    FileProcessingPipeline IFileProcessingPipelineSelector.GetPipeline(string extension) => _selfPipeline ??= ToPipeline();

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
