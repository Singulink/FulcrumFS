namespace FulcrumFS;

/// <summary>
/// Represents the result of processing a file in a <see cref="FileProcessingPipeline"/>.
/// </summary>
public sealed class FileProcessingResult
{
    internal object Result { get; }

    internal string Extension { get; }

    internal bool HasChanges { get; }

    /// <summary>
    /// Creates a new <see cref="FileProcessingResult"/> for a file result.
    /// </summary>
    public static FileProcessingResult File(IAbsoluteFilePath file, bool hasChanges) => new(file, file.Extension, hasChanges);

    /// <summary>
    /// Creates a new <see cref="FileProcessingResult"/> for a stream result. Make sure you set the stream position to where you want the next processor to start
    /// reading the stream from.
    /// </summary>
    public static FileProcessingResult Stream(Stream stream, string? extension, bool hasChanges) => new(stream, extension, hasChanges);

    /// <summary>
    /// Creates a new <see cref="FileProcessingResult"/> for a file stream result. Make sure you set the stream position to where you want the next processor to
    /// start reading the stream from.
    /// </summary>
    public static FileProcessingResult Stream(FileStream stream, bool hasChanges)
    {
        string extension = FilePath.ParseAbsolute(stream.Name, PathOptions.None).Extension;
        return new FileProcessingResult(stream, extension, hasChanges);
    }

    private FileProcessingResult(object result, string? extension, bool hasChanges)
    {
        Result = result;
        Extension = FileExtension.Normalize(extension);
        HasChanges = hasChanges;
    }
}
