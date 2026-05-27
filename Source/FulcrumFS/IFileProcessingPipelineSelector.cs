namespace FulcrumFS;

/// <summary>
/// Selects a <see cref="FileProcessingPipeline"/> to use for processing a file based on its source extension. This is the interface accepted by every
/// public add API on <see cref="FileRepoTransaction"/> and <see cref="FileRepo"/>.
/// </summary>
/// <remarks>
/// <para>Implemented by every type that can supply a concrete pipeline at call time: <see cref="FileProcessingPipeline"/> and <see cref="FileProcessor"/>
/// (both return the same pipeline regardless of extension), and <see cref="FileProcessingPipelineSelector"/> (per-extension routing with optional fallback).
/// User-defined types may also implement this interface to integrate with the add APIs.</para>
/// <para>For statically-shaped types whose pipeline does not vary by extension (and whose nested variant tree is therefore known ahead of execution), see
/// <see cref="IFileProcessingPipelineProvider"/>. That interface is the type required wherever a variant pipeline is configured.</para>
/// </remarks>
public interface IFileProcessingPipelineSelector
{
    /// <summary>
    /// Gets the pipeline to use for processing a file with the specified source extension.
    /// </summary>
    /// <param name="extension">The lowercase normalized source file extension, including the leading dot (e.g., <c>".jpg"</c>), or an empty string when the
    /// source has no extension.</param>
    /// <returns>The pipeline to execute.</returns>
    FileProcessingPipeline GetPipeline(string extension);
}
