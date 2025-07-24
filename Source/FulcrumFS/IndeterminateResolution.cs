namespace FulcrumFS;

/// <summary>
/// Specifies the resolution action to take for files with indeterminate status in a repository.
/// </summary>
public enum IndeterminateResolution
{
    /// <summary>
    /// Indicates that the file should be kept in the repository.
    /// </summary>
    Keep,

    /// <summary>
    /// Indicates that the file should be deleted from the repository.
    /// </summary>
    Delete,
}
