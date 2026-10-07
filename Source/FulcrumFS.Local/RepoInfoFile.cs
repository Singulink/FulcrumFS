using System.Globalization;
using System.Text;

namespace FulcrumFS;

/// <summary>
/// Helpers for reading, writing, upgrading and version-compatibility checking the FulcrumFS repository info marker file
/// (<see cref="FileRepoPaths.InfoFileName"/>).
/// </summary>
/// <remarks>
/// <para>The info file is a plain text key=value file. The required <c>version</c> key identifies the full-access version of the repository - i.e. the version
/// that any library code must understand in order to perform full repository operations (add, delete, fetch, etc.). Optional override keys may advertise
/// looser per-capability compatibility:</para>
/// <list type="bullet">
/// <item><description><c>read-compat-version</c>: minimum library version required to read existing files from the repository.</description></item>
/// <item><description><c>clean-compat-version</c>: minimum library version required to perform clean operations on the repository.</description></item>
/// </list>
/// <para>Any per-capability override that is missing falls back to the base <c>version</c> value. This lets a future on-disk format bump the full-access
/// baseline while still advertising compatibility for older readers/cleaners that only need to understand the lower-numbered subset of behavior.</para>
/// <para>Versions so far:</para>
/// <list type="bullet">
/// <item><description>1: the original format (library 1.0.x), written without override keys.</description></item>
/// <item><description>2: adds delete journals to the cleanup directory (library 2.0). Fetching files is unchanged, so
/// <c>read-compat-version</c> is 1; a version 1 cleaner does not know journals and would leave the files of a committed journal undeleted, so
/// <c>clean-compat-version</c> is 2.</description></item>
/// </list>
/// <para>This library understands repositories whose effective per-capability versions are less than or equal to <see cref="SupportedVersion"/>. It never
/// changes the version of an existing repository on its own: the info file is only upgraded to the version the application configured in
/// <see cref="FileRepoOptions.MinimumFormatVersion"/>.</para>
/// </remarks>
internal static class RepoInfoFile
{
    /// <summary>
    /// The newest repository info version that this library can write, and the maximum version that it understands. A repository whose effective version for
    /// a given capability exceeds this value cannot be accessed via that capability by this library.
    /// </summary>
    public const int SupportedVersion = (int)RepoFormatVersion.Version2;

    /// <summary>
    /// The first version whose cleanup directory may contain delete journals. On an older repository a staged transaction records its deletions with per-file
    /// indeterminate markers instead, and an ordinary transaction commits each deletion with a delete marker, because a version 1 cleaner may run against it.
    /// </summary>
    public const int DeleteJournalsVersion = (int)RepoFormatVersion.Version2;

    private const string FullAccessVersionKey = "version";
    private const string ReadCompatVersionKey = "read-compat-version";
    private const string CleanCompatVersionKey = "clean-compat-version";
    private const string CreatedKey = "created";

    private const string UpgradeTempFileExtension = ".tmp";

    /// <summary>
    /// Effective per-capability versions for a repository, with per-capability overrides resolved against the base <see cref="FullAccess"/> value.
    /// </summary>
    public readonly record struct Versions(int FullAccess, int ReadCompat, int CleanCompat);

    /// <summary>
    /// Writes a new info file declaring the specified version, its per-capability compatibility versions and the creation time.
    /// </summary>
    public static void Write(IAbsoluteFilePath infoFile, int version)
    {
        using var stream = infoFile.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None);

        // The info file identifies the directory as a repository, so it must not come back empty after a power loss.
        WriteContent(stream, version, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Reads the info file and resolves per-capability effective versions, falling back to the base <c>version</c> value when a per-capability override key
    /// is missing.
    /// </summary>
    /// <exception cref="InvalidDataException">The info file is malformed or missing the required <c>version</c> key.</exception>
    public static Versions Read(IAbsoluteFilePath infoFile) => GetVersions(ReadValues(infoFile), infoFile);

    /// <summary>
    /// Verifies that the repository's effective full-access version is supported by this library and returns the versions read.
    /// </summary>
    /// <exception cref="NotSupportedException">The repository requires a newer library version for full access.</exception>
    /// <exception cref="InvalidDataException">The info file is malformed.</exception>
    public static Versions VerifyFullAccessSupported(IAbsoluteFilePath infoFile)
    {
        var v = Read(infoFile);
        ThrowIfFullAccessUnsupported(v, infoFile);
        return v;
    }

    /// <summary>
    /// Verifies that the repository's effective <c>read-compat-version</c> is supported by this library and returns the versions read.
    /// </summary>
    /// <exception cref="NotSupportedException">The repository requires a newer library version for reading.</exception>
    /// <exception cref="InvalidDataException">The info file is malformed.</exception>
    public static Versions VerifyReadCompatSupported(IAbsoluteFilePath infoFile)
    {
        var v = Read(infoFile);

        if (v.ReadCompat > SupportedVersion)
        {
            throw new NotSupportedException(
                $"The FulcrumFS repository at '{infoFile.ParentDirectory.PathDisplay}' requires read-compat version {v.ReadCompat} but this library only " +
                $"supports up to version {SupportedVersion}.");
        }

        return v;
    }

    /// <summary>
    /// MUST BE CALLED WHILE HOLDING THE REPOSITORY LOCK. Verifies that the repository's effective full-access version is supported by this library and, if the
    /// repository has a version older than <paramref name="targetVersion"/>, upgrades its info file to that version in place. Returns <see langword="true"/>
    /// if the info file was upgraded.
    /// </summary>
    /// <remarks>
    /// The info file is replaced atomically by writing the new content to a temporary file in the base directory, flushing it to disk and renaming it over the
    /// info file. Only the <c>created</c> value is carried over; a version 1 info file has nothing else.
    /// </remarks>
    /// <exception cref="NotSupportedException">The repository requires a newer library version for full access.</exception>
    /// <exception cref="InvalidDataException">The info file is malformed.</exception>
    public static bool VerifyFullAccessSupportedAndUpgrade(IAbsoluteFilePath infoFile, int targetVersion)
    {
        var values = ReadValues(infoFile);
        var v = GetVersions(values, infoFile);
        ThrowIfFullAccessUnsupported(v, infoFile);

        if (v.FullAccess >= targetVersion)
            return false;

        // The repository lock keeps out other upgraders, so a fixed name is safe and a file left behind by a crashed upgrade is simply overwritten.
        var tempFile = infoFile.ParentDirectory.CombineFile(infoFile.Name + UpgradeTempFileExtension, PathOptions.None);

        try
        {
            using (var stream = tempFile.OpenStream(FileMode.Create, FileAccess.Write, FileShare.Read))
                WriteContent(stream, targetVersion, values.GetValueOrDefault(CreatedKey));

            // The rename replaces the info file in one step, so a crash leaves the old or the new content and never a partial file. The new file was flushed
            // before the rename so a power loss cannot leave it renamed but empty, and the directory is flushed after it so the rename itself is durable. On
            // Windows the directory flush is a no-op and NTFS commits the rename with the next flush of any file, which comes before anything that relies on
            // version 2: a delete journal is flushed when it is staged.
            tempFile.MoveTo(infoFile, overwrite: true);
            DurableIO.FlushDirectory(infoFile.ParentDirectory);
        }
        catch
        {
            tempFile.TryDelete(out _);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Verifies that the repository's effective <c>clean-compat-version</c> is supported by this library.
    /// </summary>
    /// <exception cref="NotSupportedException">The repository requires a newer library version for clean operations.</exception>
    /// <exception cref="InvalidDataException">The info file is malformed.</exception>
    public static void VerifyCleanCompatSupported(IAbsoluteFilePath infoFile)
    {
        var v = Read(infoFile);

        if (v.CleanCompat > SupportedVersion)
        {
            throw new NotSupportedException(
                $"The FulcrumFS repository at '{infoFile.ParentDirectory.PathDisplay}' requires clean-compat version {v.CleanCompat} but this library only " +
                $"supports up to version {SupportedVersion}.");
        }
    }

    private static void WriteContent(FileStream stream, int version, string? created)
    {
        // Version 1 is written exactly as FulcrumFS 1.0.x wrote it, without override keys, which it did not know.
        string overrides = version switch
        {
            1 => string.Empty,
            2 => $"{ReadCompatVersionKey}=1\n{CleanCompatVersionKey}=2\n",
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "The version is not supported by this library."),
        };

        string content =
            $"{FullAccessVersionKey}={version.ToString(CultureInfo.InvariantCulture)}\n" +
            overrides +
            (created is null ? string.Empty : $"{CreatedKey}={created}\n");

        stream.Write(Encoding.UTF8.GetBytes(content));
        DurableIO.FlushFile(stream);
    }

    private static Dictionary<string, string> ReadValues(IAbsoluteFilePath infoFile)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        using var stream = infoFile.OpenStream(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);

        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length is 0 || line[0] is '#')
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line[..eq].Trim();
            values[key] = line[(eq + 1)..].Trim();
        }

        return values;
    }

    private static Versions GetVersions(Dictionary<string, string> values, IAbsoluteFilePath infoFile)
    {
        if (!values.TryGetValue(FullAccessVersionKey, out string? fullAccessStr) ||
            !int.TryParse(fullAccessStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fullAccess))
        {
            throw new InvalidDataException(
                $"The FulcrumFS repository info file at '{infoFile.PathDisplay}' is missing a valid '{FullAccessVersionKey}' value.");
        }

        int GetOverride(string key)
        {
            if (!values.TryGetValue(key, out string? s))
                return fullAccess;

            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                throw new InvalidDataException(
                    $"The FulcrumFS repository info file at '{infoFile.PathDisplay}' contains an invalid '{key}' value: '{s}'.");
            }

            return v;
        }

        return new Versions(
            FullAccess: fullAccess,
            ReadCompat: GetOverride(ReadCompatVersionKey),
            CleanCompat: GetOverride(CleanCompatVersionKey));
    }

    private static void ThrowIfFullAccessUnsupported(Versions v, IAbsoluteFilePath infoFile)
    {
        if (v.FullAccess > SupportedVersion)
        {
            throw new NotSupportedException(
                $"The FulcrumFS repository at '{infoFile.ParentDirectory.PathDisplay}' requires full-access version {v.FullAccess} but this library only " +
                $"supports up to version {SupportedVersion}.");
        }
    }
}
