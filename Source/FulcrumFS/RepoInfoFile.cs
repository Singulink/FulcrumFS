using System.Globalization;

namespace FulcrumFS;

/// <summary>
/// Helpers for reading, writing and version-compatibility checking the FulcrumFS repository info marker file (<see cref="FileRepoPaths.InfoFileName"/>).
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
/// <para>This library understands repositories whose effective per-capability versions are less than or equal to <see cref="SupportedVersion"/>.</para>
/// </remarks>
internal static class RepoInfoFile
{
    /// <summary>
    /// The maximum repository info version that this library understands. A repository whose effective version for a given capability exceeds this value
    /// cannot be accessed via that capability by this library.
    /// </summary>
    public const int SupportedVersion = 1;

    private const string FullAccessVersionKey = "version";
    private const string ReadCompatVersionKey = "read-compat-version";
    private const string CleanCompatVersionKey = "clean-compat-version";

    /// <summary>
    /// Effective per-capability versions for a repository, with per-capability overrides resolved against the base <see cref="FullAccess"/> value.
    /// </summary>
    public readonly record struct Versions(int FullAccess, int ReadCompat, int CleanCompat);

    /// <summary>
    /// Writes a new info file declaring the current library version as the full-access baseline. Per-capability override keys are intentionally omitted so
    /// they fall back to the baseline value.
    /// </summary>
    public static void Write(IAbsoluteFilePath infoFile)
    {
        string content =
            $"{FullAccessVersionKey}={SupportedVersion}\n" +
            $"created={DateTime.UtcNow:O}\n";

        using var stream = infoFile.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    /// <summary>
    /// Reads the info file and resolves per-capability effective versions, falling back to the base <c>version</c> value when a per-capability override key
    /// is missing.
    /// </summary>
    /// <exception cref="InvalidDataException">The info file is malformed or missing the required <c>version</c> key.</exception>
    public static Versions Read(IAbsoluteFilePath infoFile)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var stream = infoFile.OpenStream(FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(stream))
        {
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
        }

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

    /// <summary>
    /// Verifies that the repository's effective full-access version is supported by this library.
    /// </summary>
    /// <exception cref="NotSupportedException">The repository requires a newer library version for full access.</exception>
    /// <exception cref="InvalidDataException">The info file is malformed.</exception>
    public static void VerifyFullAccessSupported(IAbsoluteFilePath infoFile)
    {
        var v = Read(infoFile);

        if (v.FullAccess > SupportedVersion)
        {
            throw new NotSupportedException(
                $"The FulcrumFS repository at '{infoFile.ParentDirectory.PathDisplay}' requires full-access version {v.FullAccess} but this library only " +
                $"supports up to version {SupportedVersion}.");
        }
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
}
