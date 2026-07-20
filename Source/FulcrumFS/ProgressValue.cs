namespace FulcrumFS;

/// <summary>
/// Represents a progress value for a file processing operation, including the variant ID, stage, progress fraction, and optional stage display message.
/// </summary>
public readonly record struct ProgressValue
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressValue"/> struct with the specified variant ID, stage, progress fraction, and optional stage display
    /// message.
    /// </summary>
    internal ProgressValue(string? variantId, string stage, double progress, string? stageDisplayMessage = null)
    {
        VariantId = variantId;
        Stage = stage;
        Progress = progress;
        StageDisplayMessage = stageDisplayMessage;
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

    /// <summary>
    /// Gets an optional informative display message for the current stage, or <see langword="null"/> if no message has been set. This is purely informative and
    /// does not represent a new stage.
    /// </summary>
    public string? StageDisplayMessage { get; init; }
}
