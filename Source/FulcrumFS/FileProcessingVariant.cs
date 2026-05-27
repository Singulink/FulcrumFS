namespace FulcrumFS;

/// <summary>
/// Represents an auto-variant that is produced from the output of a <see cref="FileProcessingPipeline"/>.
/// </summary>
public sealed record FileProcessingVariant
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessingVariant"/> class.
    /// </summary>
    /// <param name="variantId">The variant ID. Must only contain ASCII letters, digits, hyphens and underscores. The value is normalized to lowercase.</param>
    /// <param name="pipeline">The pipeline used to process this variant. Restricted to <see cref="IFileProcessingPipelineProvider"/> (a statically-shaped
    /// pipeline) so that the runner can enumerate nested variant ids ahead of execution.</param>
    /// <exception cref="ArgumentException"><paramref name="variantId"/> is empty or contains invalid characters.</exception>
    public FileProcessingVariant(string variantId, IFileProcessingPipelineProvider pipeline)
    {
        VariantId = Utilities.VariantId.Normalize(variantId);
        Pipeline = pipeline;
    }

    /// <summary>
    /// Gets the variant ID. Guaranteed to be non-empty, contain only valid characters, and be normalized to lowercase.
    /// </summary>
    public string VariantId { get; private init; }

    /// <summary>
    /// Gets the pipeline used to process this variant.
    /// </summary>
    public IFileProcessingPipelineProvider Pipeline { get; private init; }
}
