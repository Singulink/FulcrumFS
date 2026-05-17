using Microsoft.Extensions.Options;

namespace FulcrumFS;

/// <summary>
/// Cleans up a file repository: enumerates marker files in the repository's cleanup directory, removes files whose delete markers have aged past the supplied
/// delete delay, and resolves indeterminate files via an optional caller-supplied callback.
/// </summary>
/// <remarks>
/// A <see cref="FileRepoCleaner"/> does not require an active <c>FileRepo</c> and can be used to clean a repository from a separate process or scheduled task.
/// The repository must already be initialized; the cleaner does not create or repair repository structure. Cross-process safety is provided by file-system
/// primitives: indeterminate markers held open by active transactions are detected via an exclusive-share probe, and concurrent clean operations against the
/// same repository are serialized by an exclusive lock file inside the cleanup directory.
/// </remarks>
public class FileRepoCleaner : IFileRepoCleaner
{
    /// <summary>
    /// Gets the configuration options for the cleaner. The returned instance is frozen and cannot be modified.
    /// </summary>
    public FileRepoCleaningOptions Options { get; }

    private readonly IAbsoluteFilePath _infoFile;
    private readonly IAbsoluteDirectoryPath _filesDirectory;
    private readonly IAbsoluteDirectoryPath _cleanupDirectory;
    private readonly IAbsoluteFilePath _cleanLockFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaner"/> class with the specified options.
    /// </summary>
    public FileRepoCleaner(FileRepoCleaningOptions options)
    {
        if (options.BaseDirectory is null)
            throw new ArgumentException("Base directory must be set in options.", nameof(options));

        options.Freeze();
        Options = options;

        _infoFile = options.BaseDirectory.CombineFile(FileRepoPaths.InfoFileName, PathOptions.None);
        _filesDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.FilesDirectoryName, PathOptions.None);
        _cleanupDirectory = options.BaseDirectory.CombineDirectory(FileRepoPaths.CleanupDirectoryName, PathOptions.None);
        _cleanLockFile = _cleanupDirectory.CombineFile(FileRepoPaths.CleanupLockFileName, PathOptions.None);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaner"/> class with the specified options.
    /// </summary>
    public FileRepoCleaner(IOptions<FileRepoCleaningOptions> options) : this(options.Value) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRepoCleaner"/> class with the specified base directory and optional configuration action.
    /// </summary>
    /// <param name="baseDirectory">The base directory of the file repository to be cleaned.</param>
    /// <param name="configure">An optional action that configures additional options.</param>
    public FileRepoCleaner(IAbsoluteDirectoryPath baseDirectory, Action<FileRepoCleaningOptions>? configure = null)
        : this(BuildOptions(baseDirectory, configure)) { }

    private static FileRepoCleaningOptions BuildOptions(IAbsoluteDirectoryPath baseDirectory, Action<FileRepoCleaningOptions>? configure)
    {
        var options = new FileRepoCleaningOptions(baseDirectory);
        configure?.Invoke(options);
        return options;
    }

    /// <inheritdoc/>
    public Task CleanAsync(TimeSpan deleteDelay, CancellationToken cancellationToken = default) =>
        CleanAsync(deleteDelay, (Func<FileId, Task<IndeterminateResolution>>?)null, cancellationToken);

    /// <inheritdoc/>
    public Task CleanAsync(TimeSpan deleteDelay, Func<FileId, IndeterminateResolution>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        Func<FileId, Task<IndeterminateResolution>>? asyncCallbackWrapper = null;

        if (resolveIndeterminateCallback is not null)
            asyncCallbackWrapper = fileId => Task.FromResult(resolveIndeterminateCallback(fileId));

        return CleanAsync(deleteDelay, asyncCallbackWrapper, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task CleanAsync(TimeSpan deleteDelay, Func<FileId, Task<IndeterminateResolution>>? resolveIndeterminateCallback, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deleteDelay, TimeSpan.Zero, nameof(deleteDelay));

        var markerFileLogging = Options.MarkerFileLogging;

        FileStream cleanLockStream;

        try
        {
            cleanLockStream = _cleanLockFile.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {// The optimistic open failed. Diagnose what went wrong by inspecting the directory state. Order of checks: missing base dir > missing repo marker >
            // missing cleanup dir > genuine lock contention.

            if (Options.BaseDirectory.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The file repository base directory '{Options.BaseDirectory.PathDisplay}' was not found.", ex);
            }

            if (_infoFile.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The directory '{Options.BaseDirectory.PathDisplay}' is not an initialized FulcrumFS repository.", ex);
            }

            if (_cleanupDirectory.State is not EntryState.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"The FulcrumFS repository at '{Options.BaseDirectory.PathDisplay}' is in an incomplete state " +
                    $"(the cleanup directory is missing).", ex);
            }

            throw new InvalidOperationException("Another clean operation is already in progress.", ex);
        }

        using (cleanLockStream)
        {
            // Now that we hold the clean lock, verify the repository advertises a clean-compat version this library understands. This is checked AFTER lock
            // acquisition so the more specific "not a repository" / "incomplete state" diagnostics in the catch block above take precedence when applicable.
            RepoInfoFile.VerifyCleanCompatSupported(_infoFile);

            var deletedFiles = new HashSet<FileId>();
            var indeterminateFiles = new List<(FileId Id, IAbsoluteFilePath Marker)>();

            var elc = new ExceptionListCapture(ex => ex is not (ArgumentException or ObjectDisposedException or TimeoutException));

            foreach (var markerInfo in _cleanupDirectory.GetChildFilesInfo())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var marker = markerInfo.Path;

                if (marker.Extension is FileRepoPaths.IndeterminateMarkerExtension)
                {
                    if (!FileId.TryParseUnsafe(marker.NameWithoutExtension, out var fileId))
                        continue;

                    // Probe whether the marker is currently held by an active transaction by attempting to open it with exclusive access. If the open fails with
                    // an IOException then a transaction is holding the marker open and we must skip it. The probe is released immediately - actions on the marker
                    // below are best-effort and tolerate the marker being re-acquired by a new transaction in the meantime.

                    try
                    {
                        using var probe = marker.OpenStream(FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    indeterminateFiles.Add((fileId, marker));
                }
                else if (marker.Extension is FileRepoPaths.DeleteMarkerExtension)
                {
                    if (markerInfo.CreationTimeUtc + deleteDelay > DateTime.UtcNow)
                        continue;

                    if (!FileRepo.TryParseFileIdAndVariant(marker.NameWithoutExtension, out var deleteFileId, out string? variantId))
                        continue;

                    if (variantId is null)
                    {
                        await elc.TryRunAsync(DeleteFileFromCleanAsync(_filesDirectory, _cleanupDirectory, deleteFileId, immediate: true, markerFileLogging)).ConfigureAwait(false);
                        deletedFiles.Add(deleteFileId);
                    }
                    else
                    {
                        await elc.TryRunAsync(DeleteVariantFromCleanAsync(_filesDirectory, _cleanupDirectory, deleteFileId, variantId, markerFileLogging)).ConfigureAwait(false);
                    }
                }
            }

            foreach (var indeterminateFile in indeterminateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileDir = FileRepo.GetFileDirectory(_filesDirectory, indeterminateFile.Id);
                var fileDirState = fileDir.State;

                if (fileDirState is EntryState.ParentExists || deletedFiles.Contains(indeterminateFile.Id))
                {
                    // Parent dir of the file container dir exists but the file dir itself does not, so we can delete the indeterminate marker since the file dir is
                    // gone. Ignore errors, we don't want to recreate an indeterminate marker if it is gone.

                    indeterminateFile.Marker.TryDelete(out _);
                    continue;
                }

                if (resolveIndeterminateCallback is null)
                    continue;

                var resolution = await resolveIndeterminateCallback.Invoke(indeterminateFile.Id).ConfigureAwait(false);

                if (resolution is IndeterminateResolution.Keep)
                {
                    await elc.TryRunAsync(FileRepo.ClearIndeterminateStateAsync(_cleanupDirectory, indeterminateFile.Id, markerFileLogging)).ConfigureAwait(false);
                }
                else if (resolution is IndeterminateResolution.Delete)
                {
                    // If deleteDelay is positive, write a delete marker (starting the grace clock) and remove the indeterminate marker. The next clean run after
                    // the delay will physically delete the file. If deleteDelay is zero, delete immediately.

                    await elc.TryRunAsync(DeleteFileFromCleanAsync(_filesDirectory, _cleanupDirectory, indeterminateFile.Id, immediate: deleteDelay <= TimeSpan.Zero, markerFileLogging)).ConfigureAwait(false);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(resolveIndeterminateCallback), "The provided callback returned an invalid resolution value.");
                }
            }

            elc.ThrowIfHasExceptions();
        }
    }

    private static async Task DeleteFileFromCleanAsync(
        IAbsoluteDirectoryPath filesDirectory,
        IAbsoluteDirectoryPath cleanupDirectory,
        FileId fileId,
        bool immediate,
        LoggingMode markerFileLogging)
    {
        var fileDir = FileRepo.GetFileDirectory(filesDirectory, fileId);
        var deleteMarker = FileRepo.GetDeleteMarker(cleanupDirectory, fileId, null);
        var indeterminateMarker = FileRepo.GetIndeterminateMarker(cleanupDirectory, fileId);

        if (immediate)
        {
            if (!fileDir.TryDelete(recursive: true, out var ex) || !indeterminateMarker.TryDelete(out ex) || !deleteMarker.TryDelete(out ex))
                await FileRepo.LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex, markerFileLogging).ConfigureAwait(false);
        }
        else
        {
            const string message = "File has been marked for deletion.";
            await FileRepo.LogToMarkerAsync(deleteMarker, "DELETE", message, markerFileLogging).ConfigureAwait(false);
            indeterminateMarker.TryDelete(out _);
        }
    }

    private static async Task DeleteVariantFromCleanAsync(
        IAbsoluteDirectoryPath filesDirectory,
        IAbsoluteDirectoryPath cleanupDirectory,
        FileId fileId,
        string variantId,
        LoggingMode markerFileLogging)
    {
        var file = FileRepo.FindDataFile(filesDirectory, fileId, variantId);
        var deleteMarker = FileRepo.GetDeleteMarker(cleanupDirectory, fileId, variantId);

        if (file is not null && !file.TryDelete(out var ex))
        {
            await FileRepo.LogToMarkerAsync(deleteMarker, "DELETE ATTEMPT FAILED", ex, markerFileLogging).ConfigureAwait(false);
            return;
        }

        deleteMarker.TryDelete(out _);
    }
}
