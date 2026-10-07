using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FulcrumFS;

/// <summary>
/// Generates sequential file IDs backed by version 7 GUIDs, each greater than every ID the same generator created before it.
/// </summary>
/// <remarks>
/// <para>
/// An ID holds the Unix time in milliseconds when it was created, a 4-bit counter of the IDs created within that millisecond, and 70 cryptographically
/// random bits. Up to 16 IDs per millisecond carry the current time. Beyond that, or when the clock moves backwards, the generator moves its timestamp one
/// millisecond ahead instead of waiting for the clock, so a sustained burst runs the embedded timestamps ahead of the clock until the rate drops again.</para>
/// <para>
/// IDs from one generator compare in creation order both as <c>D</c> format strings (ordinal comparison) and as <see cref="System.Guid"/> values
/// (<see cref="Guid.CompareTo(Guid)"/>). No order is guaranteed between IDs from different generators, or between generators in successive runs of a process
/// whose clock reads earlier than the timestamps already used. Uniqueness across generators rests on the random bits, as it does for any version 7 GUID.</para>
/// <para>
/// Sequential IDs are predictable and reveal when they were created (see <see cref="FileId.Timestamp"/>). Use <see cref="FileId.CreateSecure"/> for IDs that
/// are exposed to untrusted parties.</para>
/// <para>
/// Every <c>FileRepo</c> owns a generator of its own. <see cref="Shared"/> is the process-wide instance behind <see cref="FileId.CreateSequential"/>; code that
/// creates many IDs should use its own generator so that it does not contend for the shared lock or share its 16 IDs per millisecond with other callers.</para>
/// </remarks>
public sealed class FileIdGenerator
{
    // RFC 9562 section 6.2, method 1 (fixed bit-length dedicated counter): the high 4 bits of the 12-bit rand_a field count the IDs within a millisecond.
    private const int CounterLimit = 16;

    private readonly TimeProvider _timeProvider;
    private readonly Lock _sync = new();

    // Starts below any clock reading (which is clamped to 0 or later) so the first ID always takes the clock's time.
    private long _lastTimestamp = -1;
    private int _counter;

    /// <summary>
    /// Gets the process-wide generator used by <see cref="FileId.CreateSequential"/>.
    /// </summary>
    public static FileIdGenerator Shared { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FileIdGenerator"/> class that reads the time from <see cref="TimeProvider.System"/>.
    /// </summary>
    public FileIdGenerator() : this(TimeProvider.System) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileIdGenerator"/> class that reads the time from the specified time provider.
    /// </summary>
    /// <param name="timeProvider">The time provider whose <see cref="TimeProvider.GetUtcNow"/> supplies the timestamps embedded in the generated IDs.</param>
    public FileIdGenerator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates a new sequential file ID backed by a version 7 GUID that is greater than every ID previously created by this generator. This method is
    /// thread-safe.
    /// </summary>
    public FileId CreateSequential()
    {
        long timestamp;
        int counter;

        using (_sync.EnterScope())
        {
            // The timestamp field is unsigned, so a clock reading before the Unix epoch is treated as the epoch itself.
            long now = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), 0);

            if (now > _lastTimestamp)
            {
                _lastTimestamp = now;
                _counter = 0;
            }
            else if (++_counter == CounterLimit)
            {
                // The clock has not passed the last timestamp (it may even have gone backwards) and 16 IDs have used that timestamp: take the next
                // millisecond rather than wait for the clock, so IDs keep increasing while the timestamps run ahead.
                _counter = 0;
                _lastTimestamp++;
            }

            timestamp = _lastTimestamp;
            counter = _counter;
        }

        // RFC 9562 byte order: 48-bit big-endian timestamp, version 7 and the counter in byte 6, random byte 7, variant bits 10 and 6 random bits in byte 8,
        // then 56 random bits.
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)timestamp << 16);
        bytes[6] = (byte)(0x70 | counter);
        RandomNumberGenerator.Fill(bytes[7..]);
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));

        return new FileId(new Guid(bytes, bigEndian: true));
    }
}
