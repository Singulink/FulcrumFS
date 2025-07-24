using System.Collections.Immutable;

namespace FulcrumFS;

/// <summary>
/// Represents a pipeline for processing files using a series of <see cref="FileProcessor"/> instances.
/// </summary>
public class FileProcessPipeline
{
    private readonly ImmutableArray<FileProcessor> _processors;

    /// <summary>
    /// Gets an empty file process pipeline that does not perform any processing on files.
    /// </summary>
    public static FileProcessPipeline Empty { get; } = new([]);

    /// <summary>
    /// Gets the collection of file processors that will be used in this pipeline to process files.
    /// </summary>
    public IReadOnlyList<FileProcessor> Processors => _processors;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessPipeline"/> class with the specified file processors.
    /// </summary>
    public FileProcessPipeline(IEnumerable<FileProcessor> processors)
    {
        _processors = processors.ToImmutableArray();
    }

    internal async Task ExecuteAsync(FileProcessContext context)
    {
        for (int i = 0; i < _processors.Length; i++)
        {
            var processor = _processors[i];
            var result = await processor.ProcessAsyncInternal(context, context.CancellationToken).ConfigureAwait(false);

            bool isNextProcessorLast = i >= _processors.Length - 2;
            await context.SetResult(result, isNextProcessorLast).ConfigureAwait(false);
        }
    }
}
