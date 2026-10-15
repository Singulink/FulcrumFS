#pragma warning disable SA1649 // File name should match first type name

namespace FulcrumFS;

/// <summary>
/// A stream that fails when it is closed and is closed all the same, as a stream that writes out what it buffered when it is closed does when the write
/// fails. It fails the first time it is closed, or every time.
/// </summary>
internal sealed class CloseFailingStream : MemoryStream
{
    public const string FailureMessage = "Simulated failure to close the stream.";

    private readonly bool _failEveryTime;
    private bool _isClosed;

    public CloseFailingStream(bool failEveryTime = false) => _failEveryTime = failEveryTime;

    public override ValueTask DisposeAsync()
    {
        if (_isClosed && !_failEveryTime)
            return ValueTask.CompletedTask;

        _isClosed = true;
        Dispose();

        return ValueTask.FromException(new IOException(FailureMessage));
    }
}

/// <summary>
/// A stream that counts how many times it is closed.
/// </summary>
internal sealed class CloseCountingStream : MemoryStream
{
    public CloseCountingStream(byte[] content) : base(content) { }

    public int CloseCount { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseCount++;

        base.Dispose(disposing);
    }
}
