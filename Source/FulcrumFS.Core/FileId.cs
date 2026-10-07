using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace FulcrumFS;

/// <summary>
/// Represents a validated file identifier.
/// </summary>
public sealed class FileId : ISpanParsable<FileId>, IEquatable<FileId>
{
    private const int OrderedBytesLength = 16;

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
    /// Determines whether two <see cref="FileId"/> instances are equal. Either operand may be <see langword="null"/>; two
    /// <see langword="null"/> operands are equal, and <see langword="null"/> equals no instance.
    /// </summary>
    /// <remarks>
    /// The operators accept <see langword="null"/> because frameworks compare values generically: Entity Framework Core, for one,
    /// compares every reference-type property with <see langword="null"/> through this operator when it takes original values, so
    /// an operator that rejected <see langword="null"/> made the type impossible to map.
    /// </remarks>
    public static bool operator ==(FileId? left, FileId? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null && left.Guid == right.Guid);

    /// <summary>
    /// Determines whether two <see cref="FileId"/> instances are not equal. Either operand may be <see langword="null"/>.
    /// </summary>
    public static bool operator !=(FileId? left, FileId? right) => !(left == right);

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
    /// Creates a file ID from the 16-byte ordered encoding written by <see cref="ToOrderedBytes()"/>.
    /// </summary>
    /// <remarks>
    /// The encoding is not RFC 9562 byte order; see <see cref="ToOrderedBytes()"/>. To create a file ID from bytes in RFC 9562 byte order, convert them
    /// with <see cref="System.Guid.Guid(ReadOnlySpan{byte}, bool)"/> with <c>bigEndian</c> set to <see langword="true"/> and pass the GUID to
    /// <see cref="Create(System.Guid)"/>.
    /// </remarks>
    /// <param name="source">The 16 bytes of the ordered encoding.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not exactly 16 bytes long, or the bytes do not encode a valid file ID.</exception>
    public static FileId FromOrderedBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length is not OrderedBytesLength)
            throw new ArgumentException($"The ordered encoding of a file ID must be exactly {OrderedBytesLength} bytes long.", nameof(source));

        // The reverse of the nibble map in ToOrderedBytes(Span<byte>): the version nibble returns to the high nibble of byte 6, and the timestamp moves one
        // nibble left into bytes 0-5.
        Span<byte> bytes = stackalloc byte[16];

        bytes[0] = (byte)(((source[0] & 0x0F) << 4) | (source[1] >> 4));
        bytes[1] = (byte)(((source[1] & 0x0F) << 4) | (source[2] >> 4));
        bytes[2] = (byte)(((source[2] & 0x0F) << 4) | (source[3] >> 4));
        bytes[3] = (byte)(((source[3] & 0x0F) << 4) | (source[4] >> 4));
        bytes[4] = (byte)(((source[4] & 0x0F) << 4) | (source[5] >> 4));
        bytes[5] = (byte)(((source[5] & 0x0F) << 4) | (source[6] >> 4));
        bytes[6] = (byte)((source[0] & 0xF0) | (source[6] & 0x0F));
        source[7..].CopyTo(bytes[7..]);

        return new FileId(new Guid(bytes, bigEndian: true));
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
    /// Returns the 16-byte ordered encoding of the file ID, under which sequential (version 7) IDs sort in the order they were created and after every
    /// secure (version 4) ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The encoding is for storing file IDs in an ordered binary database column, so that an index over the column receives new sequential IDs at its end
    /// instead of throughout it. Store it in a fixed 16-byte binary column: <c>binary(16)</c> on SQL Server, whose <c>uniqueidentifier</c> type orders values
    /// by their last six bytes first, and <c>bytea</c> or <c>BLOB</c> on other databases.</para>
    /// <para>
    /// Under ordinal comparison of the bytes (unsigned, first byte first, as databases compare binary values), the encoding of every version 7 ID is greater
    /// than the encoding of every version 4 ID, and version 7 IDs compare by their timestamp and then their counter, so the IDs from one
    /// <see cref="FileIdGenerator"/> compare in the order they were created. A repository that switches its <c>FileIdMode</c> from secure to sequential IDs
    /// therefore adds its new IDs after all of its old ones. In RFC 9562 byte order the same version 7 IDs would fall in a narrow band in the middle of the
    /// version 4 IDs, whose bytes are random, because a version 7 ID starts with the high byte of its timestamp, which is <c>0x01</c> from late 2004 to late
    /// 2039.</para>
    /// <para>
    /// The encoding is not RFC 9562 byte order: it moves the version nibble in front of the timestamp. Do not store values in this encoding and in RFC 9562
    /// byte order in the same column; the same ID has different bytes in each, and the two orders disagree. Callers that want RFC 9562 byte order instead
    /// can use <see cref="System.Guid.ToByteArray(bool)"/> on <see cref="Guid"/> with <c>bigEndian</c> set to <see langword="true"/>. Use
    /// <see cref="FromOrderedBytes(ReadOnlySpan{byte})"/> to create a file ID from its ordered encoding.</para>
    /// </remarks>
    public byte[] ToOrderedBytes()
    {
        byte[] bytes = new byte[OrderedBytesLength];
        ToOrderedBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Writes the 16-byte ordered encoding of the file ID to the specified span. See <see cref="ToOrderedBytes()"/> for the order and its use.
    /// </summary>
    /// <param name="destination">The span to write the 16 bytes to.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 16 bytes.</exception>
    public void ToOrderedBytes(Span<byte> destination)
    {
        if (destination.Length < OrderedBytesLength)
            throw new ArgumentException($"The destination must be at least {OrderedBytesLength} bytes long.", nameof(destination));

        // The encoding permutes the 32 nibbles of the RFC 9562 bytes. With 0-b the twelve nibbles of the 48-bit timestamp from most to least significant,
        // V the version nibble, C the counter nibble (random in a version 4 ID, as are the timestamp nibbles) and r the remaining random and variant nibbles:
        //
        //   byte       0  1  2  3  4  5  6  7  8 .. 15
        //   RFC 9562   01 23 45 67 89 ab VC rr rr .. rr
        //   ordered    V0 12 34 56 78 9a bC rr rr .. rr
        //
        // The version nibble moves to the front, the timestamp moves one nibble right to make room for it, and bytes 7-15 are unchanged.
        Span<byte> bytes = stackalloc byte[16];
        Guid.TryWriteBytes(bytes, bigEndian: true, out _);

        destination[0] = (byte)((bytes[6] & 0xF0) | (bytes[0] >> 4));
        destination[1] = (byte)(((bytes[0] & 0x0F) << 4) | (bytes[1] >> 4));
        destination[2] = (byte)(((bytes[1] & 0x0F) << 4) | (bytes[2] >> 4));
        destination[3] = (byte)(((bytes[2] & 0x0F) << 4) | (bytes[3] >> 4));
        destination[4] = (byte)(((bytes[3] & 0x0F) << 4) | (bytes[4] >> 4));
        destination[5] = (byte)(((bytes[4] & 0x0F) << 4) | (bytes[5] >> 4));
        destination[6] = (byte)(((bytes[5] & 0x0F) << 4) | (bytes[6] & 0x0F));
        bytes[7..].CopyTo(destination[7..]);
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
