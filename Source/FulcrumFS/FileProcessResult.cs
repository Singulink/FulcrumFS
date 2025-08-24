namespace FulcrumFS;

/// <summary>
/// Represents the result of processing a file in a <see cref="FileProcessPipeline"/>.
/// </summary>
public sealed class FileProcessResult
{
    internal object Result { get; }

    internal string Extension { get; }

    /// <summary>
    /// Creates a new <see cref="FileProcessResult"/> for a file result.
    /// </summary>
    public static FileProcessResult File(IAbsoluteFilePath file) => new(file, file.Extension);

    /// <summary>
    /// Creates a new <see cref="FileProcessResult"/> for a stream result. Make sure you set the stream position to where you want the next processor to start
    /// reading the stream from.
    /// </summary>
    public static FileProcessResult Stream(Stream stream, string? extension) => new(stream, extension);

    /// <summary>
    /// Creates a new <see cref="FileProcessResult"/> for a file stream result. Make sure you set the stream position to where you want the next processor to
    /// start reading the stream from.
    /// </summary>
    public static FileProcessResult Stream(FileStream stream)
    {
        string extension = FilePath.ParseAbsolute(stream.Name, PathOptions.None).Extension;
        return new FileProcessResult(stream, extension);
    }

    private FileProcessResult(object result, string? extension)
    {
        Result = result;
        Extension = FileExtension.Normalize(extension);
    }
}
