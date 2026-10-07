using System.Runtime.InteropServices;

namespace FulcrumFS.Utilities;

/// <summary>
/// Flushes files and directories to disk so that the state a returned call relies on survives a power loss or operating system crash, not only a process
/// crash.
/// </summary>
/// <remarks>
/// <para>
/// Journalled file systems (ext4, XFS, NTFS, APFS) apply metadata operations atomically and in order but commit their journal lazily, so after a power loss
/// the file system is at an earlier consistent point in time: operations may be missing, but never a later one without the earlier ones. Flushing a file
/// forces the journal up to and including that file's metadata, so everything done before it is committed too. The repository relies on that order and
/// flushes only what a returned call depends on: the data files of an add, a promoted survivor before its rename, the in-group delete markers of a variant
/// retirement, the markers a committed deletion of a main file depends on, a staged transaction's delete journal when it is staged and again when it is
/// committed, the delete journal an ordinary transaction's commit writes (before and after its rename), and the repository info file when it is created or
/// upgraded.</para>
/// <para>
/// Deliberately not flushed: temporary work files, pre-commit cleanup hints (a hint without its delete marker is swept and the variant stays live), rebase
/// markers (recovery recomputes the survivor deterministically), the lock files, the indeterminate marker of an add (it is created before the add's data, so
/// flushing the data commits it too), and any deletion. A deletion undone by a power loss comes back only together with everything after it, including the
/// marker that drives it, so the cleaner finishes it.</para>
/// </remarks>
internal static partial class DurableIO
{
    private const int EINTR = 4;
    private const int EBADF = 9;
    private const int EINVAL = 22;

    // ENOTSUP is 95 on Linux and 45 on the BSDs (including Apple platforms).
    private static readonly int ENOTSUP = OperatingSystem.IsLinux() ? 95 : 45;

    // Close-on-exec is requested atomically through open(2)'s flags (O_RDONLY is 0 everywhere) so a process started concurrently on another thread never
    // inherits the descriptor. Its value differs per platform, but setting it afterwards through fcntl(2) is not an option: fcntl's third argument is
    // variadic, and Apple's arm64 ABI passes variadic arguments on the stack, where a fixed-signature P/Invoke never puts them. Elsewhere the descriptor is
    // briefly inheritable.
    private static readonly int OpenDirectoryFlags =
        OperatingSystem.IsLinux() ? 0x80000 :
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() ? 0x1000000 :
        OperatingSystem.IsFreeBSD() ? 0x100000 :
        0;

    /// <summary>
    /// Gets or sets a test-only observer invoked with the path (as <see cref="IAbsolutePath.PathExport"/> or <see cref="FileStream.Name"/>) of every file or
    /// directory flushed, including directory flushes that are no-ops on Windows. <see langword="null"/> in all production use.
    /// </summary>
    internal static Action<string>? Observer { get; set; }

    /// <summary>
    /// Flushes an existing file's data and metadata to disk and returns its length.
    /// </summary>
    /// <returns>The length of the file in bytes, read from the handle opened for the flush (an <c>fstat</c> on Unix), so it costs no path lookup or extra
    /// open. An add keeps it, so that the files it returns know their sizes without reading them from storage again.</returns>
    /// <remarks>
    /// The file is opened for writing because <c>FlushFileBuffers</c> requires write access on Windows, so this fails with a sharing violation there if another
    /// handle on the file does not share writing.
    /// </remarks>
    public static long FlushFile(IAbsoluteFilePath file)
    {
        long length;

        using (var handle = File.OpenHandle(file.PathExport, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            RandomAccess.FlushToDisk(handle);
            length = RandomAccess.GetLength(handle);
        }

        Observer?.Invoke(file.PathExport);
        return length;
    }

    /// <summary>
    /// Flushes the buffered data of a stream and then the file's data and metadata to disk.
    /// </summary>
    public static void FlushFile(FileStream stream)
    {
        stream.Flush(flushToDisk: true);
        Observer?.Invoke(stream.Name);
    }

    /// <summary>
    /// Flushes a directory's entries to disk, so that files created in, renamed into or removed from it are durable.
    /// </summary>
    /// <remarks>
    /// On Windows this is a no-op: NTFS records directory changes in its metadata log, which flushing any file of the volume forces to disk, so the file flush
    /// each call site performs covers them. On Unix the directory is opened and passed to <c>fsync</c>.
    /// </remarks>
    /// <exception cref="IOException">The directory could not be opened (for example because it does not exist) or flushed. A file system that does not
    /// support flushing directories (<c>EINVAL</c>, <c>EBADF</c> or <c>ENOTSUP</c> from <c>fsync</c>) is tolerated and the call returns normally.</exception>
    public static void FlushDirectory(IAbsoluteDirectoryPath directory)
    {
        if (!OperatingSystem.IsWindows())
            FlushDirectoryUnix(directory);

        Observer?.Invoke(directory.PathExport);
    }

    /// <summary>
    /// Flushes a newly created file and the directory containing it, for markers whose existence must be durable.
    /// </summary>
    public static void FlushNewFile(IAbsoluteFilePath file)
    {
        FlushFile(file);
        FlushDirectory(file.ParentDirectory);
    }

    private static void FlushDirectoryUnix(IAbsoluteDirectoryPath directory)
    {
        int fd;

        while ((fd = Open(directory.PathExport, OpenDirectoryFlags)) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();

            if (errno is not EINTR)
                throw CreateException(directory, "open", errno);
        }

        try
        {
            while (FSync(fd) is not 0)
            {
                int errno = Marshal.GetLastPInvokeError();

                if (errno is EINTR)
                    continue;

                // Some file systems (network mounts in particular) refuse fsync on a directory with EINVAL, EBADF or ENOTSUP. Throwing would fail every add
                // on such a mount, so, as PostgreSQL does for directory fsyncs, those are treated as "not supported here" and ignored: the directory entries
                // then rely on the file system's own journalling. Any other error is a real failure and propagates.
                if (errno == EINVAL || errno == EBADF || errno == ENOTSUP)
                    return;

                throw CreateException(directory, "fsync", errno);
            }
        }
        finally
        {
            // Not retried on EINTR: the descriptor is released regardless on Linux, and retrying could close one reused by another thread.
            _ = Close(fd);
        }
    }

    private static IOException CreateException(IAbsoluteDirectoryPath directory, string function, int errno)
    {
        return new IOException(
            $"Failed to flush directory '{directory.PathDisplay}' to disk: {function} failed with error {errno} ({Marshal.GetPInvokeErrorMessage(errno)}).",
            errno);
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FSync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
