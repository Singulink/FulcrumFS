namespace FulcrumFS;

/// <summary>
/// Represents the result of processing a file in a <see cref="FileProcessPipeline"/>.
/// </summary>
public sealed class FileProcessResult
{
    internal IAbsoluteFilePath? FileResult { get; }

    internal Stream? StreamResult { get; }

    internal string Extension { get; }

    [MemberNotNullWhen(true, nameof(FileResult))]
    [MemberNotNullWhen(false, nameof(StreamResult))]
    internal bool IsFileResult => FileResult is not null;

    /// <summary>
    /// Creates a new <see cref="FileProcessResult"/> for a file result.
    /// </summary>
    public static FileProcessResult File(IAbsoluteFilePath file) => new(file, null, file.Extension);

    /// <summary>
    /// Creates a new <see cref="FileProcessResult"/> for a stream result. Make sure you set the stream position to where you want the next processor to start
    /// reading the stream from.
    /// </summary>
    public static FileProcessResult Stream(Stream stream, string? extension) => new(null, stream, extension);

    /// <summary>
    /// Creates a new <see cref="FileProcessResult"/> for a file stream result. Make sure you set the stream position to where you want the next processor to
    /// start reading the stream from.
    /// </summary>
    public static FileProcessResult Stream(FileStream stream)
    {
        string extension = FilePath.ParseAbsolute(stream.Name, PathOptions.None).Extension;
        extension = FileExtension.Normalize(extension);

        return new FileProcessResult(null, stream, extension);
    }

    private FileProcessResult(IAbsoluteFilePath? file, Stream? stream, string? extension)
    {
        FileResult = file;
        StreamResult = stream;
        Extension = FileExtension.Normalize(extension);
    }
}
