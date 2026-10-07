using System.Diagnostics;

namespace FulcrumFS;

/// <summary>
/// Represents information about a file stored in a <see cref="FileRepo"/>, including its identity and a recommended way to open it.
/// </summary>
public sealed class RepoFileInfo : IRepoFile
{
    // The size of the file once it is known, or -1 until then: set by the add that returned the instance, or read from the file system on first use. Stored
    // files never change, so the cached value cannot go stale. It is accessed with Volatile so that a concurrent caller never sees a torn value on a 32-bit
    // platform.
    private long _length = -1;

    /// <summary>
    /// Gets the file ID of the file.
    /// </summary>
    public FileId FileId { get; }

    /// <summary>
    /// Gets the variant ID of the file, or <see langword="null"/> if this represents the main file.
    /// </summary>
    public string? VariantId { get; }

    /// <summary>
    /// Gets the underlying absolute path of the file.
    /// </summary>
    /// <remarks>
    /// <para>Prefer calling <see cref="Open"/> to read repository files. Opening or otherwise accessing the file directly via this path requires understanding
    /// the sharing and deletion semantics used by the repository. Using inappropriate <see cref="FileMode"/>, <see cref="FileAccess"/>, or
    /// <see cref="FileShare"/> values can interfere with the repository's cleanup, transaction, and concurrency behavior. Accessing the file
    /// directly through this path results in undefined behavior and is not supported. Use at your own risk.</para>
    /// </remarks>
    public IAbsoluteFilePath Path { get; }

    /// <summary>
    /// Gets the extension of the file (including the leading period), or an empty string if it has no extension.
    /// </summary>
    public string Extension => Path.Extension;

    /// <summary>
    /// Gets the size of the file in bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An instance returned by an add knows the size already: the add takes it from the file it has just stored, so it is never read from the file system
    /// again. The variants that <see cref="FileRepo.GetOrAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>
    /// finds already stored are the exception and behave like fetched files.</para>
    /// <para>
    /// An instance returned by a fetch reads the size from the file system the first time it is needed and keeps it: stored files never change, so it is the
    /// size the file had when it was fetched. Reading it for the first time after the file has been physically removed throws an
    /// <see cref="IOException"/>; a value read before that stays available. For a live read of the file on disk use <see cref="Path"/>.</para>
    /// </remarks>
    /// <exception cref="IOException">The size has not been read yet and the file has been physically removed since it was fetched.</exception>
    public long Length => GetLength();

    internal RepoFileInfo(FileId fileId, string? variantId, IAbsoluteFilePath path)
    {
        FileId = fileId;
        VariantId = variantId;
        Path = path;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoFileInfo"/> class for a file whose size is already known, which it then never reads from the file
    /// system.
    /// </summary>
    internal RepoFileInfo(FileId fileId, string? variantId, IAbsoluteFilePath path, long length) : this(fileId, variantId, path)
    {
        Debug.Assert(length >= 0, "A known length cannot be negative.");
        _length = length;
    }

    /// <summary>
    /// Opens an asynchronous read-only file stream using the recommended sharing options for repository files
    /// (<see cref="FileShare.Read"/> | <see cref="FileShare.Delete"/>).
    /// </summary>
    /// <param name="accessPattern">How the caller is going to read the stream. The file is opened with <see cref="FileOptions.SequentialScan"/> for
    /// <see cref="FileAccessPattern.Sequential"/> and <see cref="FileOptions.RandomAccess"/> for <see cref="FileAccessPattern.Random"/>, which the operating
    /// system uses as caching and read-ahead hints. The stream supports reading and seeking either way.</param>
    /// <returns>A new <see cref="FileStream"/> opened for reading.</returns>
    /// <remarks>
    /// This is the recommended way to read a repository file. The returned stream allows other readers to access the file concurrently and allows the
    /// repository to delete the file while it is still open.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="accessPattern"/> is not a defined <see cref="FileAccessPattern"/> value.</exception>
    public FileStream Open(FileAccessPattern accessPattern)
    {
        var options = accessPattern switch
        {
            FileAccessPattern.Sequential => FileOptions.SequentialScan,
            FileAccessPattern.Random => FileOptions.RandomAccess,
            _ => throw new ArgumentOutOfRangeException(nameof(accessPattern), accessPattern, "The file access pattern is not defined."),
        };

        return Path.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, options: options);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answers from the same cached value as <see cref="Length"/>; a failure to read the size is reported through the returned task rather than thrown.
    /// </remarks>
    ValueTask<long> IRepoFile.GetLengthAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<long>(cancellationToken);

        try
        {
            return ValueTask.FromResult(GetLength());
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<long>(ex);
        }
    }

    /// <summary>
    /// Reads the size from the file system the first time and keeps it afterwards.
    /// </summary>
    private long GetLength()
    {
        long length = Volatile.Read(ref _length);

        if (length < 0)
        {
            length = Path.Length;
            Volatile.Write(ref _length, length);
        }

        return length;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the stream opened by <see cref="Open"/>. The local file is opened synchronously, so the returned task is already complete; a failure to open
    /// is reported through the task rather than thrown.
    /// </remarks>
    Task<Stream> IRepoFile.OpenReadAsync(FileAccessPattern accessPattern, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<Stream>(cancellationToken);

        try
        {
            return Task.FromResult<Stream>(Open(accessPattern));
        }
        catch (Exception ex)
        {
            return Task.FromException<Stream>(ex);
        }
    }
}
