using System.Collections.Immutable;

namespace FulcrumFS;

/// <summary>
/// Represents a pipeline for processing files using a series of <see cref="FileProcessor"/> instances.
/// </summary>
public class FileProcessPipeline
{
    /// <summary>
    /// Gets the collection of file processors that will be used in this pipeline to process files.
    /// </summary>
    public ImmutableArray<FileProcessor> Processors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessPipeline"/> class with the specified file processors.
    /// </summary>
    public FileProcessPipeline(IEnumerable<FileProcessor> processors)
    {
        Processors = processors.ToImmutableArray();
    }

    internal async Task ExecuteAsync(FileProcessContext context, CancellationToken cancellationToken)
    {
        for (int i = 0; i < Processors.Length; i++)
        {
            var processor = Processors[i];
            var result = await processor.ProcessAsyncInternal(context, cancellationToken).ConfigureAwait(false);

            bool isNextProcessorLast = i >= Processors.Length - 2;
            await context.SetResult(result, isNextProcessorLast).ConfigureAwait(false);
        }
    }
}
