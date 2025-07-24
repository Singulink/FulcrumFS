using FulcrumFS.Utilities;

namespace FulcrumFS;

/// <summary>
/// Represents the context for processing a file, providing access to the source file or stream and methods to create new work files or
/// directories.
/// </summary>
public sealed class FileProcessContext : IAsyncDisposable
{
    private readonly IAbsoluteDirectoryPath _workRootDirectory;

    private IAbsoluteFilePath? _file;
    private Stream? _stream;
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

    internal FileProcessContext(FileId fileId, string? variantId, IAbsoluteDirectoryPath workRootDirectory, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
    {
        FileId = fileId;
        VariantId = variantId;
        _workRootDirectory = workRootDirectory;

        _file = filePath;
        Extension = FileExtension.Normalize(filePath.Extension);
        CancellationToken = cancellationToken;
    }

    internal FileProcessContext(FileId fileId, string? variantId, IAbsoluteDirectoryPath workRootDirectory, Stream stream, string extension, bool leaveOpen, CancellationToken cancellationToken)
    {
        FileId = fileId;
        VariantId = variantId;
        _workRootDirectory = workRootDirectory;

        _stream = stream;
        Extension = extension;
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
        return _workRootDirectory.CombineDirectory(GetNextWorkId().ToString(), PathOptions.None);
    }

    /// <summary>
    /// Gets the source file as an <see cref="IAbsoluteFilePath"/> if it is available. If the source is a non-file stream, it will be copied to a new work file
    /// and returned. If the source is already an <see cref="IAbsoluteFilePath"/>, it will be returned directly.
    /// </summary>
    public async ValueTask<IAbsoluteFilePath> GetSourceAsFileAsync()
    {
        if (_file is IAbsoluteFilePath file)
        {
            _file = null; // Clear source to prevent multiple calls.
            return file;
        }

        if (_stream is not Stream stream)
            throw new InvalidOperationException("Source was already retrieved. Multiple calls to get the source are not allowed.");

        _stream = null; // Clear source to prevent multiple calls.

        if (stream is FileStream fileStream)
        {
            // If the source is already a file stream, we can return the file path it is associated with directly.

            file = FilePath.ParseAbsolute(fileStream.Name, PathOptions.None);

            if (!_leaveOpen)
                await fileStream.DisposeAsync().ConfigureAwait(false);

            return file;
        }

        var newWorkFile = GetNewWorkFile(Extension);
        newWorkFile.ParentDirectory.Create();
        fileStream = newWorkFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.Delete);

        await using (fileStream.ConfigureAwait(false))
        {
            await stream.CopyToAsync(fileStream, CancellationToken).ConfigureAwait(false);
        }

        if (!_leaveOpen)
            await stream.DisposeAsync().ConfigureAwait(false);

        return newWorkFile;
    }

    /// <summary>
    /// Gets the source as a seekable <see cref="Stream"/>. If the source is already a seekable stream, it will be returned directly. If the source is a stream
    /// that cannot seek, it will be copied to either a new work file or a memory stream depending on <paramref name="maxInMemoryCopySize"/> before being
    /// returned.
    /// </summary>
    /// <param name="maxInMemoryCopySize">The maximum size in bytes that the source stream can be copied to memory. If the source stream exceeds this size, it
    /// will be copied to a new work file instead.</param>
    public async ValueTask<Stream> GetSourceAsSeekableStreamAsync(long maxInMemoryCopySize)
    {
        if (_file is IAbsoluteFilePath file)
        {
            _file = null; // Clear source to prevent multiple calls.
            return file.OpenAsyncStream(FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        }

        if (_stream is not Stream stream)
            throw new InvalidOperationException("Source was already retrieved. Multiple calls to get the source are not allowed.");

        _stream = null; // Clear source to prevent multiple calls.

        if (stream.CanSeek)
            return stream;

        var fallbackFile = GetNewWorkFile(Extension);
        var streamCopy = await stream
            .CopyToSeekableAsync(maxInMemoryCopySize, fallbackFile, cancellationToken: CancellationToken)
            .ConfigureAwait(false);

        if (!_leaveOpen)
            await stream.DisposeAsync().ConfigureAwait(false);

        return streamCopy;
    }

    /// <summary>
    /// Asynchronously releases the resources used by the instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _file = null;

        if (_stream is Stream stream)
        {
            _stream = null;

            if (!_leaveOpen)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task SetResult(FileProcessResult result, bool isLastStep)
    {
        Extension = result.Extension;
        IsLastProcessStep = isLastStep;

        if (result.IsFileResult)
        {
            if (_stream is not null)
            {
                if (!_leaveOpen)
                    await _stream.DisposeAsync().ConfigureAwait(false);

                _stream = null;
            }

            _leaveOpen = false;
            _file = result.FileResult;

            return;
        }

        // Result is a stream result

        if (_stream == result.StreamResult)
            return;

        if (_stream is not null)
            await _stream.DisposeAsync().ConfigureAwait(false);

        _leaveOpen = false;
        _stream = result.StreamResult;
        _file = null;
    }

    private int GetNextWorkId() => Interlocked.Increment(ref _lastWorkEntryId);
}
