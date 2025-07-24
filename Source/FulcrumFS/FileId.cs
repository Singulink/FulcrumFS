using System.Diagnostics.CodeAnalysis;

namespace FulcrumFS;

/// <summary>
/// Represents a validated file identifier.
/// </summary>
public sealed class FileId : IParsable<FileId>, IEquatable<FileId>
{
    private static DateTimeOffset _lastTimeStamp = DateTimeOffset.MinValue;
    private static readonly object _timeSync = new();

    /// <summary>
    /// Implicitly converts a <see cref="FileId"/> instance to a <see cref="System.Guid"/>.
    /// </summary>
    public static implicit operator Guid(FileId fileId) => fileId.Guid;

    /// <summary>
    /// Implicitly converts a <see cref="System.Guid"/> to a <see cref="FileId"/>.
    /// </summary>
    /// <param name="fileIdGuid">The <see cref="System.Guid"/> to convert to a <see cref="FileId"/>.</param>
    public static explicit operator FileId(Guid fileIdGuid) => new(fileIdGuid);

    /// <summary>
    /// Determines whether two <see cref="FileId"/> instances are equal.
    /// </summary>
    public static bool operator ==(FileId left, FileId right) => left.Guid == right.Guid;

    /// <summary>
    /// Determines whether two <see cref="FileId"/> instances are not equal.
    /// </summary>
    public static bool operator !=(FileId left, FileId right) => left.Guid != right.Guid;

    /// <summary>
    /// Gets the underlying GUID representing the file ID.
    /// </summary>
    public Guid Guid { get; }

    internal string String => field ??= Guid.ToString("D").ToUpperInvariant();

    /// <summary>
    /// Initializes a new instance of the <see cref="FileId"/> class with the specified GUID.
    /// </summary>
    public FileId(Guid guid)
    {
        if (guid.Version is not 7)
            throw new ArgumentException("Invalid File ID.");

        Guid = guid;
    }

    private FileId(Guid guid, string str)
    {
        Guid = guid;
        String = str;
    }

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    public static FileId Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    public static FileId Parse(string s)
    {
        if (!TryParse(s, out var fileId))
            throw new FormatException($"Input File ID string '{s}' was in an incorrect format");

        return fileId;
    }

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    static bool IParsable<FileId>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FileId result) => TryParse(s, out result);

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, [MaybeNullWhen(false)] out FileId fileId)
    {
        if (Guid.TryParseExact(s, "D", out var guid) && guid.Version is 7)
        {
            fileId = new(guid, s.ToUpperInvariant());
            return true;
        }

        fileId = null;
        return false;
    }

    /// <summary>
    /// Gets the string representation of the file ID.
    /// </summary>
    public override string ToString() => String;

    /// <inheritdoc/>
    public override int GetHashCode() => Guid.GetHashCode();

    /// <summary>
    /// Determines whether the specified <see cref="FileId"/> is equal to the current <see cref="FileId"/>.
    /// </summary>
    public bool Equals(FileId? other) => Equals(this, other);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is FileId fileId && this == fileId;

    /// <summary>
    /// Determines whether two <see cref="FileId"/> instances are equal.
    /// </summary>
    public static bool Equals(FileId? left, FileId? right)
    {
        if (left is null)
            return right is null;

        if (right is null)
            return false;

        return left == right;
    }

    internal static FileId CreateSequential()
    {
        lock (_timeSync)
        {
            var nextTimeStamp = _lastTimeStamp.AddMilliseconds(1);
            var currentTimeStamp = DateTimeOffset.UtcNow;

            if (currentTimeStamp < nextTimeStamp)
                currentTimeStamp = nextTimeStamp;

            _lastTimeStamp = currentTimeStamp;

            return new(Guid.CreateVersion7(currentTimeStamp));
        }
    }
}
