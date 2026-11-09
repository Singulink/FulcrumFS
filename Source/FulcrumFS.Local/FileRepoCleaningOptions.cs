using Singulink.Enums;

namespace FulcrumFS;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents configuration options for a <see cref="FileRepoCleaner"/>.
/// </summary>
/// <remarks>
/// <para>
/// The cleaner checks and freezes the options when it is constructed, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.</para>
/// <para>
/// The members that are <see langword="required"/> have no default. The compiler requires them when the options are created in code, but not when they are
/// bound from a configuration, so they read as not set until they are set, and the <see cref="FileRepoCleaner"/> constructor throws, with a message that
/// names them, if one is still not set then.</para>
/// </remarks>
public sealed class FileRepoCleaningOptions
{
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
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> has a name that is not friendly (see <see cref="BaseDirectory"/>).</exception>
    [SetsRequiredMembers]
    public FileRepoCleaningOptions(IAbsoluteDirectoryPath baseDirectory)
    {
        CheckBaseDirectory(baseDirectory, nameof(baseDirectory));
        BaseDirectory = baseDirectory;
    }

    /// <summary>
    /// Gets or sets the base directory of the file repository to be cleaned, or <see langword="null"/> while it is not set.
    /// </summary>
    /// <remarks>
    /// The path must be valid for <see cref="PathOptions.NoUnfriendlyNames"/> on every platform, as <see cref="FileRepoOptions.BaseDirectory"/> must: no
    /// name in it starts or ends with a space, ends with a dot or contains a control character, and on Windows none is a reserved device name such as
    /// <c>CON</c>. Configuration binding reads paths with <see cref="PathOptions.None"/>, which keeps such names, so without this check a typo such as a
    /// trailing space or dot would name a directory that is not the repository's.
    /// </remarks>
    /// <exception cref="ArgumentException">The value has a name that is not friendly (see above).</exception>
    [MaybeNull]
    public required IAbsoluteDirectoryPath BaseDirectory {
        get => field ?? (IsFrozen ? throw NotSet(nameof(BaseDirectory)) : null);
        set {
            EnsureNotFrozen();
            CheckBaseDirectory(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the logging mode used when writing log entries to marker files during clean operations. Default is
    /// <see cref="RepoLoggingMode.HumanReadable"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="RepoLoggingMode"/> value.</exception>
    public RepoLoggingMode MarkerFileLogging {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfNotDefined();
            field = value;
        }
    } = RepoLoggingMode.HumanReadable;

    /// <summary>
    /// Gets a value indicating whether the options are in use: a <see cref="FileRepoCleaner"/> checked them (see <see cref="Validate"/>) when it was
    /// constructed with them, and they can no longer be changed.
    /// </summary>
    [MemberNotNullWhen(true, nameof(BaseDirectory))]
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the <see cref="FileRepoCleaner"/> constructor does. The required <see cref="BaseDirectory"/> is set.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(BaseDirectory))]
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0 || BaseDirectory is null)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        List<string> problems = [];

        if (BaseDirectory is null)
            problems.Add(NotSetMessage(nameof(BaseDirectory)));

        return problems;
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when a <see cref="FileRepoCleaner"/> is constructed
    /// with them. Options that are frozen already stay as they are.
    /// </summary>
    /// <param name="paramName">The name of the parameter that the options were passed in, which the exception names.</param>
    /// <exception cref="ArgumentException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(BaseDirectory))]
    internal void Freeze(string paramName)
    {
        if (IsFrozen)
            return;

        var problems = GetProblems();

        if (problems.Count > 0 || BaseDirectory is null)
            throw new ArgumentException(string.Join(" ", problems), paramName);

        IsFrozen = true;
    }

    private static void CheckBaseDirectory(IAbsoluteDirectoryPath value, string paramName)
    {
        if (!value.IsValidFor(PathOptions.NoUnfriendlyNames, out string? errorMessage))
        {
            throw new ArgumentException(
                $"{nameof(FileRepoCleaningOptions)}.{nameof(BaseDirectory)} '{value.PathDisplay}' must be a path with friendly names: {errorMessage}",
                paramName);
        }
    }

    private static string NotSetMessage(string property) => $"{nameof(FileRepoCleaningOptions)}.{property} is not set. It is required, and has no default.";

    private static InvalidOperationException NotSet(string property) => new(NotSetMessage(property));

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException(
                $"The {nameof(FileRepoCleaningOptions)} are in use by a {nameof(FileRepoCleaner)}, so they can no longer be changed.");
        }
    }
}
