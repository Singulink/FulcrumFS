namespace FulcrumFS;

/// <content>
/// Contains marker-file helpers for the file repository: creating exclusive/shared marker handles, appending log entries, and clearing indeterminate state.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Creates a new marker file, logs an entry to it, and returns a disposable that keeps the marker file open with exclusive access until disposed. The
    /// marker file must not already exist. This is used to indicate that a transaction is actively holding the marker - other operations attempting to open
    /// the marker (e.g. cleanup probing with <see cref="FileShare.None"/>) will receive a sharing violation until the returned disposable is disposed.
    /// </summary>
    internal static async Task<IAsyncDisposable> CreateExclusiveMarkerAsync(IAbsoluteFilePath markerFile, string header, object message, LoggingMode markerFileLogging)
    {
        var stream = markerFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.None);

        try
        {
            await WriteMarkerEntryAsync(stream, header, message, markerFileLogging).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens or creates a marker file, appends a log entry, and returns a disposable that keeps the marker file open with shared access until disposed.
    /// Multiple callers may concurrently hold the returned handle on the same marker, and any holder may delete the marker while another still has it open.
    /// Cleanup operations attempting to open the marker exclusively (with <see cref="FileShare.None"/>) will be blocked while any handle remains open.
    /// </summary>
    internal static async Task<IAsyncDisposable> OpenOrCreateSharedMarkerAsync(IAbsoluteFilePath markerFile, string header, object message, LoggingMode markerFileLogging)
    {
        var stream = markerFile.OpenAsyncStream(FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Delete);

        try
        {
            await WriteMarkerEntryAsync(stream, header, message, markerFileLogging).ConfigureAwait(false);
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
    internal static async Task LogToOptionalMarkerAsync(IAbsoluteFilePath markerFile, string header, object message, LoggingMode markerFileLogging)
    {
        try
        {
            var stream = markerFile.OpenAsyncStream(FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete);
            stream.Seek(0, SeekOrigin.End);

            await using (stream.ConfigureAwait(false))
                await WriteMarkerEntryAsync(stream, header, message, markerFileLogging).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Logs an entry to a marker file. Errors are silently swallowed only if the marker file ends up existing on disk; otherwise the exception is propagated to
    /// the caller.
    /// </summary>
    internal static async Task LogToMarkerAsync(IAbsoluteFilePath markerFile, string header, object message, LoggingMode markerFileLogging)
    {
        bool opened = false;

        try
        {
            var stream = markerFile.OpenAsyncStream(FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            opened = true;

            await using (stream.ConfigureAwait(false))
                await WriteMarkerEntryAsync(stream, header, message, markerFileLogging).ConfigureAwait(false);
        }
        catch (IOException) when (opened || markerFile.State is EntryState.Exists) { }
    }

    private static async Task WriteMarkerEntryAsync(Stream stream, string header, object message, LoggingMode markerFileLogging)
    {
        if (markerFileLogging is not LoggingMode.HumanReadable)
            return;

        var sw = new StreamWriter(stream, leaveOpen: true);

        string entry =
            $"=============== {header} ===============\r\n\r\n" +
            $"Timestamp: {DateTimeOffset.Now}\r\n\r\n" +
            $"{message}\r\n\r\n";

        await using (sw.ConfigureAwait(false))
            await sw.WriteLineAsync(entry).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the indeterminate state for the specified file by deleting its indeterminate marker. This is a best-effort operation - if the marker cannot be
    /// deleted, the failure is logged to the marker itself and the file remains in indeterminate state for a later cleanup attempt.
    /// </summary>
    internal static async ValueTask ClearIndeterminateStateAsync(IAbsoluteDirectoryPath cleanupDirectory, FileId fileId, LoggingMode markerFileLogging)
    {
        var indeterminateMarker = GetIndeterminateMarker(cleanupDirectory, fileId);

        // If we can't delete the indeterminate marker, log the error but continue.
        // This only needs to be a "best effort" - file will stay in indeterminate state and another attempt to delete the marker will be made later.

        try
        {
            indeterminateMarker.Delete(ignoreNotFound: true);
        }
        catch (IOException ex)
        {
            await LogToOptionalMarkerAsync(indeterminateMarker, "DELETE MARKER ATTEMPT FAILED", ex, markerFileLogging).ConfigureAwait(false);
        }
    }
}
