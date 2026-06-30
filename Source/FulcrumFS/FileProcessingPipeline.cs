using System.Globalization;
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
    /// Gets or initializes a value indicating whether a variant pipeline should produce an alias to its source variant when running the pipeline results in no
    /// changes to the source file. Default value is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>This property only applies to variant pipelines (the top-level pipeline passed to a variant-add method on <see cref="FileRepo"/>, or any nested
    /// entry in <see cref="Variants"/>). It has no effect on the main pipeline of a <see cref="FileRepoTransaction"/> add call; use
    /// <see cref="ThrowWhenMainSourceUnchanged"/> for that scenario.</para>
    /// <para>When this property is <see langword="true"/> and no processor in the pipeline reports changes, no data file is stored for the variant. Instead, a
    /// zero-byte alias marker is written that transparently resolves to the variant's source on fetch operations. Nested variants of the aliased variant
    /// continue to run against the unchanged parent source.</para>
    /// <para>This is useful for avoiding storing duplicate files. For example, a frame variant pipeline that produces no changes when the source image is
    /// already small enough will store an alias to the source instead of an identical copy.</para>
    /// </remarks>
    public bool AliasWhenVariantSourceUnchanged { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the main pipeline of a <see cref="FileRepoTransaction"/> add call should throw a
    /// <see cref="FileSourceUnchangedException"/> when running the pipeline results in no changes to the source file. Default value is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This property only applies to the main pipeline of a transactional add; it has no effect on variant pipelines (use
    /// <see cref="AliasWhenVariantSourceUnchanged"/> for those). When <see langword="true"/> and no processor reports changes, the exception is propagated to
    /// the caller so the add can be aborted (the main file cannot be omitted from a file group).
    /// </remarks>
    public bool ThrowWhenMainSourceUnchanged { get; init; }

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
    /// Gets a pre-order flat enumeration of every variant in this pipeline's nested variant tree (descendants only; does not include any conceptual "root"
    /// entry for the pipeline itself). The list is computed and validated lazily on first access; an <see cref="ArgumentException"/> is thrown at that time
    /// if any two variant IDs anywhere in the tree collide.
    /// </summary>
    internal IReadOnlyList<FileProcessingVariant> AllVariants => field ??= BuildAllVariants(this);

    private static List<FileProcessingVariant> BuildAllVariants(FileProcessingPipeline root)
    {
        var flat = new List<FileProcessingVariant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, flat, seen);
        return flat;

        static void Walk(FileProcessingPipeline pipeline, List<FileProcessingVariant> flat, HashSet<string> seen)
        {
            foreach (var v in pipeline.Variants)
            {
                if (!seen.Add(v.VariantId))
                    throw new ArgumentException($"Pipeline variants processing tree contains duplicate variant ID '{v.VariantId}'.");

                flat.Add(v);
                Walk(v.Pipeline.GetPipeline(), flat, seen);
            }
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
            AliasWhenVariantSourceUnchanged = AliasWhenVariantSourceUnchanged,
            ThrowWhenMainSourceUnchanged = ThrowWhenMainSourceUnchanged,
            Variants = newVariants,
        };
    }

    /// <inheritdoc/>
    FileProcessingPipeline IFileProcessingPipelineProvider.GetPipeline() => this;

    /// <inheritdoc/>
    FileProcessingPipeline IFileProcessingPipelineSelector.GetPipeline(string extension) => this;

    internal async Task ExecuteAsync(FileProcessingContext context, Func<ProgressValue, ValueTask>? progressCallback, bool knownRepoFileSource, bool isMainPipeline)
    {
        if ((SourceBufferingMode is SourceBufferingMode.ForceTempCopy && !knownRepoFileSource) ||
            (SourceBufferingMode is SourceBufferingMode.Auto && !context.IsSourceInMemoryOrFile))
        {
            await context.BufferSourceToWorkFileAsync().ConfigureAwait(false);
        }

        Dictionary<string, int> countPerName = [];

        for (int i = 0; i < Processors.Count; i++)
        {
            var processor = Processors[i];

            Func<double, ValueTask>? processorProgressCallback = null;

            // Handle progress reporting if enabled:
            if (progressCallback is not null)
            {
                // Get a unique name:
                string processorName = processor.DisplayName;

                if (processorName.Contains(' '))
                    throw new InvalidOperationException($"Processor display name '{processorName}' must not contain a space character.");

                if (!countPerName.TryAdd(processorName, 1))
                {
                    int count = countPerName[processorName];
                    countPerName[processorName] = count + 1;
                    processorName = string.Create(CultureInfo.InvariantCulture, $"{processorName} ({count + 1})");
                }

                // Create the callback to use for this one:
                processorProgressCallback = async (fraction) =>
                {
                    var progressValue = new ProgressValue(context.VariantId, processorName, fraction);
                    await progressCallback(progressValue).ConfigureAwait(false);
                };

                // Ensure we see some progress for this processor even if it doesn't report any:
                await processorProgressCallback(0.0).ConfigureAwait(false);

                // Set the callback on the context:
                context.ProgressCallback = processorProgressCallback;
            }

            var result = await processor.CallProcessAsync(context).ConfigureAwait(false);

            bool oneStepLeft = i == Processors.Count - 2;
            await context.SetResultAsync(result, oneStepLeft).ConfigureAwait(false);
        }

        bool throwOnUnchanged = isMainPipeline ? ThrowWhenMainSourceUnchanged : AliasWhenVariantSourceUnchanged;

        if (throwOnUnchanged && !context.HasChanges)
            throw new FileSourceUnchangedException();
    }
}
