namespace FulcrumFS;

/// <summary>
/// Represents the result of adding a file variant to the repository.
/// </summary>
public record AddVariantResult
{
    /// <summary>
    /// Gets the path of the file variant.
    /// </summary>
    public IAbsoluteFilePath File { get; }

    /// <summary>
    /// Gets a value indicating whether the variant already existed in the repository.
    /// </summary>
    public bool AlreadyExisted { get; }

    internal AddVariantResult(IAbsoluteFilePath file, bool alreadyExisted)
    {
        File = file;
        AlreadyExisted = alreadyExisted;
    }
}
