using Singulink.Enums;

namespace FulcrumFS;

/// <summary>
/// Represents a pipeline for processing files using a series of <see cref="FileProcessor"/> instances.
/// </summary>
public class FileProcessingPipeline
{
    /// <summary>
    /// Gets an empty file process pipeline that does not perform any processing on files.
    /// </summary>
    public static FileProcessingPipeline Empty { get; } = new([]);

    /// <summary>
    /// Gets the collection of file processors that will be used in this pipeline to process files.
    /// </summary>
    public IReadOnlyList<FileProcessor> Processors { get; }

#pragma warning disable SA1623 // Property summary documentation should match accessors

    /// <summary>
    /// Gets or initializes the mode for buffering source streams to temporary repository work files. Default value is <see cref="SourceBufferingMode.Auto"/>.
    /// </summary>
    public SourceBufferingMode SourceBufferingMode
    {
        get;
        init {
            value.ThrowIfNotDefined(nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating whether <see cref="FileSourceUnchangedException"/> should be thrown when the source file remains unchanged after
    /// being processed through the pipeline. Default value is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Setting this property to <see langword="true"/> and catching <see cref="FileSourceUnchangedException"/> can be used to avoiding storing duplicate files
    /// in a repository. For example, if you are adding a thumbnail variant to an image in the repository but the source image is already small enough to be
    /// used as the thumbnail, the processing pipeline may leave the image unchanged, in which case you can reference the source image instead of storing a
    /// duplicate thumbnail file.
    /// </remarks>
    public bool ThrowWhenSourceUnchanged { get; init; }

#pragma warning restore SA1623

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessingPipeline"/> class with the specified file processors.
    /// </summary>
    public FileProcessingPipeline(IEnumerable<FileProcessor> processors)
    {
        Processors = [.. processors];
    }

    internal async Task ExecuteAsync(FileProcessingContext context, bool knownRepoFileSource)
    {
        if ((SourceBufferingMode is SourceBufferingMode.ForceTempCopy && !knownRepoFileSource) ||
            (SourceBufferingMode is SourceBufferingMode.Auto && !context.IsSourceInMemoryOrFile))
        {
            await context.BufferSourceToWorkFileAsync().ConfigureAwait(false);
        }

        for (int i = 0; i < Processors.Count; i++)
        {
            var processor = Processors[i];
            var result = await processor.CallProcessAsync(context).ConfigureAwait(false);

            bool oneStepLeft = i == Processors.Count - 2;
            await context.SetResultAsync(result, oneStepLeft).ConfigureAwait(false);
        }

        if (ThrowWhenSourceUnchanged && !context.HasChanges)
            throw new FileSourceUnchangedException();
    }
}
