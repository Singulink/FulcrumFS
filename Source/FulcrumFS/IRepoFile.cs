namespace FulcrumFS;

/// <summary>
/// Represents a file stored in an <see cref="IFileRepo"/>: a main file or one of its variants.
/// </summary>
/// <remarks>
/// <para>
/// An instance describes the file as it was when it was fetched. Stored files never change once they are added, so the description stays accurate for as long
/// as the file exists, but the file can be deleted afterwards, after which <see cref="OpenReadAsync(FileAccessPattern, CancellationToken)"/> may fail, and so
/// may <see cref="GetLengthAsync(CancellationToken)"/> if the instance does not know the size yet.</para>
/// <para>
/// The interface does not expose where or how the content is stored, since an implementation does not have to keep files in a local file system: the content is
/// read through <see cref="OpenReadAsync(FileAccessPattern, CancellationToken)"/>. The local implementation, <c>RepoFileInfo</c>, also exposes the path
/// of the file.</para>
/// <para>
/// The size of the file is obtained on demand through <see cref="GetLengthAsync(CancellationToken)"/> rather than reported when the file is fetched, so that a
/// fetch costs no more than finding the file, while a file returned by an add knows its size already. The length of an opened stream is the size of the file
/// as well. The local implementation also has <c>RepoFileInfo.Length</c>, which answers from the same cached size.</para>
/// </remarks>
public interface IRepoFile
{
    /// <summary>
    /// Gets the ID of the file group the file belongs to.
    /// </summary>
    FileId FileId { get; }

    /// <summary>
    /// Gets the variant ID of the file in its normalized (lowercase) form, or <see langword="null"/> if this is the main file.
    /// </summary>
    string? VariantId { get; }

    /// <summary>
    /// Gets the extension of the file including the leading period, or an empty string if it has no extension.
    /// </summary>
    /// <remarks>
    /// A variant stored as an alias of its source (see <see cref="FileProcessingPipeline.AliasWhenVariantSourceUnchanged"/>) has the extension of the source
    /// whose content it shares.
    /// </remarks>
    string Extension { get; }

    /// <summary>
    /// Gets the size of the content of the file in bytes.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that cancels the operation.</param>
    /// <returns>The size of the file in bytes, which is also the <see cref="Stream.Length"/> of a stream opened over it.</returns>
    /// <remarks>
    /// <para>
    /// The size is obtained on demand and cached by the instance once it is known. Stored files never change, so it stays the same for as long as the file
    /// exists, and later calls answer without I/O, even after the file has been deleted. An implementation that already knows the size, for example from an
    /// earlier open or from the metadata a remote fetch returned, answers the first call without I/O as well.</para>
    /// <para>
    /// Files returned by an add know their size. For every file in the result of
    /// <see cref="IFileRepoTransaction.AddAsync(Stream, string, bool, IFileProcessingPipelineSelector, CancellationToken)"/>,
    /// <see cref="IFileRepoStagedTransaction.AddAsync(Stream, string, bool, IFileProcessingPipelineSelector, CancellationToken)"/>,
    /// <see cref="IFileRepo.AddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/>,
    /// <see cref="IFileRepo.TryAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/> and
    /// <see cref="IFileRepo.GetOrAddVariantAsync(FileId, string, string?, IFileProcessingPipelineSelector, CancellationToken)"/> (the variants it added; those
    /// that already existed may read their size on demand), this method completes synchronously and requires no storage access or network round trip. The add
    /// has just written the content, so the size is known where that happened (a remote implementation returns it in the add response), and an application
    /// that adds a file and then records its size does not pay a round trip for reading it back. For fetched files (<see cref="IFileRepo.GetAsync(FileId)"/>,
    /// <see cref="IFileRepo.GetGroupAsync(FileId)"/> and <see cref="IFileRepo.GetVariantAsync(FileId, string)"/>) the size is read on the first call.</para>
    /// <para>
    /// This is a method rather than a property because a property would make every implementation produce the size when it fetches the file. A fetch finds a
    /// file by its name alone, so on a shared repository server that would be a separate metadata read for every fetched file and every member of a group,
    /// whether or not anyone asks for the size; the method costs nothing until it is called.</para>
    /// </remarks>
    /// <exception cref="IOException">The size is not known yet and the content is no longer available, for example because a cleanup pass physically removed
    /// the file after it was fetched.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read-only, seekable stream over the content of the file.
    /// </summary>
    /// <param name="accessPattern">How the caller is going to read the stream: from start to end, or by seeking within it. This is a hint that the
    /// implementation may use to choose how it reads the content; the stream can be read either way.</param>
    /// <param name="cancellationToken">A cancellation token that cancels opening the stream. It does not apply to reads from the returned stream.</param>
    /// <returns>A readable, seekable stream positioned at the start of the content, which the caller owns and must dispose.</returns>
    /// <remarks>
    /// <para>
    /// The stream is readable and seekable, and cannot be written to: <see cref="Stream.CanSeek"/> is <see langword="true"/>, and
    /// <see cref="Stream.Length"/>, <see cref="Stream.Position"/> and <see cref="Stream.Seek(long, SeekOrigin)"/> work, with the length equal to the size
    /// of the file. The stream may support only asynchronous reads, so callers read it with
    /// <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> or <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/> and must not rely on
    /// <see cref="Stream.Read(byte[], int, int)"/>. The local implementation returns a <see cref="FileStream"/>, which supports both. An implementation
    /// that reads over a network can provide seeking with range requests.</para>
    /// <para>
    /// The access pattern is a hint about the caller's intent. Implementations may use it to choose buffering, read-ahead or their request strategy, for
    /// example prefetching ahead of a sequential reader but fetching only the ranges a random reader asks for, and must return the same, correct content
    /// whichever pattern is passed. The local implementation opens the file with <see cref="FileOptions.SequentialScan"/> or
    /// <see cref="FileOptions.RandomAccess"/>.</para>
    /// <para>
    /// An open stream must not prevent the file from being deleted. The local implementation opens the file as <c>RepoFileInfo.Open</c> does, with
    /// <see cref="FileShare.Read"/> | <see cref="FileShare.Delete"/>: other readers can open the file concurrently, and the repository can delete the file
    /// while the stream is still open.</para>
    /// </remarks>
    /// <exception cref="IOException">The content is no longer available, for example because a cleanup pass physically removed the file after it was
    /// fetched.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    Task<Stream> OpenReadAsync(FileAccessPattern accessPattern, CancellationToken cancellationToken = default);
}
