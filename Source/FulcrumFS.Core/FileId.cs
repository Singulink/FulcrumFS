using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace FulcrumFS;

/// <summary>
/// Represents a validated file identifier.
/// </summary>
public sealed class FileId : ISpanParsable<FileId>, IEquatable<FileId>
{
    private static readonly long _maxTimestampMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>
    /// Implicitly converts a <see cref="FileId"/> instance to a <see cref="System.Guid"/>.
    /// </summary>
    public static implicit operator Guid(FileId fileId) => fileId.Guid;

    /// <summary>
    /// Converts a <see cref="System.Guid"/> to a <see cref="FileId"/>.
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

    /// <summary>
    /// Gets the directory path where the file and its variants are stored relative to the repository's <see cref="FileRepoPaths.FilesDirectoryName"/>
    /// subdirectory.
    /// </summary>
    public IRelativeDirectoryPath RelativeDirectory => field ??= GetRelativeDirectory();

    /// <summary>
    /// Gets the creation time embedded in a sequential (version 7) file ID, with millisecond precision and a zero offset, or <see langword="null"/> for a
    /// secure (version 4) file ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is the 48-bit big-endian count of milliseconds since the Unix epoch held in the first six bytes of the GUID in RFC 9562 byte order. For an ID
    /// created by a <see cref="FileIdGenerator"/> it is the time the ID was created, or slightly later if a burst of IDs ran the generator's timestamps ahead
    /// of the clock.</para>
    /// <para>
    /// A version 7 ID that was not created by this library can hold a timestamp later than <see cref="DateTimeOffset.MaxValue"/>; its value is also
    /// <see langword="null"/>.</para>
    /// </remarks>
    public DateTimeOffset? Timestamp
    {
        get {
            if (Guid.Version is not 7)
                return null;

            Span<byte> bytes = stackalloc byte[16];
            Guid.TryWriteBytes(bytes, bigEndian: true, out _);
            long milliseconds = (long)(BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 16);

            return milliseconds <= _maxTimestampMilliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
        }
    }

    private string FileIdString => field ??= Guid.ToString("D");

    /// <summary>
    /// Initializes a new instance of the <see cref="FileId"/> class with the specified GUID.
    /// </summary>
    public FileId(Guid guid)
    {
        if (!IsValidGuidForFileId(guid))
            throw new ArgumentException("Invalid File ID.");

        Guid = guid;
    }

    private FileId(Guid guid, string? s)
    {
        Guid = guid;
        FileIdString = s;
    }

    /// <summary>
    /// Creates a new file ID from the specified GUID.
    /// </summary>
    public static FileId Create(Guid guid) => new(guid);

    /// <summary>
    /// Creates a new sequential file ID backed by a version 7 GUID using the process-wide <see cref="FileIdGenerator.Shared"/> generator.
    /// </summary>
    /// <remarks>
    /// The shared generator's lock and its budget of 16 IDs per millisecond (beyond which timestamps run ahead of the clock) are shared by every caller in
    /// the process. Code that creates many IDs should create a <see cref="FileIdGenerator"/> of its own and call <see cref="FileIdGenerator.CreateSequential"/>
    /// on it instead.
    /// </remarks>
    public static FileId CreateSequential() => FileIdGenerator.Shared.CreateSequential();

    /// <summary>
    /// Creates a new cryptographically random file ID backed by a version 4 GUID.
    /// </summary>
    public static FileId CreateSecure() => new(Guid.NewGuid());

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
    public static FileId Parse(ReadOnlySpan<char> s)
    {
        if (!TryParse(s, out var fileId))
            throw new FormatException($"Input File ID string '{s}' was in an incorrect format");

        return fileId;
    }

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, [MaybeNullWhen(false)] out FileId fileId) => TryParse(s.AsSpan(), out fileId);

    /// <summary>
    /// NOTE!!! For internal use only - accepts only the lowercase D format that file IDs have in repository file and directory names, rejecting any other
    /// format or casing, and keeps the string as the file ID's string representation.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool TryParseUnsafe([NotNullWhen(true)] string? s, [MaybeNullWhen(false)] out FileId fileId)
    {
        // The D format is exactly 36 characters, so the length check also rejects surrounding whitespace. An uppercase name is not one the repository wrote:
        // on a case-sensitive file system it is a different entry from the lowercase one.
        if (s?.Length is 36 && !s.AsSpan().ContainsAnyInRange('A', 'Z') && Guid.TryParseExact(s, "D", out var guid) && IsValidGuidForFileId(guid))
        {
            fileId = new(guid, s);
            return true;
        }

        fileId = null;
        return false;
    }

    /// <summary>
    /// Parses the specified string representation of a file identifier.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> s, [MaybeNullWhen(false)] out FileId fileId)
    {
        if (Guid.TryParseExact(s, "D", out var guid) && IsValidGuidForFileId(guid))
        {
            fileId = new(guid, null);
            return true;
        }

        fileId = null;
        return false;
    }

    /// <summary>
    /// Returns the file path to a main file or its variant relative to the repository's <see cref="FileRepoPaths.FilesDirectoryName"/> subdirectory.
    /// </summary>
    public IRelativeFilePath GetRelativeFilePath(string? variantId, string extension)
    {
        extension = FileExtension.Normalize(extension);
        string fileName = string.IsNullOrEmpty(variantId) ? FileRepoPaths.MainFileName : VariantId.Normalize(variantId);
        return GetRelativeDirectory().CombineFile(fileName + extension);
    }

    /// <summary>
    /// Determines whether the specified <see cref="FileId"/> is equal to the current <see cref="FileId"/>.
    /// </summary>
    public bool Equals(FileId? other) => Equals(this, other);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is FileId fileId && this == fileId;

    /// <inheritdoc/>
    public override int GetHashCode() => Guid.GetHashCode();

    /// <summary>
    /// Gets the string representation of the file ID.
    /// </summary>
    public override string ToString() => FileIdString;

    private static bool IsValidGuidForFileId(Guid guid) => guid.Version is 4 or 7;

    private IRelativeDirectoryPath GetRelativeDirectory()
    {
        var fileIdSpan = FileIdString.AsSpan();
        return DirectoryPath.ParseRelative($"{fileIdSpan[9..11]}/{fileIdSpan[11..13]}/{fileIdSpan}", PathFormat.Universal, PathOptions.None);
    }

    #region Explicit Interface Implementations

    /// <inheritdoc/>
    static FileId IParsable<FileId>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static FileId ISpanParsable<FileId>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool IParsable<FileId>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FileId result)
    {
        return TryParse(s, out result);
    }

    /// <inheritdoc/>
    static bool ISpanParsable<FileId>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out FileId result)
    {
        return TryParse(s, out result);
    }

    #endregion
}
