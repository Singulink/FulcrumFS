using Singulink.Enums;

namespace FulcrumFS;

/// <summary>
/// Represents a pipeline for processing files using a series of <see cref="FileProcessor"/> instances.
/// </summary>
public class FileProcessingPipeline : IFileProcessingPipelineProvider, IFileProcessingPipelineSelector
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
            value.ThrowIfNotDefined();
            field = value;
        }
    }

    /// <summary>
    /// Gets or initializes a value indicating how an unchanged source file is handled after running through the pipeline. Default value is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>When this property is <see langword="true"/> and no processor in the pipeline reports changes, the behavior depends on the role of the pipeline
    /// in the add operation:</para>
    /// <list type="bullet">
    /// <item><description>On the <em>main</em> pipeline of a <see cref="FileRepoTransaction"/> add call a <see cref="FileSourceUnchangedException"/> is thrown
    /// to the caller because <see cref="RepoFileGroupInfo.Main"/> cannot be omitted.</description></item>
    /// <item><description>On any <em>variant</em> pipeline (the top-level pipeline passed to a variant-add method on <see cref="FileRepo"/>, or any nested
    /// entry in <see cref="Variants"/>) the variant is silently skipped, no file is stored for it, and any nested variants of the skipped variant continue to
    /// run against the unchanged parent source. Skipped variants are omitted from the returned result list.</description></item>
    /// </list>
    /// <para>This is useful for avoiding storing duplicate files. For example, a thumbnail variant pipeline that produces no changes when the source image is
    /// already small enough can be skipped instead of storing an identical copy.</para>
    /// </remarks>
    public bool SkipWhenSourceUnchanged { get; init; }

#pragma warning restore SA1623

    /// <summary>
    /// Gets or initializes the collection of auto-variants produced after the main pipeline completes. Each variant pipeline receives the output of this
    /// pipeline as its source. Variants are <see cref="IFileProcessingPipelineProvider"/>s so their nested variant trees can be enumerated ahead of execution.
    /// </summary>
    /// <remarks>
    /// Variant IDs within a single <see cref="Variants"/> list must be unique after normalization or an <see cref="ArgumentException"/> is thrown at
    /// initialization. All variant IDs in the entire stored tree under a single file ID must also be unique; cross-level collisions are detected by the add
    /// runner at store time.
    /// </remarks>
    public IReadOnlyList<FileProcessingVariant> Variants
    {
        get;
        init {
            var list = new List<FileProcessingVariant>(value.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var v in value)
            {
                if (!seen.Add(v.VariantId))
                    throw new ArgumentException($"Duplicate variant ID '{v.VariantId}' in pipeline variants.", nameof(value));

                list.Add(v);
            }

            field = list;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessingPipeline"/> class with the specified file processors.
    /// </summary>
    public FileProcessingPipeline(params IEnumerable<FileProcessor> processors)
    {
        Processors = [.. processors];
        Variants = [];
    }

    /// <summary>
    /// Returns a new <see cref="FileProcessingPipeline"/> with the specified variant appended to <see cref="Variants"/>.
    /// </summary>
    /// <param name="variantId">The variant ID. Must only contain ASCII letters, digits, hyphens and underscores.</param>
    /// <param name="pipeline">The pipeline used to process the variant.</param>
    public FileProcessingPipeline WithVariant(string variantId, IFileProcessingPipelineProvider pipeline)
    {
        var newVariants = new List<FileProcessingVariant>(Variants.Count + 1);
        newVariants.AddRange(Variants);
        newVariants.Add(new FileProcessingVariant(variantId, pipeline));

        return new FileProcessingPipeline(Processors)
        {
            SourceBufferingMode = SourceBufferingMode,
            SkipWhenSourceUnchanged = SkipWhenSourceUnchanged,
            Variants = newVariants,
        };
    }

    /// <inheritdoc/>
    FileProcessingPipeline IFileProcessingPipelineProvider.GetPipeline() => this;

    /// <inheritdoc/>
    FileProcessingPipeline IFileProcessingPipelineSelector.GetPipeline(string extension) => this;

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

        if (SkipWhenSourceUnchanged && !context.HasChanges)
            throw new FileSourceUnchangedException();
    }
}
