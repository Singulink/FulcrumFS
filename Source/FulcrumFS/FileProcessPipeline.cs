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
}
