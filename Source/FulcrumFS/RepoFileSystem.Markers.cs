using System.Text;

namespace FulcrumFS;

/// <content>
/// Marker-file primitives: creating exclusive/shared marker handles, appending log entries, and clearing indeterminate state. The marker file logging mode is
/// captured at construction so callsites do not pass it on every call.
/// </content>
partial class RepoFileSystem
{
    /// <summary>
    /// Creates a new marker file, logs an entry to it, and returns a disposable that keeps the marker file open with exclusive access until disposed. The
    /// marker file must not already exist. This is used to indicate that a transaction is actively holding the marker - other operations attempting to open
    /// the marker (e.g. cleanup probing with <see cref="FileShare.None"/>) will receive a sharing violation until the returned disposable is disposed.
    /// </summary>
    public async Task<IAsyncDisposable> CreateExclusiveMarkerAsync(IAbsoluteFilePath markerFile, string header, object message)
    {
        var stream = markerFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.None);

        try
        {
            await WriteMarkerEntryAsync(stream, header, message).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a new marker file OR takes over an existing one with exclusive (<see cref="FileShare.None"/>) access. Returns a disposable that keeps the
    /// marker open with no sharing until disposed; other actors (foreground or cleaner) probing the marker with <see cref="FileShare.None"/> will receive a
    /// sharing violation and must back off. If the marker already exists from a prior crashed operation, the existing content is preserved and the new log
    /// entry is appended. If another holder is currently active (cleaner mid-recovery, or a concurrent foreground), the takeover open throws an
    /// <see cref="IOException"/>; callers must handle that as "another operation is in progress, retry later".
    /// </summary>
    public async Task<IAsyncDisposable> CreateOrTakeoverExclusiveMarkerAsync(IAbsoluteFilePath markerFile, string header, object message)
    {
        // Open exclusively, creating the file if it doesn't exist (e.g. first attempt) or taking it over if a prior crashed attempt left it behind. If
        // another holder is currently active, this throws IOException - the caller treats that as a transient "operation in progress" condition. Seek to
        // end so any prior crashed-attempt content is preserved and the new entry is appended.
        var stream = markerFile.OpenAsyncStream(FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.Seek(0, SeekOrigin.End);

        try
        {
            await WriteMarkerEntryAsync(stream, header, message).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Takes over exclusive (<see cref="FileShare.None"/>) ownership of an existing marker file without creating it or writing any entry to it. The
    /// returned stream is positioned at the end of the file and is intended to receive failure-log entries via <see cref="WriteMarkerEntryAsync"/> if needed.
    /// Throws <see cref="IOException"/> if the marker does not exist or another actor currently holds it with non-shared access; callers treat both as
    /// "not my turn, skip".
    /// </summary>
    /// <remarks>
    /// This is the no-create, no-write sibling of <see cref="CreateOrTakeoverExclusiveMarkerAsync"/>. Used by the cleaner to acquire ownership of a delete
    /// hint for the duration of a delete attempt without polluting the marker file with a "clean attempt" entry on every pass.
    /// </remarks>
    public static FileStream TakeoverExclusiveMarker(IAbsoluteFilePath markerFile)
    {
        var stream = markerFile.OpenAsyncStream(FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    /// <summary>
    /// Opens or creates a marker file, appends a log entry, and returns a disposable that keeps the marker file open with shared access until disposed.
    /// Multiple callers may concurrently hold the returned handle on the same marker, and any holder may delete the marker while another still has it open.
    /// Cleanup operations attempting to open the marker exclusively (with <see cref="FileShare.None"/>) will be blocked while any handle remains open.
    /// </summary>
    public async Task<IAsyncDisposable> OpenOrCreateSharedMarkerAsync(IAbsoluteFilePath markerFile, string header, object message)
    {
        var stream = markerFile.OpenAsyncStream(FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Delete);

        try
        {
            await WriteMarkerEntryAsync(stream, header, message).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Logs an entry to a marker file, if it already exists. Errors are silently swallowed - the marker may or may not end up existing on disk.
    /// </summary>
    public async Task LogToOptionalMarkerAsync(IAbsoluteFilePath markerFile, string header, object message)
    {
        // Nothing to log and we don't need to ensure the marker exists (caller said "optional") - skip the open entirely.
        if (MarkerFileLogging is not RepoLoggingMode.HumanReadable)
            return;

        FileStream? stream = await TryOpenForLoggingAsync(markerFile, FileMode.Open).ConfigureAwait(false);

        if (stream is null)
            return;

        try
        {
            await SeekAndWriteMarkerEntryWithLockAsync(stream, header, message).ConfigureAwait(false);
        }
        catch (IOException) { }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Logs an entry to a marker file. Errors are silently swallowed only if the marker file ends up existing on disk; otherwise the exception is propagated to
    /// the caller.
    /// </summary>
    public async Task LogToMarkerAsync(IAbsoluteFilePath markerFile, string header, object message)
    {
        FileStream? stream = await TryOpenForLoggingAsync(markerFile, FileMode.Append).ConfigureAwait(false);

        if (stream is null)
        {
            // Couldn't open after retries. If the marker exists on disk, swallow (the entry is best-effort); otherwise propagate so the caller knows the
            // marker was never written. Reaching this branch with markerFile.State == DoesNotExist requires a transient open failure that cleared by the
            // time we re-probed, which is essentially impossible - the existence check exists to preserve the original contract that LogToMarkerAsync
            // surfaces "marker does not exist" failures to the caller.
            if (markerFile.State is EntryState.Exists)
                return;

            throw new IOException($"Failed to open marker file '{markerFile.PathDisplay}' for logging after multiple attempts.");
        }

        try
        {
            await WriteMarkerEntryAsync(stream, header, message).ConfigureAwait(false);
        }
        catch (IOException) { }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens a marker file for logging, retrying briefly on transient sharing-violation <see cref="IOException"/>s caused by concurrent holders. Returns
    /// <see langword="null"/> if all attempts fail or if the marker does not exist (when opening with <see cref="FileMode.Open"/>). Grants
    /// <see cref="FileShare.Write"/> so concurrent shared holders (e.g. <see cref="OpenOrCreateSharedMarkerAsync"/>) don't block this open.
    /// </summary>
    private static async Task<FileStream?> TryOpenForLoggingAsync(IAbsoluteFilePath markerFile, FileMode mode)
    {
        const int MaxAttempts = 3;
        const int RetryDelayMs = 25;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelayMs).ConfigureAwait(false);

            try
            {
                return markerFile.OpenAsyncStream(mode, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                // Permanent condition for FileMode.Open - no point retrying. (FileMode.Append cannot raise this since it creates on absence.)
                return null;
            }
            catch (IOException) { }
        }

        return null;
    }

    /// <summary>
    /// Sentinel offset used for the cross-writer coordination range lock in <see cref="SeekAndWriteMarkerEntryWithLockAsync"/>. Placed far beyond any plausible
    /// marker file content so the lock never overlaps real bytes; important on Windows, where range locks are mandatory: locking real content would
    /// briefly block external tools (Notepad, log viewers, backup software) from reading those bytes while we hold the lock. By locking only a
    /// non-existent virtual byte, the file remains fully readable by outside tools throughout the write.
    /// </summary>
    private const long WriteLockSentinelOffset = 1L << 62;

    /// <summary>
    /// Writes a single log entry to a marker stream in a single <see cref="FileStream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> call. Performs no
    /// locking or seeking; safe to use only when the stream is opened with <see cref="FileMode.Append"/> (O_APPEND kernel atomicity serializes concurrent
    /// writers and seeks to real EOF on every write) OR when the caller holds the file with <see cref="FileShare.None"/> (no concurrent writers possible).
    /// For shared non-append streams use <see cref="SeekAndWriteMarkerEntryWithLockAsync"/> instead.
    /// </summary>
    public async Task WriteMarkerEntryAsync(FileStream stream, string header, object message)
    {
        if (MarkerFileLogging is not RepoLoggingMode.HumanReadable)
            return;

        string entry =
            $"=============== {header} ===============\r\n\r\n" +
            $"Timestamp: {DateTimeOffset.Now}\r\n\r\n" +
            $"{message}\r\n\r\n\r\n";

        byte[] buffer = Encoding.UTF8.GetBytes(entry);
        await stream.WriteAsync(buffer).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a single log entry to a shared non-append marker stream, serializing against concurrent writers via an OS-level sentinel range lock (see
    /// <see cref="WriteLockSentinelOffset"/> for the reader-friendliness rationale). After the lock is acquired the position is seeked to end of file (real
    /// EOF may have advanced since the stream was opened by another writer) and the entry is emitted in a single
    /// <see cref="FileStream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> call so the kernel-level write itself is atomic. Skipped on macOS
    /// because <see cref="FileStream.Lock"/> is not implemented there; on macOS this becomes best-effort (acceptable for the only current caller,
    /// <see cref="LogToOptionalMarkerAsync"/>, whose contract already tolerates lost entries).
    /// </summary>
    private async Task SeekAndWriteMarkerEntryWithLockAsync(FileStream stream, string header, object message)
    {
        if (MarkerFileLogging is not RepoLoggingMode.HumanReadable)
            return;

        const int MaxLockAttempts = 4;
        const int LockRetryDelayMs = 25;

        bool locked = false;

        if (!OperatingSystem.IsMacOS())
        {
            for (int attempt = 0; attempt < MaxLockAttempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(LockRetryDelayMs).ConfigureAwait(false);

                try
                {
                    stream.Lock(WriteLockSentinelOffset, 1);
                    locked = true;
                    break;
                }
                catch (IOException) { }
            }

            if (!locked)
                throw new IOException("Failed to acquire marker file lock for writing after multiple attempts.");
        }

        try
        {
            stream.Seek(0, SeekOrigin.End);
            await WriteMarkerEntryAsync(stream, header, message).ConfigureAwait(false);
        }
        finally
        {
            if (locked && !OperatingSystem.IsMacOS())
            {
                try { stream.Unlock(WriteLockSentinelOffset, 1); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Clears the indeterminate state for the specified file by deleting its indeterminate marker. This is a best-effort operation - if the marker cannot be
    /// deleted, the failure is logged to the marker itself and the file remains in indeterminate state for a later cleanup attempt.
    /// </summary>
    public async ValueTask TryClearIndeterminateStateAsync(FileId fileId)
    {
        var indeterminateMarker = GetIndeterminateMarker(fileId);

        try
        {
            indeterminateMarker.Delete(ignoreNotFound: true);
        }
        catch (IOException ex)
        {
            await LogToOptionalMarkerAsync(indeterminateMarker, "DELETE MARKER ATTEMPT FAILED", ex).ConfigureAwait(false);
        }
    }
}
