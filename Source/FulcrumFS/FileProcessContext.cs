namespace FulcrumFS;

/// <summary>
/// Represents the context for processing a file, providing access to the source file or stream and methods to create new work files or
/// directories.
/// </summary>
public sealed class FileProcessContext : IAsyncDisposable
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
    /// Gets a value indicating whether the pipeline is at the last process step.
    /// </summary>
    public bool IsLastProcessStep { get; private set; }

    internal CancellationToken CancellationToken { get; }

    internal FileProcessContext(FileId fileId, string? variantId, IAbsoluteDirectoryPath workRootDirectory, IAbsoluteFilePath sourceFile, CancellationToken cancellationToken)
    {
        FileId = fileId;
        VariantId = variantId;
        _workRootDirectory = workRootDirectory;

        _source = sourceFile;
        Extension = FileExtension.Normalize(sourceFile.Extension);
        CancellationToken = cancellationToken;
    }

    internal FileProcessContext(FileId fileId, string? variantId, IAbsoluteDirectoryPath workRootDirectory, Stream stream, string extension, bool leaveOpen, CancellationToken cancellationToken)
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
        return _workRootDirectory.CombineFile(GetNextWorkId() + extension, PathOptions.None);
    }

    /// <summary>
    /// Gets a new non-existent directory path that can be used for any intermediate work required by a <see cref="FileProcessor"/> or to store the final
    /// result. The directory is not created automatically. Work directories are automatically cleaned up after the processing pipeline completes but file
    /// processors can clean up intermediate work directories or their contents earlier if they are no longer needed.
    /// </summary>
    public IAbsoluteDirectoryPath GetNewWorkDirectory()
    {
        ObjectDisposedException.ThrowIf(_source is null, this);
        return _workRootDirectory.CombineDirectory(GetNextWorkId().ToString(), PathOptions.None);
    }

    /// <summary>
    /// Gets the source as an <see cref="IAbsoluteFilePath"/>. If the source is a non-file stream, it will be copied to a new work file and returned. If the
    /// source is already an <see cref="IAbsoluteFilePath"/>, it will be returned directly.
    /// </summary>
    public async ValueTask<IAbsoluteFilePath> GetSourceAsFileAsync()
    {
        ObjectDisposedException.ThrowIf(_source is null, this);

        if (_source is IAbsoluteFilePath file)
            return file;

        var stream = (Stream)_source;

        if (stream is FileStream fileStream)
        {
            // If the source is already a file stream, we can return the file path it is associated with directly.
            file = FilePath.ParseAbsolute(fileStream.Name, PathOptions.None);
        }
        else
        {
            file = GetNewWorkFile(Extension);
            file.ParentDirectory.Create();
            fileStream = file.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.Delete);

            await using (fileStream.ConfigureAwait(false))
            {
                await stream.CopyToAsync(fileStream, CancellationToken).ConfigureAwait(false);
            }
        }

        if (!_leaveOpen)
            await stream.DisposeAsync().ConfigureAwait(false);

        _leaveOpen = false;

        _source = file;
        return file;
    }

    /// <summary>
    /// Gets the source as a seekable <see cref="Stream"/>.
    /// </summary>
    /// <param name="preferInMemory">If <see langword="true"/>, a memory stream will always be returned as long as the source is smaller than <paramref
    /// name="maxInMemoryCopySize"/>. If <see langword="false"/>, different stream types can be returned depending on what the source is, but it will always be
    /// a seekable stream.</param>
    /// <param name="maxInMemoryCopySize">The maximum number of bytes that will be copied from the source stream to a memory stream before falling back to. If the source stream exceeds this size, it
    /// will be copied to a new work file instead.</param>
    public async ValueTask<Stream> GetSourceAsSeekableStreamAsync(bool preferInMemory, long maxInMemoryCopySize)
    {
        ObjectDisposedException.ThrowIf(_source is null, this);

        if (_source is not Stream stream)
        {
            var file = (IAbsoluteFilePath)_source;
            _source = stream = file.OpenAsyncStream(FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            _leaveOpen = false;
        }

        if (stream.CanSeek && (!preferInMemory || stream is MemoryStream || stream.Length > maxInMemoryCopySize))
            return stream;

        var fallbackFile = GetNewWorkFile(Extension);
        var streamCopy = await stream
            .CopyToSeekableAsync(maxInMemoryCopySize, fallbackFile, cancellationToken: CancellationToken)
            .ConfigureAwait(false);

        if (!_leaveOpen)
            await stream.DisposeAsync().ConfigureAwait(false);

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

    internal async Task SetResultAsync(FileProcessResult result, bool isLastStep)
    {
        ObjectDisposedException.ThrowIf(_source is null, this);

        Extension = result.Extension;
        IsLastProcessStep = isLastStep;

        if (_source == result.Result)
            return;

        if (!_leaveOpen && _source is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);

        _source = result.Result;
        _leaveOpen = false;
    }

    private int GetNextWorkId() => Interlocked.Increment(ref _lastWorkEntryId);
}
