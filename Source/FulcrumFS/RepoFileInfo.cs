namespace FulcrumFS;

/// <summary>
/// Represents information about a file stored in a <see cref="FileRepo"/>, including its identity and a recommended way to open it.
/// </summary>
public sealed record RepoFileInfo
{
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
    public long Length => Path.Length;

    internal RepoFileInfo(FileId fileId, string? variantId, IAbsoluteFilePath path)
    {
        FileId = fileId;
        VariantId = variantId;
        Path = path;
    }

    /// <summary>
    /// Opens an asynchronous read-only file stream using the recommended sharing options for repository files
    /// (<see cref="FileShare.Read"/> | <see cref="FileShare.Delete"/>).
    /// </summary>
    /// <returns>A new <see cref="FileStream"/> opened for reading.</returns>
    /// <remarks>
    /// This is the recommended way to read a repository file. The returned stream allows other readers to access the file concurrently and allows the
    /// repository to delete the file while it is still open.
    /// </remarks>
    public FileStream Open() => Path.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
}
