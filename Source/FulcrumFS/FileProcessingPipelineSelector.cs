namespace FulcrumFS;

/// <summary>
/// Selects the appropriate <see cref="FileProcessingPipeline"/> based on the source file extension by routing across a set of registered pipelines.
/// </summary>
/// <remarks>
/// <para>Each registered pipeline declares the extensions it accepts via the <see cref="FileProcessor.AllowedFileExtensions"/> of its first processor.
/// A single catch-all (default) pipeline - one whose first processor declares no extensions, or one with no processors at all - may be included in the
/// constructor list and is used to handle any extension that does not match another entry.</para>
/// <para>If no entry matches the source extension and no default is registered, <see cref="GetPipeline(string)"/> throws a
/// <see cref="FileProcessingException"/>. A selector can itself be used as the default entry of another selector, enabling nesting.</para>
/// </remarks>
public sealed class FileProcessingPipelineSelector : IFileProcessingPipelineSelector
{
    private readonly Dictionary<string, IFileProcessingPipelineProvider> _byExtension = [];
    private readonly IFileProcessingPipelineProvider? _default;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProcessingPipelineSelector"/> class with the specified pipelines. Each pipeline is registered for every
    /// extension declared by its first processor's <see cref="FileProcessor.AllowedFileExtensions"/>. A single catch-all pipeline (one whose first processor
    /// declares no extensions, or one with no processors at all) may be included to handle any extension that does not match another entry.
    /// </summary>
    /// <param name="pipelines">The pipelines to register. Extensions declared across pipelines must not overlap and at most one catch-all is permitted.</param>
    /// <exception cref="ArgumentException">Multiple pipelines declare the same extension, or more than one catch-all is provided.</exception>
    public FileProcessingPipelineSelector(params IEnumerable<IFileProcessingPipelineProvider> pipelines)
    {
        foreach (var pipeline in pipelines)
        {
            var resolved = pipeline.GetPipeline();
            var extensions = resolved.Processors.FirstOrDefault()?.AllowedFileExtensions ?? [];

            if (extensions.Count is 0)
            {
                if (_default is not null)
                    throw new ArgumentException("Only one catch-all pipeline (with no processors or whose first processor declares no extensions) is permitted.", nameof(pipelines));

                _default = pipeline;
                continue;
            }

            foreach (string rawExtension in extensions)
            {
                string extension = FileExtension.Normalize(rawExtension);

                if (!_byExtension.TryAdd(extension, pipeline))
                    throw new ArgumentException($"Multiple registered pipelines declare extension '{extension}'.", nameof(pipelines));
            }
        }
    }

    /// <inheritdoc/>
    public FileProcessingPipeline GetPipeline(string extension)
    {
        if (_byExtension.TryGetValue(extension, out var pipeline))
            return pipeline.GetPipeline();

        if (_default is not null)
            return _default.GetPipeline();

        throw new FileProcessingException($"No pipeline is registered for extension '{extension}' and no default has been provided.");
    }
}
