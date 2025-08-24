namespace FulcrumFS;

/// <summary>
/// Represents a pipeline for processing files using a series of <see cref="FileProcessor"/> instances.
/// </summary>
public class FileProcessPipeline
{
    /// <summary>
    /// Gets an empty file process pipeline that does not perform any processing on files.
    /// </summary>
    public static FileProcessPipeline Empty { get; } = new([]);

    /// <summary>
    /// Gets the collection of file processors that will be used in this pipeline to process files.
    /// </summary>
    public IReadOnlyList<FileProcessor> Processors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessPipeline"/> class with the specified file processors.
    /// </summary>
    public FileProcessPipeline(IEnumerable<FileProcessor> processors)
    {
        Processors = [..processors];
    }

    internal async Task ExecuteAsync(FileProcessContext context)
    {
        for (int i = 0; i < Processors.Count; i++)
        {
            var processor = Processors[i];
            var result = await processor.CallProcessAsync(context, context.CancellationToken).ConfigureAwait(false);

            bool isNextProcessorLast = i >= Processors.Count - 2;
            await context.SetResultAsync(result, isNextProcessorLast).ConfigureAwait(false);
        }
    }
}
