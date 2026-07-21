#pragma warning disable SA1649 // File name should match first type name

namespace FulcrumFS;

/// <summary>
/// A processor that reports no changes (passing the source through unchanged) and counts how many times it was invoked. Combined with
/// <see cref="FileProcessingPipeline.AliasWhenVariantSourceUnchanged"/> it causes an alias marker to be written for the variant.
/// </summary>
internal sealed class NoChangeCountingProcessor : FileProcessor
{
    private int _invocationCount;

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public override IReadOnlyList<string> AllowedFileExtensions => [];

    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        Interlocked.Increment(ref _invocationCount);
        var sourceFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
        return FileProcessingResult.File(sourceFile, hasChanges: false);
    }
}

/// <summary>
/// A processor that produces a changed data file by writing a fixed set of bytes to a new work file, forcing a real (non-alias) variant data file to be
/// stored.
/// </summary>
internal sealed class RewriteProcessor : FileProcessor
{
    private readonly byte[] _content;

    public RewriteProcessor(byte[]? content = null) => _content = content ?? [1, 2, 3, 4, 5];

    public override IReadOnlyList<string> AllowedFileExtensions => [];

    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        var workFile = context.GetNewWorkFile(context.Extension);
        workFile.ParentDirectory.Create();

        var stream = workFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.None);

        await using (stream.ConfigureAwait(false))
            await stream.WriteAsync(_content).ConfigureAwait(false);

        return FileProcessingResult.File(workFile, hasChanges: true);
    }
}

/// <summary>
/// A processor that reports a stage display message followed by a progress fraction (passing the source through unchanged), used to test message reporting
/// through the pipeline.
/// </summary>
internal sealed class MessageReportingProcessor : FileProcessor
{
    public override IReadOnlyList<string> AllowedFileExtensions => [];

    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        if (context.ProgressDisplayMessageCallback is not null)
            await context.ProgressDisplayMessageCallback("Working").ConfigureAwait(false);

        if (context.ProgressCallback is not null)
            await context.ProgressCallback(0.5).ConfigureAwait(false);

        var sourceFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);
        return FileProcessingResult.File(sourceFile, hasChanges: false);
    }
}

/// <summary>
/// Helpers for building common variant pipelines used across the variant alias and retirement tests.
/// </summary>
internal static class VariantPipelines
{
    /// <summary>
    /// Gets a variant pipeline with no processors that produces an alias to its source (because it makes no changes).
    /// </summary>
    public static FileProcessingPipeline Alias() => new() { AliasWhenVariantSourceUnchanged = true };

    /// <summary>
    /// Gets a variant pipeline that produces a real data file with the specified content.
    /// </summary>
    public static FileProcessingPipeline RealData(byte[]? content = null) => new RewriteProcessor(content).ToPipeline();
}
