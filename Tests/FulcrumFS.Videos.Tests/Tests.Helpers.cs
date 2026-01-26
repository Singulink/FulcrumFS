using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Shouldly;
using Singulink.IO;
using Singulink.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FulcrumFS.Videos;

// This file contains the helper methods and properties used by all of the Tests.*.cs test files, both for VideoProcessor and ThumbnailProcessor.

partial class Tests
{
    public required TestContext TestContext { get; set; }

    // NOTE!!! We do not currently test all of the VideoCompressionLevel, AudioQuality, and VideoQuality modes in CI mode - so if changes are made to those,
    // you must run the tests locally (without the CI define / env var) to ensure full coverage.
#if !CI
    private const string BigBuckBunnyFullVideoFileName = "bbb_sunflower_1080p_60fps_normal-25s.mp4";
#else
    // In CI we use a shorter version to save on testing time:
    private const string BigBuckBunnyFullVideoFileName = "bbb_sunflower_1080p_60fps_normal-1s.mp4";
#endif

    // Note: we use 'ForceValidateAllStreams = false' for most tests in this file to reduce test time.
    // Note: this is only done in Release mode, so that Debug tests still run validation logic on all streams for better coverage.
#if DEBUG
    public const bool DefaultForceValidateAllStreams = true;
#else
    public const bool DefaultForceValidateAllStreams = false;
#endif

    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _videoFilesDir = _appDir.CombineDirectory("Videos");
    private static readonly IAbsoluteDirectoryPath _tempFilesDir = _appDir.CombineDirectory("Temp");

    // Global count helper to ensure unique repo dirs:
    private static int _count;

    private static IDisposable? GetRepo(out FileRepo repo)
    {
        // Creates a new repo in a unique temp directory and returns a disposer to clean it up.
        var repoDir = _appDir.CombineDirectory("RepoRoot" + Interlocked.Increment(ref _count).ToString(CultureInfo.InvariantCulture));
        repo = new(repoDir, options =>
        {
            options.DeleteDelay = TimeSpan.Zero;
            options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(120);
        });
        ResetRepository(repoDir, clearOnly: false);
        var repoCopy = repo;
        return new DisposeHelper(() =>
        {
            repoCopy.Dispose();
            ResetRepository(repoDir, clearOnly: true);
        });
    }

    // Helper class to run an action on dispose or finalize, and also try to run on process exit if dispose or finalizer wasn't called:
    private sealed class DisposeHelper : IDisposable
    {
        private readonly Action _onDispose;
        private readonly EventHandler _processExitHandler;
        private InterlockedFlag _run;

        public DisposeHelper(Action onDispose)
        {
            _onDispose = onDispose;
            _processExitHandler = Create_CurrentDomain_ProcessExit(new(this));
            AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        }

        private static EventHandler Create_CurrentDomain_ProcessExit(WeakReference<DisposeHelper> weakThis) => (sender, e) =>
        {
            if (weakThis.TryGetTarget(out var @this))
            {
                @this.Dispose(disposing: true, exiting: true);
            }
        };

        public void Dispose()
        {
            Dispose(disposing: true, exiting: false);
        }

        private void Dispose(bool disposing, bool exiting)
        {
            if (_run.TrySet())
            {
                try
                {
                    _onDispose();
                }
                finally
                {
                    if (!exiting) AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
                    if (disposing) GC.SuppressFinalize(this);
                }
            }
        }

        ~DisposeHelper()
        {
            Dispose(disposing: false, exiting: false);
        }
    }

    private static void ResetRepository(IAbsoluteDirectoryPath repoDir, bool clearOnly)
    {
        // Deletes the repo directory (with retries) and recreates it unless clearOnly is true.
        if (repoDir.Exists)
        {
            // Retry up to 10 times, as it randomly fails sometimes:
            for (int i = 10; i > 0; i--)
            {
                try
                {
                    repoDir.Delete(recursive: true);
                    break;
                }
                catch (IOException) when (i != 1)
                {
                }
            }
        }

        if (!clearOnly)
        {
            repoDir.Create();
        }
    }

    private async ValueTask<bool> AreStreamsEqual(Stream expected, Stream actual, CancellationToken cancellationToken)
    {
        // Compares two streams byte-for-byte.
        if (expected.Length != actual.Length)
            return false;

        byte[] buffer1 = ArrayPool<byte>.Shared.Rent(65536);
        byte[] buffer2 = ArrayPool<byte>.Shared.Rent(buffer1.Length);
        buffer1.Length.ShouldBeEquivalentTo(buffer2.Length);

        int extra = (int)(expected.Length % buffer1.Length);
        await expected.ReadExactlyAsync(buffer1, 0, extra, TestContext.CancellationToken);
        await actual.ReadExactlyAsync(buffer2, 0, extra, TestContext.CancellationToken);
        bool result = buffer1.AsSpan(0, extra).SequenceEqual(buffer2.AsSpan(0, extra));
        cancellationToken.ThrowIfCancellationRequested();

        if (result)
        {
            while (expected.Position < expected.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await expected.ReadExactlyAsync(buffer1, 0, buffer1.Length, TestContext.CancellationToken);
                await actual.ReadExactlyAsync(buffer2, 0, buffer2.Length, TestContext.CancellationToken);

                if (!buffer1.SequenceEqual(buffer2))
                {
                    result = false;
                    break;
                }
            }
        }

        ArrayPool<byte>.Shared.Return(buffer1);
        ArrayPool<byte>.Shared.Return(buffer2);
        return result;
    }

    private async Task<string> RunFFtoolProcessWithErrorHandling(string toolName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        // Runs an FFmpeg tool and returns stdout, throwing with detailed context on failure.
        var toolPath =
            DirectoryPath.ParseAbsolute(FFmpegPathInitializer.BinariesDirectoryPath)
            .CombineFile(toolName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

        return await ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            toolPath,
            arguments,
            cancellationToken: cancellationToken);
    }

    private async Task<(string Output, string Error, int ReturnCode)> RunFFtoolProcess(
        string toolName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        // Runs an FFmpeg tool and returns stdout, stderr, and the exit code.
        var toolPath =
            DirectoryPath.ParseAbsolute(FFmpegPathInitializer.BinariesDirectoryPath)
            .CombineFile(toolName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

        return await ProcessUtils.RunProcessToStringAsync(
            toolPath,
            arguments,
            cancellationToken: cancellationToken);
    }

    private static int _tempFileCount;
    private static string GetUniqueTempFilePath(string fileExtension)
    {
        // Generates a unique temp file path with the specified extension and ensures it does not exist.
        _tempFilesDir.Create();
        var file = _tempFilesDir.CombineFile(Interlocked.Increment(ref _tempFileCount).ToString(CultureInfo.InvariantCulture) + fileExtension);
        file.Delete();
        return file.PathExport;
    }

    static Tests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _tempFilesDir.Delete(recursive: true, ignoreNotFound: true);
    }

    private async Task<(string File, IDisposable Disposable)> ExtractStream(
        string inputFile,
        string fileExtension,
        int streamIdx,
        CancellationToken cancellationToken)
    {
        // Extracts a specific stream to a temporary file and returns the path plus a disposer.
        // Create a temp file with the desired extension:
        string outputFilePath = GetUniqueTempFilePath(fileExtension);
        var disposeHelper = new DisposeHelper(() => File.Delete(outputFilePath));

        // Extract the desired stream using ffmpeg (w/o metadata):
        try
        {
            await RunFFtoolProcessWithErrorHandling(
                "ffmpeg",
                [
                    "-i", inputFile,
                    "-map", string.Create(CultureInfo.InvariantCulture, $"0:{streamIdx}"),
                    "-map_metadata:g", "-1",
                    "-map_metadata:s:0", "-1",
                    "-c", "copy",
                    "-fflags", "+bitexact",
                    "-copy_unknown",
                    "-xerror",
                    "-hide_banner",
                    "-y",
                    outputFilePath
                ],
                cancellationToken);
        }
        catch
        {
            disposeHelper.Dispose();
            throw;
        }

        // Return the output file path and the dispose helper:
        return (outputFilePath, disposeHelper);
    }

    private async Task<bool> CompareStreamEquality(
        string file1,
        string file2,
        string extension,
        int streamIdx1,
        int streamIdx2,
        CancellationToken cancellationToken)
    {
        // Extracts matching streams from two files and compares their contents.
        IDisposable disposeHelper;
        (string extractedFile1, disposeHelper) = await ExtractStream(file1, extension, streamIdx1, cancellationToken);
        using var disposeHelper1 = disposeHelper;
        (string extractedFile2, disposeHelper) = await ExtractStream(file2, extension, streamIdx2, cancellationToken);
        using var disposeHelper2 = disposeHelper;

        await using var stream1 = FilePath.ParseAbsolute(extractedFile1).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
        await using var stream2 = FilePath.ParseAbsolute(extractedFile2).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        return await AreStreamsEqual(stream1, stream2, cancellationToken);
    }

    private async Task<int> GetStreamCount(string fileName, CancellationToken cancellationToken)
    {
        // Returns the number of streams in a media file using ffprobe:
        return (await FFprobeUtils.GetVideoFileAsync(FilePath.ParseAbsolute(fileName), cancellationToken)).Streams.Length;
    }

    // Note: if expectedChanges is null, no changes are expected so the file will be compared byte-for-byte; otherwise, the file is expected to have been
    // changed and only the specified streams will be compared for {in}equality. If exceptionMessage is not null instead, it will be checked that the
    // specified exception is thrown when doing processing.
    private async Task CheckProcessing(
        FileRepo repo,
        VideoProcessingOptions options,
        string videoFileName,
        string? exceptionMessage,
        (int NewStreamCount, (int From, int To, string ExtensionToCheckWith, bool Equal)[] StreamMapping)? expectedChanges,
        string? addAsyncExtensionOverride = null,
        bool throwWhenSourceUnchanged = false,
        bool pathIsAbsolute = false,
        Func<string, Task>? afterFinishedAction = null)
    {
        // Create the processing pipeline:
        var pipeline = new VideoProcessor(options).ToPipeline(throwWhenSourceUnchanged: throwWhenSourceUnchanged);

        // Open the source video file:
        var fullFileName = pathIsAbsolute ? FilePath.ParseAbsolute(videoFileName) : _videoFilesDir.CombineFile(videoFileName);
        await using var stream = fullFileName.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId;

        if (exceptionMessage is not null)
        {
            // Check the expected exception is thrown:
            var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
            {
                await using var txn = await repo.BeginTransactionAsync();
                if (addAsyncExtensionOverride is not null)
                {
                    await txn.AddAsync(stream, addAsyncExtensionOverride, true, pipeline, TestContext.CancellationToken);
                }
                else
                {
                    await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
                }
            });
            ex.Message.ShouldBe(exceptionMessage);

            // No more checks to do:
            return;
        }
        else
        {
            // Add the video to the repo:

            await using var txn = await repo.BeginTransactionAsync();

            if (addAsyncExtensionOverride is not null)
            {
                fileId = (await txn.AddAsync(stream, addAsyncExtensionOverride, true, pipeline, TestContext.CancellationToken)).FileId;
            }
            else
            {
                fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            }

            await txn.CommitAsync(TestContext.CancellationToken);
        }

        // Get the processed video path:
        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        if (expectedChanges is null)
        {
            // We expected equality, check that:
            if (videoPath is not FileStream s1 || s1.Name != fullFileName.PathExport)
            {
                stream.Position = 0;
                await using var otherStream = videoPath.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
                (await AreStreamsEqual(stream, otherStream, TestContext.CancellationToken)).ShouldBeTrue();
            }
        }
        else
        {
            // We expected changes, check those:
            stream.Position = 0;
            await using var otherStream = videoPath.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
            (await AreStreamsEqual(stream, otherStream, TestContext.CancellationToken)).ShouldBeFalse();

            // Check the stream count:
            int actualStreamCount = await GetStreamCount(videoPath.PathExport, TestContext.CancellationToken);
            actualStreamCount.ShouldBe(expectedChanges.Value.NewStreamCount);

            // Check any mapped streams
            foreach (var (from, to, extensionToCheckWith, equal) in expectedChanges.Value.StreamMapping)
            {
                bool streamsEqual = await CompareStreamEquality(
                    fullFileName.PathExport,
                    videoPath.PathExport,
                    extensionToCheckWith,
                    from,
                    to,
                    TestContext.CancellationToken);

                try
                {
                    streamsEqual.ShouldBe(equal);
                }
                catch (Exception ex)
                {
                    throw new ShouldAssertException($"Stream comparison failed from stream {from} to stream {to}.", ex);
                }
            }
        }

        // If we have an after finished action, run it now before we clean up:
        if (afterFinishedAction is not null)
        {
            await afterFinishedAction(videoPath.PathExport);
        }
    }

    private static bool AreImagePixelsEqual(string imagePath1, string imagePath2)
    {
        // Compares two images pixel-by-pixel using ImageSharp (8bpc is good enough for our uses here).
        using var image1 = Image.Load<Rgba32>(imagePath1);
        using var image2 = Image.Load<Rgba32>(imagePath2);

        if (image1.Width != image2.Width || image1.Height != image2.Height)
            return false;

        bool equal = true;
        image1.ProcessPixelRows(image2, (accessor1, accessor2) =>
        {
            for (int j = 0; j < image1.Height; j++)
            {
                if (!accessor1.GetRowSpan(j).SequenceEqual(accessor2.GetRowSpan(j)))
                {
                    equal = false;
                    break;
                }
            }
        });
        return equal;
    }

    public static IEnumerable<object[]> VideosToCheck => field ??= [.. Enumerable.Range(1, 200).Select((x) => (object[])[
        ((IEnumerable<string>)[".mp4", ".mkv", ".mov", ".webm", ".avi", ".ts", ".mpeg", ".3gp"])
            .Select((y) => "video" + x.ToString(CultureInfo.InvariantCulture) + y).Single((y) => _videoFilesDir.CombineFile(y).Exists)
    ])];

    public static IEnumerable<string> InvalidVideoFiles => field ??=
    [
        "video159.ts",
        "video168.mp4",
        "video175.mp4",
    ];

    public static FrozenSet<string> VideoFilesWithMP4IncompatibleStreamsAfterProcessing => field ??=
    [
        "video134.mkv",
        "video135.ts",
        "video159.ts",
    ];

    public static FrozenSet<string> VideoFilesWithMP4IncompatibleStreams => field ??=
    [
        .. VideoFilesWithMP4IncompatibleStreamsAfterProcessing,
        "video8.3gp",
        "video9.avi",
        "video11.mkv",
        "video185.mkv",
        "video188.mp4",
        "video189.mp4",
        "video190.mp4",
        "video191.mp4",
        "video192.mkv",
        "video193.mkv",
        "video194.mkv",
        "video195.mkv",
    ];

    public static FrozenSet<string> VideoFilesWithThumbnails => field ??=
    [
        "video53.mp4",
        "video54.mp4",
        "video133.mp4",
    ];

    public static FrozenSet<string> VideoFilesWithSubtitles => field ??=
    [
        "video16.mkv",
        "video17.mkv",
        "video18.mkv",
        "video19.mkv",
        "video20.mp4",
        "video21.mp4",
        "video22.mkv",
        "video23.mkv",
        "video24.mkv",
        "video25.mkv",
        "video26.mkv",
        "video27.mkv",
        "video28.mkv",
        "video29.mkv",
        "video133.mp4",
        "video169.mkv",
        "video170.mkv",
    ];

    public static FrozenSet<string> VideoFilesWithHEVCIncompatibleDimensions => field ??=
    [
        "video136.mp4",
        "video137.mp4",
    ];

    public static FrozenSet<string> VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencoding => field ??=
    [
        "video66.mp4",
    ];

    public static FrozenSet<string> VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencodingWithNativeAAC => field ??=
    [
        "video63.mp4",
        "video64.mp4",
        "video65.mp4",
        "video153.mp4",
        "video154.mp4",
        "video155.mp4",
    ];

    public static IEnumerable<object[]> ValidVideosToCheck => field
        ??= VideosToCheck.Where((x) => !InvalidVideoFiles.Contains((string)x[0]));

    public static IEnumerable<object[]> ValidVideosToCheckToHEVC => field
        ??= ValidVideosToCheck.Where((x) => !VideoFilesWithHEVCIncompatibleDimensions.Contains((string)x[0]));

    public static FrozenSet<string> VideoFilesWithoutVideoStreams => field ??=
    [
        "video161.mp4",
    ];

    public static IEnumerable<object[]> ValidVideosWithVideoStreamsToCheck => field
        ??= ValidVideosToCheck.Where((x) => !VideoFilesWithoutVideoStreams.Contains((string)x[0]));
}
