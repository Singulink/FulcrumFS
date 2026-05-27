namespace FulcrumFS;

/// <summary>
/// Provides a statically-shaped <see cref="FileProcessingPipeline"/> whose variant tree can be enumerated ahead of execution. Both <see cref="FileProcessor"/>
/// and <see cref="FileProcessingPipeline"/> implement this interface.
/// </summary>
/// <remarks>
/// This is the type accepted wherever a variant pipeline is configured (e.g. <see cref="FileProcessingVariant.Pipeline"/> and the <c>WithVariant</c> methods).
/// It guarantees that nested variant ids can be discovered ahead of execution, which the add runner relies on for sorted variant-lock acquisition. For
/// dynamic per-extension routing, see <see cref="IFileProcessingPipelineSelector"/>.
/// </remarks>
public interface IFileProcessingPipelineProvider
{
    /// <summary>
    /// Gets the pipeline produced by this provider.
    /// </summary>
    FileProcessingPipeline GetPipeline();
}
