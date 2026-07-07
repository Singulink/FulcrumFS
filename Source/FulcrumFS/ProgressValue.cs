namespace FulcrumFS;

/// <summary>
/// Represents a progress value for a file processing operation, including the variant ID, stage, and progress fraction.
/// </summary>
public readonly record struct ProgressValue
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressValue"/> struct with the specified variant ID, stage, and progress fraction.
    /// </summary>
    internal ProgressValue(string? variantId, string stage, double progress)
    {
        VariantId = variantId;
        Stage = stage;
        Progress = progress;
    }

    /// <summary>
    /// Gets the ID of the variant being processed.
    /// </summary>
    public string? VariantId { get; init; }

    /// <summary>
    /// Gets the current stage of the processing operation.
    /// </summary>
    public string Stage { get; init; }

    /// <summary>
    /// Gets the progress fraction of the operation, ranging from 0.0 to 1.0.
    /// </summary>
    public double Progress { get; init; }
}
