using Singulink.Enums;

namespace FulcrumFS;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents configuration options for a <see cref="FileRepoCleaner"/>.
/// </summary>
public class FileRepoCleaningOptions
{
    private bool _frozen;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaningOptions"/> class.
    /// </summary>
    public FileRepoCleaningOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaningOptions"/> class with the specified base directory for the file repository to be cleaned.
    /// </summary>
    /// <param name="baseDirectory">The base directory of the file repository to clean. Must be an existing repository base directory.</param>
    [SetsRequiredMembers]
    public FileRepoCleaningOptions(IAbsoluteDirectoryPath baseDirectory)
    {
        BaseDirectory = baseDirectory;
    }

    /// <summary>
    /// Gets or sets the base directory of the file repository to be cleaned.
    /// </summary>
    public required IAbsoluteDirectoryPath BaseDirectory {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the logging mode used when writing log entries to marker files during clean operations. Default is <see cref="RepoLoggingMode.HumanReadable"/>.
    /// </summary>
    public RepoLoggingMode MarkerFileLogging {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
            field = value;
        }
    } = RepoLoggingMode.HumanReadable;

    internal void Freeze()
    {
        _frozen = true;
    }

    private void EnsureNotFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException("Cannot modify options after the cleaner has been initialized.");
    }
}
