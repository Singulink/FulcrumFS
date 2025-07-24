using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace FulcrumFS;

/// <summary>
/// Represents a validated file identifier.
/// </summary>
public readonly struct FileId : IParsable<FileId>, IEquatable<FileId>
{
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
    public Guid Guid => field != Guid.Empty ? field : throw new InvalidOperationException("FileID instance was not initialized.");

    internal string String => field ?? throw new InvalidOperationException("FileID instance was not initialized.");

    /// <summary>
    /// Initializes a new instance of the <see cref="FileId"/> struct with the specified GUID.
    /// </summary>
    public FileId(Guid guid)
    {
        if (guid == Guid.Empty)
            throw new ArgumentException("File ID cannot be all zeros", nameof(guid));

        Guid = guid;
        String = guid.ToString("N");
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
        var guid = Guid.ParseExact(s, "N");

        if (guid == Guid.Empty)
            throw new FormatException("File ID cannot be all zeros.");

        return new FileId(guid, s);
    }

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FileId result) => TryParse(s, out result);

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, [MaybeNullWhen(false)] out FileId result)
    {
        if (Guid.TryParseExact(s, "N", out var guid) && guid != Guid.Empty)
        {
            result = new(guid, s);
            return true;
        }

        result = default;
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
    public bool Equals(FileId other) => this == other;

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is FileId fileId && this == fileId;

    internal static FileId CreateRandom()
    {
        byte[] bytes = new byte[16];

        Start:

        RandomNumberGenerator.Fill(bytes);
        var guid = new Guid(bytes);

        // Ensure we don't return a zero-filled GUID

        if (guid == Guid.Empty)
            goto Start;

        return new(guid);
    }
}
