namespace FulcrumFS;

/// <summary>
/// Represents the result of adding a file to the repository.
/// </summary>
public record AddFileResult
{
    /// <summary>
    /// Gets the file ID of the file.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Gets the path of the file.
    /// </summary>
    public IAbsoluteFilePath File { get; }

    internal AddFileResult(Guid id, IAbsoluteFilePath file)
    {
        Id = id;
        File = file;
    }
}
