using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FulcrumFS;

/// <summary>
/// Represents the context for processing a file, providing access to the source file or stream and methods to create new work files or directories.
/// </summary>
public sealed class FileProcessingContext : IAsyncDisposable
{
    private readonly IAbsoluteDirectoryPath _workRootDirectory;

    private object? _source;
    private bool _leaveOpen;

    private int _lastWorkEntryId;

    /// <summary>
    /// Gets the file ID that is being processed in the pipeline.
    /// </summary>
    public FileId FileId { get; }

    /// <summary>
    /// Gets the variant ID of the file being processed in the pipeline, or <see langword="null"/> if the file is not a variant.
    /// </summary>
    public string? VariantId { get; }

    /// <summary>
    /// Gets the file extension of the source, including the leading dot (e.g., ".jpg", ".png"), otherwise an empty string. File extensions are always
    /// lowercase.
    /// </summary>
    public string Extension { get; private set; }

    /// <summary>
    /// Gets the cancellation token that can be used to observe cancellation requests.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets a value indicating whether the pipeline is at the last process step.
    /// </summary>
    public bool IsLastProcessStep { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the source has had any changes made to it during processing.
    /// </summary>
    public bool HasChanges { get; private set; }

    internal bool IsSourceInMemoryOrFile => _source is IAbsoluteFilePath or MemoryStream or FileStream;

    internal FileProcessingContext(
        FileId fileId,
        string? variantId,
        IAbsoluteDirectoryPath workRootDirectory,
        IAbsoluteFilePath sourceFile,
        CancellationToken cancellationToken)
    {
        FileId = fileId;
        VariantId = variantId;
        _workRootDirectory = workRootDirectory;

        _source = sourceFile;
        Extension = FileExtension.Normalize(sourceFile.Extension);
        CancellationToken = cancellationToken;
    }

    internal FileProcessingContext(
        FileId fileId,
        string? variantId,
        IAbsoluteDirectoryPath workRootDirectory,
        Stream stream,
        string extension,
        bool leaveOpen,
        CancellationToken cancellationToken)
    {
        FileId = fileId;
        VariantId = variantId;
        _workRootDirectory = workRootDirectory;

        _source = stream;
        Extension = FileExtension.Normalize(extension);
        _leaveOpen = leaveOpen;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets a new non-existent file path that can be used for any intermediate work required by a <see cref="FileProcessor"/> or to store the final result.
    /// The file is not created automatically. Work files are automatically cleaned up after the processing pipeline completes but file processors can clean up
    /// intermediate work files earlier if they are no longer needed.
    /// </summary>
    /// <param name="extension">The file extension to use for the work file. Must be empty for no extension or start with a dot (e.g., ".jpg", ".png").</param>
    public IAbsoluteFilePath GetNewWorkFile(string extension)
    {
        ObjectDisposedException.ThrowIf(_source is null, this);
        extension = FileExtension.Normalize(extension);
        return _workRootDirectory.CombineFile(GetNextWorkId().ToString(CultureInfo.InvariantCulture) + extension, PathOptions.None);
    }

    /// <summary>
    /// Gets a new non-existent directory path that can be used for any intermediate work required by a <see cref="FileProcessor"/> or to store the final
    /// result. The directory is not created automatically. Work directories are automatically cleaned up after the processing pipeline completes but file
    /// processors can clean up intermediate work directories or their contents earlier if they are no longer needed.
    /// </summary>
    public IAbsoluteDirectoryPath GetNewWorkDirectory()
    {
        ObjectDisposedException.ThrowIf(_source is null, this);
        return _workRootDirectory.CombineDirectory(GetNextWorkId().ToString(CultureInfo.InvariantCulture), PathOptions.None);
    }

    /// <summary>
    /// Gets the source as an <see cref="IAbsoluteFilePath"/>. If the source is a non-file stream, it will be copied to a new work file and returned. If the
    /// source is already an <see cref="IAbsoluteFilePath"/>, it will be returned directly.
    /// </summary>
    public async ValueTask<IAbsoluteFilePath> GetSourceAsFileAsync()
    {
        ObjectDisposedException.ThrowIf(_source is null, this);

        if (_source is IAbsoluteFilePath sourceFile)
            return sourceFile;

        var sourceStream = (Stream)_source;

        if (sourceStream is FileStream fileStream)
        {
            // If the source is already a file stream, we can return the file path it is associated with directly.
            sourceFile = FilePath.ParseAbsolute(fileStream.Name, PathOptions.None);
        }
        else
        {
            sourceFile = GetNewWorkFile(Extension);
            sourceFile.ParentDirectory.Create();
            fileStream = sourceFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.Delete);

            await using (fileStream.ConfigureAwait(false))
            {
                await sourceStream.CopyToAsync(fileStream, CancellationToken).ConfigureAwait(false);
            }
        }

        if (!_leaveOpen)
            await sourceStream.DisposeAsync().ConfigureAwait(false);

        _leaveOpen = false;

        _source = sourceFile;
        return sourceFile;
    }

    /// <summary>
    /// Gets the source as a seekable <see cref="Stream"/>.
    /// </summary>
    /// <param name="preferInMemory">If <see langword="true"/>, a memory stream will always be returned as long as the source is smaller than <paramref
    /// name="maxInMemoryCopySize"/>. If <see langword="false"/>, different stream types can be returned depending on what the source is, but it will always be
    /// a seekable stream.</param>
    /// <param name="maxInMemoryCopySize">The maximum number of bytes that will be copied from the source stream to a memory stream. If the source stream
    /// exceeds this size, it will be copied to a new work file instead.</param>
    public async ValueTask<Stream> GetSourceAsSeekableStreamAsync(bool preferInMemory, long maxInMemoryCopySize)
    {
        ObjectDisposedException.ThrowIf(_source is null, this);

        if (_source is not Stream sourceStream)
        {
            var sourceFile = (IAbsoluteFilePath)_source;
            _source = sourceStream = sourceFile.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            _leaveOpen = false;
        }

        if (sourceStream.CanSeek && (sourceStream is MemoryStream || !preferInMemory || sourceStream.Length > maxInMemoryCopySize))
            return sourceStream;

        var fallbackFile = GetNewWorkFile(Extension);
        var streamCopy = await sourceStream
            .CopyToSeekableAsync(maxInMemoryCopySize, fallbackFile, CancellationToken)
            .ConfigureAwait(false);

        if (!_leaveOpen)
            await sourceStream.DisposeAsync().ConfigureAwait(false);

        streamCopy.Position = 0;
        _source = streamCopy;
        _leaveOpen = false;
        return streamCopy;
    }

    /// <summary>
    /// Asynchronously releases the resources used by the instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_leaveOpen && _source is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);

        _source = null;
    }

    internal async Task SetResultAsync(FileProcessingResult result, bool oneStepLeft)
    {
        ObjectDisposedException.ThrowIf(_source is null, this);

        Extension = result.Extension;
        IsLastProcessStep = oneStepLeft;
        HasChanges |= result.HasChanges;

        if (_source == result.Result)
            return;

        if (!_leaveOpen && _source is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);

        _source = result.Result;
        _leaveOpen = false;
    }

    internal async Task BufferSourceToWorkFileAsync()
    {
        ObjectDisposedException.ThrowIf(_source is null, this);

        var workFile = GetNewWorkFile(Extension);
        workFile.ParentDirectory.Create();

        if (_source is IAbsoluteFilePath sourceFile)
        {
            await sourceFile.CopyToAsync(workFile, CancellationToken).ConfigureAwait(false);
            _source = workFile;
            return;
        }

        var sourceStream = (Stream)_source;
        var workFileStream = workFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
        await sourceStream.CopyToAsync(workFileStream, CancellationToken).ConfigureAwait(false);

        if (!_leaveOpen)
            await sourceStream.DisposeAsync().ConfigureAwait(false);

        workFileStream.Position = 0;
        _source = workFileStream;
        _leaveOpen = false;
    }

    private int GetNextWorkId() => Interlocked.Increment(ref _lastWorkEntryId);
}
