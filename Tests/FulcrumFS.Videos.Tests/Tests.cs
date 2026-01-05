using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Globalization;
#if !DEBUG
using System.Linq.Expressions;
using System.Reflection;
#endif
using PrefixClassName.MsTest;
using Shouldly;
using Singulink.IO;
using Singulink.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

#pragma warning disable SA1118 // Parameter should not span multiple lines
#if !DEBUG
#pragma warning disable SA1310 // Field names should not contain underscore
#endif

namespace FulcrumFS.Videos;

[PrefixTestClass]
public sealed class Tests
{
    public required TestContext TestContext { get; set; }

    /*
    ===============================================================================================
    ================================= SHARED TEST RUNNING HELPERS =================================
    ===============================================================================================
    */

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

#if !DEBUG
    // Helper class that caches reflection types and methods for calling internal APIs in release builds.
    private static class ReflectionHelper
    {
        private static readonly Assembly FulcrumFSVideosAssembly = typeof(VideoProcessor).Assembly;

        // FFmpegProcessUtils delegates:
        private static readonly Type ProcessUtilsType = FulcrumFSVideosAssembly.GetType("FulcrumFS.Videos.ProcessUtils", throwOnError: true)!;

        public static readonly Func<IAbsoluteFilePath, IEnumerable<string>, bool, CancellationToken, bool, ValueTask<string>>
            ProcessUtils_RunProcessToStringWithErrorHandlingAsync =
                ProcessUtilsType.GetMethod("RunProcessToStringWithErrorHandlingAsync", BindingFlags.Static | BindingFlags.Public)!
                    .CreateDelegate<Func<IAbsoluteFilePath, IEnumerable<string>, bool, CancellationToken, bool, ValueTask<string>>>();

        public static readonly
            Func<IAbsoluteFilePath, IEnumerable<string>, bool, CancellationToken, bool, ValueTask<(string Output, string Error, int ReturnCode)>>
            ProcessUtils_RunProcessToStringAsync =
                ProcessUtilsType.GetMethod("RunProcessToStringAsync", BindingFlags.Static | BindingFlags.Public)!
                    .CreateDelegate<Func<
                        IAbsoluteFilePath,
                        IEnumerable<string>,
                        bool,
                        CancellationToken,
                        bool,
                        ValueTask<(string Output, string Error, int ReturnCode)>>>();

        // FFprobeUtils types and delegates:
        private static readonly Type FFprobeUtilsType = FulcrumFSVideosAssembly.GetType("FulcrumFS.Videos.FFprobeUtils", throwOnError: true)!;
        private static readonly Type VideoFileInfoType = FFprobeUtilsType.GetNestedType("VideoFileInfo")!;
        private static readonly Type StreamInfoType = FFprobeUtilsType.GetNestedType("StreamInfo")!;

        public static readonly Func<IAbsoluteFilePath, CancellationToken, object> FFprobeUtils_GetVideoFileAsync =
            FFprobeUtilsType.GetMethod("GetVideoFileAsync", BindingFlags.Static | BindingFlags.Public)!
                .CreateDelegate<Func<IAbsoluteFilePath, CancellationToken, object>>();

        // Delegate for getting the Streams property from a VideoFileInfo (returns boxed ImmutableArray<StreamInfo>):
        private static readonly ParameterExpression VideoFileInfo_GetStreams_Param = Expression.Parameter(typeof(object), "instance");

        public static readonly Func<object, object> VideoFileInfo_GetStreams =
            Expression.Lambda<Func<object, object>>(
                Expression.Convert(
                    Expression.Property(
                        Expression.Convert(VideoFileInfo_GetStreams_Param, VideoFileInfoType),
                        VideoFileInfoType.GetProperty("Streams")!),
                    typeof(object)),
                VideoFileInfo_GetStreams_Param).Compile();

        // Delegate for getting the Length property from an ImmutableArray<StreamInfo>:
        public static readonly Func<object, int> ImmutableArrayOfStreamInfo_GetLength =
            typeof(ReflectionHelper).GetMethod(nameof(GetImmutableArrayLength), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(StreamInfoType)
                .CreateDelegate<Func<object, int>>();

        private static int GetImmutableArrayLength<T>(object array) => ((ImmutableArray<T>)array).Length;

        // Delegate for awaiting Task<VideoFileInfo> as Task<object>:
        public static readonly Func<object, Task<object>> DowncastTaskOfVideoFileInfo =
            typeof(ReflectionHelper).GetMethod(nameof(DowncastTaskAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(VideoFileInfoType)
                .CreateDelegate<Func<object, Task<object>>>();

        private static async Task<object> DowncastTaskAsync<T>(object task) where T : notnull
        {
            return await (Task<T>)task;
        }
    }
#endif

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

#if DEBUG
        // In debug builds we can call the internal method directly:
        return await ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            toolPath,
            arguments,
            cancellationToken: cancellationToken);
#else
        // Use cached reflection delegate to call the internal method - we use reflection here to avoid having to duplicate the error handling code here:
        return await ReflectionHelper.ProcessUtils_RunProcessToStringWithErrorHandlingAsync(toolPath, arguments, false, cancellationToken, true);
#endif
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

#if DEBUG
        // In debug builds we can call the internal method directly:
        return await ProcessUtils.RunProcessToStringAsync(
            toolPath,
            arguments,
            cancellationToken: cancellationToken);
#else
        // Use cached reflection delegate to call the internal method - we use reflection here to avoid having to duplicate the error handling code here:
        return await ReflectionHelper.ProcessUtils_RunProcessToStringAsync(toolPath, arguments, false, cancellationToken, true);
#endif
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
        // Returns the number of streams in a media file using ffprobe (direct or via reflection).
#if DEBUG
        // In debug builds we can call the internal method directly:
        return (await FFprobeUtils.GetVideoFileAsync(FilePath.ParseAbsolute(fileName), cancellationToken)).Streams.Length;
#else
        // Use cached delegates to call the internal methods - we use reflection to avoid having to duplicate the ffprobe handling code here:
        object task = ReflectionHelper.FFprobeUtils_GetVideoFileAsync(FilePath.ParseAbsolute(fileName), cancellationToken);
        object videoFileInfo = await ReflectionHelper.DowncastTaskOfVideoFileInfo(task);
        object streams = ReflectionHelper.VideoFileInfo_GetStreams(videoFileInfo);
        return ReflectionHelper.ImmutableArrayOfStreamInfo_GetLength(streams);
#endif
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

        FileId? fileId = null;

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

    /*
    ===============================================================================================
    ======================================== TEST METHODS =========================================
    ===============================================================================================
    */

    [TestMethod]
    public async Task TestSourceValidation()
    {
        // Tests source video/audio stream validation options including duration, stream count, dimensions, and pixel count limits.

        using var repoCtx = GetRepo(out var repo);

        async Task RunTest(string fileName, VideoProcessingOptions options, string? expectedMessage)
        {
            try
            {
                await CheckProcessing(repo, options, fileName, expectedMessage, expectedChanges: null);
            }
            catch (Exception ex)
            {
                throw new ShouldAssertException($"Error during RunTest with file '{fileName}' and options '{options}'.", ex);
            }
        }

        // Validate the predefined configs wrt the None config, since we only test off of that:
        VideoStreamValidationOptions.StandardVideo.ShouldBe(VideoStreamValidationOptions.None with { MinStreams = 1, MaxStreams = 1 });
        AudioStreamValidationOptions.StandardAudio.ShouldBe(AudioStreamValidationOptions.None with { MinStreams = 1, MaxStreams = 1 });
        AudioStreamValidationOptions.OptionalStandardAudio.ShouldBe(AudioStreamValidationOptions.None with { MaxStreams = 1 });

        // Use the following as our base options
        var baseOptions = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = false,
        };

        // video1.mp4 has the following properties that we'll test validation with: width - 128, height - 72, duration - 1s, 1 video stream, 1 audio stream
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2) } },
            "Video stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Video stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2) } },
            "Audio stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Audio stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1) } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video1.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinWidth = 129 } },
            "Video stream 0 width is less than the minimum required width.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinWidth = 128 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxWidth = 127 } },
            "Video stream 0 width exceeds the maximum allowed width.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxWidth = 128 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinHeight = 73 } },
            "Video stream 0 height is less than the minimum required height.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinHeight = 72 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxHeight = 71 } },
            "Video stream 0 height exceeds the maximum allowed height.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxHeight = 72 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinPixels = 9217 } },
            "Video stream 0 is less than the minimum required pixel count.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinPixels = 9216 } },
            null);
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxPixels = 9215 } },
            "Video stream 0 exceeds the maximum allowed pixel count.");
        await RunTest(
            "video1.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxPixels = 9216 } },
            null);

        // video133.mp4 has the following properties that we'll test validation with: 8 video streams, 4 audio streams, 3 subtitle streams, 3 thumbnail streams
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 9 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 8 } },
            null);
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 7 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video133.mp4",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 8 } },
            null);
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 5 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 4 } },
            null);
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 3 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video133.mp4",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 4 } },
            null);

        // video134.mkv has the following properties that we'll test validation with: 1 video stream, 1 audio stream, 1 attachment stream
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video134.mkv",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video134.mkv",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 1 } },
            null);

        // video135.ts has the following properties that we'll test validation with: 1 video stream, 1 audio stream, 1 data stream
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of video streams is less than the minimum required.");
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of video streams exceeds the maximum allowed.");
        await RunTest(
            "video135.ts",
            baseOptions with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 2 } },
            "The number of audio streams is less than the minimum required.");
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinStreams = 1 } },
            null);
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 0 } },
            "The number of audio streams exceeds the maximum allowed.");
        await RunTest(
            "video135.ts",
            baseOptions with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 1 } },
            null);
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheck))]
    public async Task TestPreserveOptions(string fileName)
    {
        // Note: we are also testing that oversized videos don't re-encode to H.264 unnecessarily here (e.g., video143.mp4).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve,
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheck))]
    public async Task TestH264StandardizedOptions(string fileName)
    {
        // This tests that it can succedd processing every valid file, that the stream count matches afterwards (except for those with streams incompatible
        // with MP4), and that all streams have been changed (re-encoded):

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.StandardizedH264AACMP4 with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            TryPreserveUnrecognizedStreams = true, // Allows us to test our subtitle handling code also
        };

        if (!VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) && !VideoFilesWithThumbnails.Contains(fileName))
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            FileId? fileId = null;

            await using var txn = await repo.BeginTransactionAsync();
            fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        }
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheckToHEVC))]
    public async Task TestHEVCStandardizedOptions(string fileName)
    {
        // Same as TestStandardizedOptions, but using HEVC as the target video codec to test our hevc handling:

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.StandardizedHEVCAACMP4 with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
#if CI
            VideoCompressionLevel = VideoCompressionLevel.Lowest, // Use lowest compression level to speed up tests
#endif
            TryPreserveUnrecognizedStreams = true, // Allows us to test our subtitle handling code also
#if DEBUG
            ForceLibFDKAACUsage = false, // Force native aac usage in debug mode to test both code paths
#endif
        };

        if (!VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) && !VideoFilesWithThumbnails.Contains(fileName))
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            FileId? fileId = null;

            await using var txn = await repo.BeginTransactionAsync();
            fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        }
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheck))]
    public async Task TestH264ReencodeOptions(string fileName)
    {
        // This tests that it can succeed processing every valid file (with original settings, rather than limited ones), that the stream count matches
        // afterwards (except for those with streams incompatible with MP4), and that all streams have been changed (re-encoded):

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always, // Also test libfdk_aac / fallback usage here
            ResultVideoCodecs = [VideoCodec.H264],
            ResultAudioCodecs = [AudioCodec.AAC],
            TryPreserveUnrecognizedStreams = true, // Allows us to use the same list as for TestStandardizedOptions
#if CI
            VideoCompressionLevel = VideoCompressionLevel.Lowest, // Use lowest compression level to speed up tests
#endif
        };

        bool removeStreams = VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) || VideoFilesWithThumbnails.Contains(fileName);
        if (VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencoding.Contains(fileName))
        {
            options = options with { RemoveAudioStreams = true };
            removeStreams = true;
        }

        if (!removeStreams)
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            FileId? fileId = null;

            await using var txn = await repo.BeginTransactionAsync();
            fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        }
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosToCheckToHEVC))]
    public async Task TestHEVCReencodeOptions(string fileName)
    {
        // Same as TestH264ReencodeOptions, but using HEVC as the target video codec to test our hevc handling:

        using var repoCtx = GetRepo(out var repo);
        var options = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always, // Also test aac usage here (in debug mode)
#if DEBUG
            ForceLibFDKAACUsage = false, // Force native aac usage in debug mode to test both code paths
#endif
            ResultVideoCodecs = [VideoCodec.H264],
            ResultAudioCodecs = [AudioCodec.AAC],
            TryPreserveUnrecognizedStreams = true, // Allows us to use the same list as for TestStandardizedOptions
        };

        bool removeStreams = VideoFilesWithMP4IncompatibleStreamsAfterProcessing.Contains(fileName) || VideoFilesWithThumbnails.Contains(fileName);
        if (VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencoding.Contains(fileName) ||
            VideoFilesWithMP4IncompatibleAudioStreamsAfterOnlyReencodingWithNativeAAC.Contains(fileName))
        {
            options = options with { RemoveAudioStreams = true };
            removeStreams = true;
        }

        if (!removeStreams)
        {
            int oldCount = await GetStreamCount(_videoFilesDir.CombineFile(fileName).PathExport, TestContext.CancellationToken);

            await CheckProcessing(
                repo,
                options,
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: oldCount,
                    StreamMapping: VideoFilesWithMP4IncompatibleStreams.Contains(fileName) || VideoFilesWithSubtitles.Contains(fileName)
                        ? []
                        : [.. Enumerable.Range(0, oldCount).Select((i) => (From: i, To: i, ExtensionToCheckWith: ".mp4", Equal: false)).ToArray()]));
        }
        else
        {
            var pipeline = new VideoProcessor(options).ToPipeline();

            await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            FileId? fileId = null;

            await using var txn = await repo.BeginTransactionAsync();
            fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        }
    }

    [TestMethod]
    [DynamicData(nameof(VideoFilesWithSubtitles))]
    public async Task TestStandardizedOptionsNotPreservingUnrecognizedStreams(string fileName)
    {
        // Tests that subtitle streams are correctly stripped when TryPreserveUnrecognizedStreams is disabled (default for StandardizedH264AACMP4).

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.StandardizedH264AACMP4 with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
        }).ToPipeline();

        await using var stream = _videoFilesDir.CombineFile(fileName).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId = null;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
    }

    [TestMethod]
    public async Task TestCreateAndDelete()
    {
        // Tests the complete file repository lifecycle: create a video file with a scaled variant, then delete both.
        // Verifies files exist after commit, are removed after deletion, and parent directories are cleaned up.

        using var repoCtx = GetRepo(out var repo);

        await using var stream = _videoFilesDir.CombineFile("video1.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId fileId;

        await using (var txn = await repo.BeginTransactionAsync())
        {
            var added = await txn.AddAsync(stream, leaveOpen: false, new VideoProcessor(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            }).ToPipeline(), TestContext.CancellationToken);
            fileId = added.FileId;

            await repo.AddVariantAsync(added.FileId, "scaled_down", new VideoProcessor(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResizeOptions = new(VideoResizeMode.FitDown, 64, 36),
            }).ToPipeline(), TestContext.CancellationToken);

            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var scaledDownPath = await repo.GetVariantAsync(fileId, "scaled_down");
        scaledDownPath.Exists.ShouldBeTrue();

        await using (var txn = await repo.BeginTransactionAsync())
        {
            await txn.DeleteAsync(fileId, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        videoPath.Exists.ShouldBeFalse();
        scaledDownPath.Exists.ShouldBeFalse();

        videoPath.ParentDirectory.Exists.ShouldBeFalse();
        videoPath.ParentDirectory.ParentDirectory!.Exists.ShouldBeTrue();
    }

    [TestMethod]
    public async Task ThrowWhenSourceUnchanged()
    {
        // Tests that FileProcessingException is thrown when throwWhenSourceUnchanged is enabled and the file would be unchanged.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video1.mp4",
            exceptionMessage: "File processing did not result in any changes to the source file.",
            expectedChanges: null,
            throwWhenSourceUnchanged: true);
    }

    [TestMethod]
    public async Task TestUnsupportedExtension()
    {
        // Tests that files with extensions not matching the allowed SourceFormats are rejected early with a clear error.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceFormats = [MediaContainerFormat.MP4],
            },
            "video2.mkv",
            exceptionMessage: "Extension '.mkv' is not allowed. Allowed extensions: .mp4, .mov, .m4a, .3gp, .3g2, .mj2",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestUnsupportedFormat()
    {
        // Tests that files with container formats not matching SourceFormats are rejected after format detection.
        // Uses .mp4 extension override to bypass extension check and trigger actual format validation.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceFormats = [MediaContainerFormat.MP4],
            },
            "video2.mkv",
            exceptionMessage: "The source video format 'matroska,webm' is not supported by this processor.",
            expectedChanges: null,
            addAsyncExtensionOverride: ".mp4");
    }

    [TestMethod]
    public async Task TestSupportedFormat()
    {
        // Tests that files matching the SourceFormats constraint are accepted and processed normally.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceFormats = [MediaContainerFormat.MP4],
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestOtherInputValidation()
    {
        // Tests validation of videos with misleading metadata. Uses video158.mkv which has advertised duration of 0.5s
        // but actual measured duration of ~1s, to verify both advertised and measured duration checks work correctly.

        using var repoCtx = GetRepo(out var repo);

        async Task RunTest(string fileName, VideoProcessingOptions options, string? expectedMessage)
        {
            try
            {
                await CheckProcessing(repo, options, fileName, expectedMessage, expectedChanges: null);
            }
            catch (Exception ex)
            {
                throw new ShouldAssertException($"Error during RunTest with file '{fileName}' and options '{options}'.", ex);
            }
        }

        var configNoForceCheck = VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = false,
        };

        // video158.mkv has an advertised duration of 0.5s, but actual duration is just over 1s:
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Measured video stream duration exceeds the maximum allowed length.");
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1.5) } },
            null);
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            "Measured audio stream duration exceeds the maximum allowed length.");
        await RunTest(
            "video158.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(1.5) } },
            null);
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.25) } },
            "Video stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            null);
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.25) } },
            "Audio stream 0 is longer than the maximum allowed duration.");
        await RunTest(
            "video158.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MaxLength = TimeSpan.FromSeconds(0.5) } },
            null);

        // video196.mkv has an advertised duration of 1.5s, but actual duration is just over 1s:
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            "Measured video stream duration is less than the minimum required length.");
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.0) } },
            null);
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            "Measured audio stream duration is less than the minimum required length.");
        await RunTest(
            "video196.mkv",
            VideoProcessingOptions.Preserve with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.0) } },
            null);
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2.0) } },
            "Video stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { VideoSourceValidation = VideoStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            null);
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(2.0) } },
            "Audio stream 0 is shorter than the minimum required duration.");
        await RunTest(
            "video196.mkv",
            configNoForceCheck with { AudioSourceValidation = AudioStreamValidationOptions.None with { MinLength = TimeSpan.FromSeconds(1.5) } },
            null);
    }

    [TestMethod]
    public async Task TestUnsupportedVideoCodec()
    {
        // Tests that video files with codecs not in SourceVideoCodecs are rejected.
        // Uses video10.mp4 (HEVC) with H264-only source constraint.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.H264],
            },
            "video10.mp4",
            exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestSupportedVideoCodec()
    {
        // Tests that video files with codecs matching SourceVideoCodecs are accepted.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.H264],
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestUnsupportedAudioCodec()
    {
        // Tests that files with audio codecs not in SourceAudioCodecs are rejected.
        // Uses video4.webm (Opus audio) with AAC-only source constraint.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceAudioCodecs = [AudioCodec.AAC],
            },
            "video4.webm",
            exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestSupportedAudioCodec()
    {
        // Tests that files with audio codecs matching SourceAudioCodecs are accepted.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceAudioCodecs = [AudioCodec.AAC],
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestUnwantedOutputFormatRemuxes()
    {
        // Tests that files in formats not matching ResultFormats are remuxed (not re-encoded) to a supported format.
        // Uses video2.mkv with MP4-only result format to verify streams are preserved but container changes.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultFormats = [MediaContainerFormat.MP4],
            },
            "video2.mkv",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestUnwantedOutputVideoCodecReencodes()
    {
        // Tests that video streams with codecs not in ResultVideoCodecs are re-encoded while audio is preserved.
        // Uses video10.mp4 (HEVC) with H264-only result to verify video re-encoding, audio stream copy.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultVideoCodecs = [VideoCodec.H264],
            },
            "video10.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestUnwantedOutputAudioCodecReencodes()
    {
        // Tests that audio streams with codecs not in ResultAudioCodecs are re-encoded while video is preserved.
        // Uses video14.mp4 (Vorbis audio) with AAC-only result to verify audio re-encoding, video stream copy.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultAudioCodecs = [AudioCodec.AAC],
            },
            "video14.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestWantedOutputFormatDoesntRemuxUnnecessarily()
    {
        // Tests that files already in an allowed ResultFormats container are not unnecessarily remuxed.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultFormats = [MediaContainerFormat.MP4, MediaContainerFormat.Mkv],
            },
            "video2.mkv",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestWantedOutputVideoCodecDoesntReencodeUnnecessarily()
    {
        // Tests that video streams already in an allowed ResultVideoCodecs codec are not unnecessarily re-encoded.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultVideoCodecs = [VideoCodec.HEVCAnyTag],
            },
            "video10.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestWantedOutputAudioCodecDoesntReencodeUnnecessarily()
    {
        // Tests that audio streams already in an allowed ResultAudioCodecs codec are not unnecessarily re-encoded.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultAudioCodecs = [AudioCodec.AAC, AudioCodec.Vorbis],
            },
            "video14.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestForceProgressiveDownload()
    {
        // Tests that ForceProgressiveDownload correctly moves the moov atom before mdat for streaming compatibility.
        // Verifies the original file has moov after mdat, and the processed file has moov before mdat - this provides improved streaming performance, but does
        // not make it seekable.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ForceProgressiveDownload = true,
        }).ToPipeline();

        await using var stream = _videoFilesDir.CombineFile("video1.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId = null;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Check that the original file was not progressive download, and the new one is (note: the check is very basic & could be fooled, but is sufficient
        // for test purposes):
        var (outputOriginal, errorOriginal, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", _videoFilesDir.CombineFile("video1.mp4").PathExport, "-v", "trace"],
            TestContext.CancellationToken);
        var (outputModified, errorModified, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-v", "trace"],
            TestContext.CancellationToken);
        returnCodeOriginal.ShouldBe(0);
        returnCodeModified.ShouldBe(0);
        int idxMoovOriginal = errorOriginal.IndexOf("moov", StringComparison.Ordinal);
        int idxMoovModified = errorModified.IndexOf("moov", StringComparison.Ordinal);
        int idxMdatOriginal = errorOriginal.IndexOf("mdat", StringComparison.Ordinal);
        int idxMdatModified = errorModified.IndexOf("mdat", StringComparison.Ordinal);
        idxMoovOriginal.ShouldBeGreaterThanOrEqualTo(0);
        idxMoovModified.ShouldBeGreaterThanOrEqualTo(0);
        idxMdatOriginal.ShouldBeGreaterThanOrEqualTo(0);
        idxMdatModified.ShouldBeGreaterThanOrEqualTo(0);
        idxMoovOriginal.ShouldBeGreaterThan(idxMdatOriginal);
        idxMoovModified.ShouldBeLessThan(idxMdatModified);
    }

    [TestMethod]
    public async Task TestTryPreserveUnrecognizedStreamsFalse()
    {
        // Tests that unrecognized streams are stripped when TryPreserveUnrecognizedStreams is false.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: no extra streams, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video20.mp4: has subtitle stream also, stripped to 2 streams.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video20.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video134.mkv: has attachment, stripped to 2 streams.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video134.mkv",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

         // video135.ts: has data stream, stripped to 2 streams.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                TryPreserveUnrecognizedStreams = false,
            },
            "video135.ts",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping: [])); // Can't easily compare streams as going from .ts to .mp4 makes it appear re-encoded.
    }

    [TestMethod]
    public async Task TestTryStripThumbnails()
    {
        // Tests that TryStripThumbnails removes embedded video thumbnail streams from the output.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: no thumbnail stream, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video53.mp4: has a png thumbnail stream.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video53.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video54.mp4: has a mjpeg thumbnail stream.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video54.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestNoVideoOrAudioStreamsThrows()
    {
        // Tests that processing fails with clear error message when input contains no audio or video streams.

        using var repoCtx = GetRepo(out var repo);

        // video159.ts: no audio or video streams, but one data stream.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video159.ts",
            exceptionMessage: "The source video contains no audio or video streams.",
            expectedChanges: null);

        // video168.mp4: no streams at all.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video168.mp4",
            exceptionMessage: "The source video contains no audio or video streams.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestSingleAudioOrVideoStreamFiles()
    {
        // Tests that files with only video (video160.mp4) or only audio (video161.mp4) streams can be processed successfully.

        // video160.mp4: video-only file.
        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video161.mp4: audio-only file.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video161.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRemoveAudioThrows()
    {
        // Tests that requesting audio removal from an audio-only file (video161.mp4) results in an error,
        // but succeeds for video-only files (video160.mp4) since there's nothing to remove.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemoveAudioStreams = true,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemoveAudioStreams = true,
            },
            "video161.mp4",
            exceptionMessage: "The source video contains no video streams.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRemoveAudioStreamAdjustment()
    {
        // Tests that RemoveAudioStreams correctly strips audio streams from video files.
        // video160.mp4 (video-only) is unchanged.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemoveAudioStreams = true,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemoveAudioStreams = true,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
            ]));
    }

    [TestMethod]
    public async Task TestAlwaysReencodeVideoStreams()
    {
        // Tests that VideoReencodeMode.Always forces video stream re-encoding even when codec is acceptable.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: video re-encoded, audio unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video160.mp4: video-only, re-encoded.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
            ]));

        // video161.mp4: audio-only, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
            },
            "video161.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestAlwaysReencodeAudioStreams()
    {
        // Tests that AudioReencodeMode.Always forces audio stream re-encoding even when codec is acceptable.

        using var repoCtx = GetRepo(out var repo);

        // video1.mp4: audio re-encoded, video unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));

        // video160.mp4: video-only, unchanged.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video160.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video161.mp4: audio-only, re-encoded.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video161.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestMaxChromasubsamplingRespected()
    {
        // Tests that MaximumChromaSubsampling is correctly enforced, with downsampling when needed.

        using var repoCtx = GetRepo(out var repo);

        // Test video is 4:2:0, max is 4:2:0.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:0, max is 4:2:2.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:0, max is 4:4:4.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling444,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:2, max is 4:2:2.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
            },
            "video30.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:2, max is 4:4:4.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling444,
            },
            "video30.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:4:4, max is 4:4:4.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling444,
            },
            "video31.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 4:2:2, max is 4:2:0.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
            },
            "video30.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:0 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 4:4:4, max is 4:2:2.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
            },
            "video31.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:2 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumChromaSubsampling = ChromaSubsampling.Subsampling422,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 4:4:4, max is 4:2:0.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
            },
            "video31.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:0 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });
    }

    [TestMethod]
    public async Task TestMaxBitsPerSampleRespected()
    {
        // Tests that MaximumBitsPerChannel is correctly enforced, with downsampling when needed.

        using var repoCtx = GetRepo(out var repo);

        // Test video is 8 bpc, max is 8 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 8 bpc, max is 10 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits10,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 8 bpc, max is 12 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits12,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 10 bpc, max is 10 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits10,
            },
            "video32.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 10 bpc, max is 12 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits12,
            },
            "video32.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 12 bpc, max is 12 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits12,
            },
            "video43.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test video is 10 bpc, max is 8 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
            },
            "video32.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 8 bpc now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumBitsPerChannel = BitsPerChannel.Bits8,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 12 bpc, max is 10 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits10,
            },
            "video43.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 10 bpc now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumBitsPerChannel = BitsPerChannel.Bits10,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });

        // Test video is 12 bpc, max is 8 bpc.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
            },
            "video43.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]), afterFinishedAction: async (newPath) =>
            {
                // Validate that we actually have 4:2:0 now:
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        MaximumBitsPerChannel = BitsPerChannel.Bits8,
                    },
                    newPath,
                    exceptionMessage: null,
                    expectedChanges: null,
                    pathIsAbsolute: true);
            });
    }

    [TestMethod]
    [DataRow("video1.mp4", 420, 8, false)] // video1.mp4 is yuv420p / pc
    [DataRow("video30.mp4", 422, 8, false)] // video30.mp4 is yuv422p / pc
    [DataRow("video31.mp4", 444, 8, false)] // video31.mp4 is yuv444p / pc
    [DataRow("video32.mp4", 420, 10, false)] // video32.mp4 is yuv420p10le / pc
    [DataRow("video33.mp4", 422, 10, false)] // video33.mp4 is yuv422p10le / pc
    [DataRow("video34.mp4", 444, 10, false)] // video34.mp4 is yuv444p10le / pc
    [DataRow("video35.mp4", 420, 8, false)] // video35.mp4 is yuv420p / tv
    [DataRow("video36.mp4", 422, 8, false)] // video36.mp4 is yuv422p / tv
    [DataRow("video37.mp4", 444, 8, false)] // video37.mp4 is yuv444p / tv
    [DataRow("video38.mp4", 420, 10, false)] // video38.mp4 is yuv420p10le / tv
    [DataRow("video39.mp4", 422, 10, false)] // video39.mp4 is yuv422p10le / tv
    [DataRow("video40.mp4", 444, 10, false)] // video40.mp4 is yuv444p10le / tv
    [DataRow("video41.mp4", 444, 8, true)] // video41.mp4 is gbrp / pc
    [DataRow("video42.mp4", 444, 10, true)] // video42.mp4 is gbrp10le / pc
    [DataRow("video43.mp4", 420, 12, false)] // video43.mp4 is yuv420p12le / pc
    [DataRow("video44.mp4", 422, 12, false)] // video44.mp4 is yuv422p12le / pc
    [DataRow("video45.mp4", 444, 12, false)] // video45.mp4 is yuv444p12le / pc
    [DataRow("video46.mp4", 444, 12, true)] // video46.mp4 is gbrp12le / pc
    [DataRow("video47.mp4", 420, 12, false)] // video47.mp4 is yuv420p12le / tv
    [DataRow("video48.mp4", 422, 12, false)] // video48.mp4 is yuv422p12le / tv
    [DataRow("video49.mp4", 444, 12, false)] // video49.mp4 is yuv444p12le / tv
    [DataRow("video50.mp4", 420, 10, false)] // video50.mp4 is yuv420p10le / pc
    [DataRow("video51.webm", 420, 8, true)] // video51.webm is yuva420p / pc
    [DataRow("video52.webm", 420, 8, true)] // video52.webm is yuva420p / tv
    public async Task TestPixelFormats(string fileName, int actualChromaSubsampling, int actualBpc, bool isAbnormal)
    {
        // Note: we are validate all of the following here: all known pixel formats are handled as expected, abnormal pixel formats are re-encoded when max
        // chroma subsampling is not "Preserve", max bits per channel / chroma subsampling correctly re-encodes / preserves, both pc & tv variants of pixel
        // formats (that support both variants) are handled correctly.

        using var repoCtx = GetRepo(out var repo);

        await Parallel.ForEachAsync(
        [
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Bits8),
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Bits10),
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Bits12),
            (ChromaSubsampling.Subsampling420, BitsPerChannel.Preserve),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Bits8),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Bits10),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Bits12),
            (ChromaSubsampling.Subsampling422, BitsPerChannel.Preserve),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Bits8),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Bits10),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Bits12),
            (ChromaSubsampling.Subsampling444, BitsPerChannel.Preserve),
            (ChromaSubsampling.Preserve, BitsPerChannel.Bits8),
            (ChromaSubsampling.Preserve, BitsPerChannel.Bits10),
            (ChromaSubsampling.Preserve, BitsPerChannel.Bits12),
        ], TestContext.CancellationToken, async (info, _) =>
        {
            var (subsampling, bpc) = info;

            // Determine if we should reencode based on the actual vs. max settings:
            bool shouldReencode =
                (isAbnormal && subsampling != ChromaSubsampling.Preserve) ||
                (actualChromaSubsampling > subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 420,
                    ChromaSubsampling.Subsampling422 => 422,
                    ChromaSubsampling.Subsampling444 => 444,
                    ChromaSubsampling.Preserve => actualChromaSubsampling,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                }) ||
                (actualBpc > bpc switch
                {
                    BitsPerChannel.Bits8 => 8,
                    BitsPerChannel.Bits10 => 10,
                    BitsPerChannel.Bits12 => 12,
                    BitsPerChannel.Preserve => actualBpc,
                    _ => throw new UnreachableException("Unimplemented bits per channel value."),
                });

            // For normal pixel formats, we just want to validate that the bpc / chroma subsampling are detected correctly, as we have already tested
            // re-encoding / preserving combos in ValidateMaxChromaSubsamplingRespected / ValidateMaxBitsPerSampleRespected. Therefore, we only test exact
            // match, next lower bpc, and next lower chroma subsampling for normal pixel formats.
            if (!isAbnormal)
            {
                bool isExactMatch = actualChromaSubsampling == subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 420,
                    ChromaSubsampling.Subsampling422 => 422,
                    ChromaSubsampling.Subsampling444 => 444,
                    ChromaSubsampling.Preserve => int.MaxValue,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                };
                if (isExactMatch)
                {
                    isExactMatch = actualBpc == bpc switch
                    {
                        BitsPerChannel.Bits8 => 8,
                        BitsPerChannel.Bits10 => 10,
                        BitsPerChannel.Bits12 => 12,
                        BitsPerChannel.Preserve => int.MaxValue,
                        _ => throw new UnreachableException("Unimplemented bits per channel value."),
                    };
                }

                bool isNextLowerBpc = actualChromaSubsampling == subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 420,
                    ChromaSubsampling.Subsampling422 => 422,
                    ChromaSubsampling.Subsampling444 => 444,
                    ChromaSubsampling.Preserve => int.MaxValue,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                };
                if (isNextLowerBpc)
                {
                    isNextLowerBpc = actualBpc == bpc switch
                    {
                        BitsPerChannel.Bits8 => 10,
                        BitsPerChannel.Bits10 => 12,
                        BitsPerChannel.Bits12 => -1,
                        BitsPerChannel.Preserve => int.MaxValue,
                        _ => throw new UnreachableException("Unimplemented bits per channel value."),
                    };
                }

                bool isNextLowerChromaSubsampling = actualChromaSubsampling == subsampling switch
                {
                    ChromaSubsampling.Subsampling420 => 422,
                    ChromaSubsampling.Subsampling422 => 444,
                    ChromaSubsampling.Subsampling444 => -1,
                    ChromaSubsampling.Preserve => int.MaxValue,
                    _ => throw new UnreachableException("Unimplemented chroma subsampling value."),
                };
                if (isNextLowerChromaSubsampling)
                {
                    isNextLowerChromaSubsampling = actualBpc == bpc switch
                    {
                        BitsPerChannel.Bits8 => 8,
                        BitsPerChannel.Bits10 => 10,
                        BitsPerChannel.Bits12 => 12,
                        BitsPerChannel.Preserve => int.MaxValue,
                        _ => throw new UnreachableException("Unimplemented bits per channel value."),
                    };
                }

                if (!isExactMatch && !isNextLowerBpc && !isNextLowerChromaSubsampling)
                {
                    return;
                }
            }

            // Helper for our potential checks below:
            async Task Check(VideoProcessingOptions options, bool shouldReencode)
            {
                await CheckProcessing(
                    repo,
                    options,
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: shouldReencode ? (NewStreamCount: 2, StreamMapping:
                    [
                        (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                        (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
                    ]) : null, afterFinishedAction: shouldReencode ? async (newPath) =>
                    {
                        // Validate that we actually have what we expected now:
                        await CheckProcessing(
                            repo,
                            VideoProcessingOptions.Preserve with
                            {
                                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                                MaximumChromaSubsampling = subsampling,
                                MaximumBitsPerChannel = bpc,
                            },
                            newPath,
                            exceptionMessage: null,
                            expectedChanges: null,
                            pathIsAbsolute: true);
                    } : null);
            }

            // Run the processor & check if we reencoded or not as expected:
            await Check(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaximumChromaSubsampling = subsampling,
                MaximumBitsPerChannel = bpc,
#if CI
                VideoCompressionLevel = VideoCompressionLevel.Low, // Use low compression level to speed up tests
#endif
            }, shouldReencode);

            // Validate it works in HEVC also:
            await Check(VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
                ResultVideoCodecs = [VideoCodec.HEVC],
                MaximumChromaSubsampling = subsampling,
                MaximumBitsPerChannel = bpc,
#if CI
                VideoCompressionLevel = VideoCompressionLevel.Low, // Use low compression level to speed up tests
#endif
            }, true);

            // Validate it works in H.264 if we haven't already tested it (note: if shouldReencode is true, then we've already checked for H.264 earlier):
            if (!shouldReencode)
            {
                await Check(VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    VideoReencodeMode = StreamReencodeMode.Always,
                    ResultVideoCodecs = [VideoCodec.H264],
                    MaximumChromaSubsampling = subsampling,
                    MaximumBitsPerChannel = bpc,
#if CI
                    VideoCompressionLevel = VideoCompressionLevel.Low, // Use low compression level to speed up tests
#endif
                }, true);
            }
        });
    }

    [TestMethod]
    public async Task TestStreamLanguagePreservedWithMetadataStripping()
    {
        // Tests that stream language metadata is preserved even when general metadata stripping is enabled.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();

        // video20.mp4: unknown language.
        await using var stream20 = _videoFilesDir.CombineFile("video20.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId20 = null;

        await using var txn20 = await repo.BeginTransactionAsync();
        fileId20 = (await txn20.AddAsync(stream20, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn20.CommitAsync(TestContext.CancellationToken);

        var videoPath20 = await repo.GetAsync(fileId20!);
        videoPath20.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (this subtitle has an unknown language):
        var (outputModified20, errorModified20, returnCodeModified20) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath20.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified20.ShouldBe(0);
        outputModified20.Contains("\"language\": \"und\"", StringComparison.Ordinal).ShouldBeTrue();

        // video21.mp4: eng language.
        await using var stream21 = _videoFilesDir.CombineFile("video21.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId21 = null;

        await using var txn21 = await repo.BeginTransactionAsync();
        fileId21 = (await txn21.AddAsync(stream21, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn21.CommitAsync(TestContext.CancellationToken);

        var videoPath21 = await repo.GetAsync(fileId21!);
        videoPath21.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (the subtitle has "eng" language):
        var (outputModified21, errorModified21, returnCodeModified21) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath21.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified21.ShouldBe(0);
        outputModified21.Contains("\"language\": \"eng\"", StringComparison.Ordinal).ShouldBeTrue();

        // video163.mp4: eng video, fre audio.
        await using var stream163 = _videoFilesDir.CombineFile("video163.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId163 = null;

        await using var txn163 = await repo.BeginTransactionAsync();
        fileId163 = (await txn163.AddAsync(stream163, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn163.CommitAsync(TestContext.CancellationToken);

        var videoPath163 = await repo.GetAsync(fileId163!);
        videoPath163.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (the video stream has "eng" language, and the audio stream has "fre" language):
        var (outputModified163, errorModified163, returnCodeModified163) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath163.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified163.ShouldBe(0);
        int engIdx = outputModified163.IndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
        int freIdx = outputModified163.IndexOf("\"language\": \"fre\"", StringComparison.Ordinal);
        engIdx.ShouldBeGreaterThan(-1);
        freIdx.ShouldBeGreaterThan(-1);
        engIdx.ShouldBeLessThan(freIdx);
    }

    [TestMethod]
    public async Task TestRequiredMetadataStrippingMode()
    {
        // Tests that VideoMetadataStrippingMode.Required removes thumbnails, standard metadata, and custom metadata.
        // Uses video53.mp4 (has a thumbnail) and video162.mp4 (has artist & custom_metadata tags).

        using var repoCtx = GetRepo(out var repo);

        // Test if it successfully removes thumbnails from a video with thumbnails:
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.Required,
            },
            "video53.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping: []));

        // Test if can remove standard metadata & custom metadata from the file:

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video162.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId = null;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var (outputOriginal, errorOriginal, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        var (outputModified, errorModified, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified.ShouldBe(0);

        outputOriginal.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputOriginal.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();

        outputModified.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeFalse();
        outputModified.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    [TestMethod]
    public async Task TestMetadataPreservation()
    {
        // Tests that VideoMetadataStrippingMode.None preserves standard and custom metadata even when re-encoding.
        // Uses video162.mp4 with artist & custom_metadata tags.

        using var repoCtx = GetRepo(out var repo);

        // Test if can preserve standard metadata & custom metadata in the file:

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video162.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId = null;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var (outputOriginal, errorOriginal, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        var (outputModified, errorModified, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified.ShouldBe(0);

        outputOriginal.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputOriginal.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();

        outputModified.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputModified.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();
    }

    [TestMethod]
    public async Task TestStreamMetadataPreservation()
    {
        // Tests that stream-level metadata (language tags) is preserved when re-encoding with MetadataStrippingMode.None.
        // Uses video163.mp4 which has eng video stream and fre audio stream.

        using var repoCtx = GetRepo(out var repo);

        // Test if can preserve stream metadata in the file:
        // Note: the logic used to achieve this is different to TestStreamLanguagePreservedWithMetadataStripping, since the logic used with metadata stripping
        // on vs off is substantially different.

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream = _videoFilesDir.CombineFile("video163.mp4").OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId = null;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Check that the language metadata is preserved in the modified file (the video stream has "eng" language, and the audio stream has "fre" language):
        var (outputModified, errorModified, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified.ShouldBe(0);
        int engIdx = outputModified.IndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
        int freIdx = outputModified.IndexOf("\"language\": \"fre\"", StringComparison.Ordinal);
        engIdx.ShouldBeGreaterThan(-1);
        freIdx.ShouldBeGreaterThan(-1);
        engIdx.ShouldBeLessThan(freIdx);
    }

    [TestMethod]
    public async Task TestMetadataStrippingPreferredMode()
    {
        // Tests that VideoMetadataStrippingMode.Preferred preserves metadata when no re-encoding is needed, but strips it when re-encoding is forced.
        // Uses video162.mp4 with artist & custom_metadata tags.

        using var repoCtx = GetRepo(out var repo);

        // Test that it doesn't cause re-encoding:
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.Preferred,
            },
            "video162.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // Test if it strips the metadata when re-encoding is forced:

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Preferred,
            VideoReencodeMode = StreamReencodeMode.Always,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video162.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        FileId? fileId = null;

        await using var txn = await repo.BeginTransactionAsync();
        fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        var (outputOriginal, errorOriginal, returnCodeOriginal) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        var (outputModified, errorModified, returnCodeModified) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        returnCodeModified.ShouldBe(0);

        outputOriginal.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeTrue();
        outputOriginal.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeTrue();

        outputModified.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBeFalse();
        outputModified.Contains("\"custom_metadata\": \"Test Custom Metadata\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    [TestMethod]
    public async Task TestMetadataStrippingRequiredModeForcesRemuxing()
    {
        // Tests that VideoMetadataStrippingMode.Required forces remuxing even when no re-encoding is needed.

        using var repoCtx = GetRepo(out var repo);

        // Test that it remuxes the file even when no other changes are needed:
        // Note: it won't cause re-encoding for our purposes here, since we compare ignoring metadata.

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = VideoMetadataStrippingMode.Required,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));
    }

    // Helper to generate a video file that calculates the diff between the original and processed & exemplifies them.
    // Difference is converted to a heatmap for easy viewing, scale is ~5.3x (that is, a difference in brightness of ~19% = full white), and goes from black
    // (no difference) to white (maximum difference) through green and cyan.
    // File is outputted at 10fps for most videos (except for the ones for TestVideoCompressionLevel, which are 30fps) to save time.
    // Note also, we offset by ~1 frame for the TestVideoCompressionLevel tests, as they seem to be slightly out of sync otherwise for some reason.
    private async Task GenerateVideoDiffFile(
        IAbsoluteFilePath processedFile, IAbsoluteFilePath originalFile, int fps = 10, double duration = 10.0, double timeOffset = 0.0)
    {
        // Note: this method is disabled on CI as it can be quite slow and is for visual inspection only anyway.
#if !CI
        var diffFile = processedFile.ParentDirectory.CombineFile($"{processedFile.NameWithoutExtension}-diff.mp4");
        diffFile.Delete();

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-ss", timeOffset > 0 ? timeOffset.ToString(CultureInfo.InvariantCulture) : "0",
                "-i", originalFile.PathExport,
                "-ss", timeOffset < 0 ? (-timeOffset).ToString(CultureInfo.InvariantCulture) : "0",
                "-i", processedFile.PathExport,
                "-filter_complex", string.Create(CultureInfo.InvariantCulture,
                    $"[0:v]" +
                    $"fps=fps={fps}," +
                    $"format=pix_fmts=yuv444p:color_ranges=pc" +
                    $"[o0];" +
                    $"[1:v]" +
                    $"fps=fps={fps}," +
                    $"format=pix_fmts=yuv444p:color_ranges=pc" +
                    $"[o1];" +
                    $"[o0][o1]" +
                    $"blend=all_mode=difference," +
                    $"format=gray," +
                    $"geq='r=min(max(16*r(X,Y)-510,0),255):g=min(max(16*g(X,Y),0),255):b=min(max(16*b(X,Y)-255,0),255)'"),
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-y",
                diffFile.PathExport
            ],
            TestContext.CancellationToken);
#endif
    }

#if !CI
    [TestMethod]
    [DataRow(VideoCompressionLevel.Lowest)]
    [DataRow(VideoCompressionLevel.Low)]
    [DataRow(VideoCompressionLevel.Medium)]
    [DataRow(VideoCompressionLevel.High)]
    [DataRow(VideoCompressionLevel.Highest)]
    public async Task TestVideoCompressionLevelH264(VideoCompressionLevel level)
    {
        // Note: this test is disabled on CI as it can be quite slow.
        // Tests H.264 video compression at different levels. Outputs result files and diff heatmaps for visual comparison (they should all look similar).
        // Files should be sized approximately smaller at higher compression levels, but not guaranteed; however, they should certainly be more consistently
        // sized.

        var resultFile = _appDir.CombineDirectory("TestVideoCompressionLevelH264Results").CombineFile($"{level}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            VideoCompressionLevel = level,
            VideoQuality = VideoQuality.High,
            RemoveAudioStreams = true,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile, fps: 30, duration: 1.0, timeOffset: 0.017);
    }

    [TestMethod]
    [DataRow(VideoCompressionLevel.Lowest)]
    [DataRow(VideoCompressionLevel.Low)]
    [DataRow(VideoCompressionLevel.Medium)]
    [DataRow(VideoCompressionLevel.High)]
    [DataRow(VideoCompressionLevel.Highest)]
    public async Task TestVideoCompressionLevelHEVC(VideoCompressionLevel level)
    {
        // Note: this test is disabled on CI as it can be quite slow.
        // Tests HEVC video compression at different levels. Outputs result files and diff heatmaps for visual comparison (they should all look similar).
        // Files should be sized approximately smaller at higher compression levels, but not guaranteed; however, they should certainly be more consistently
        // sized.

        var resultFile = _appDir.CombineDirectory("TestVideoCompressionLevelHEVCResults").CombineFile($"{level}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.HEVC],
            VideoReencodeMode = StreamReencodeMode.Always,
            VideoCompressionLevel = level,
            VideoQuality = VideoQuality.High,
            RemoveAudioStreams = true,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile, fps: 30, duration: 1.0, timeOffset: 0.017);
    }
#endif

    [TestMethod]
    public async Task TestVideoQualityH264()
    {
        // Tests H.264 video quality at different levels. Outputs result files and diff heatmaps for visual comparison.
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        VideoQuality[] qualities = [VideoQuality.Lowest, VideoQuality.Low, VideoQuality.Medium, VideoQuality.High, VideoQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestVideoQualityImpl("H264", VideoCodec.H264, item.Quality, ct));

        // Verify file sizes increase with quality
        for (int i = 1; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    [TestMethod]
    public async Task TestVideoQualityHEVC()
    {
        // Tests HEVC video quality at different levels. Outputs result files and diff heatmaps for visual comparison.
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        VideoQuality[] qualities = [VideoQuality.Lowest, VideoQuality.Low, VideoQuality.Medium, VideoQuality.High, VideoQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestVideoQualityImpl("HEVC", VideoCodec.HEVC, item.Quality, ct));

        // Verify file sizes increase with quality
        for (int i = 1; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    private async Task<long> TestVideoQualityImpl(string resultFolderName, VideoCodec codec, VideoQuality quality, CancellationToken cancellationToken)
    {
        // Helper to encode a clip at a given quality/codec, emit diff output, and return size.
        var resultFile = _appDir.CombineDirectory($"TestVideoQuality{resultFolderName}Results").CombineFile($"{quality}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [codec],
            VideoReencodeMode = StreamReencodeMode.Always,
            VideoQuality = quality,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, cancellationToken)).FileId;
        await txn.CommitAsync(cancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateVideoDiffFile(resultFile, origFile);

        return resultFile.Length;
    }

    // Helper to generate an audio spectrogram set for processed vs original files. Produces a 3840x2160 spectrogram of the processed audio.
    // A difference image is produced for easy viewing of changes, which is converted to a heatmap for easy viewing, scale is ~5.3x (that is, a difference in
    // brightness of ~19% = full white), and goes from black (no difference) to white (maximum difference) through green and cyan.
    // Duration controls how much of the clip is visualized.
    // The spectrogram diff does not tell the full story of audio quality, but is a useful quick visual reference for comparison within a specific encoder.
    private async Task GenerateAudioSpectrogramFile(IAbsoluteFilePath processedFile, IAbsoluteFilePath originalFile, double duration = 24.0)
    {
        // Note: this method is disabled on CI as it can be quite slow and is for visual inspection only anyway.
#if !CI
        var spectrogramFile = processedFile.ParentDirectory.CombineFile($"{processedFile.NameWithoutExtension}-spectrogram.png");
        spectrogramFile.Delete();

        var spectrogramDiffFile = processedFile.ParentDirectory.CombineFile($"{processedFile.NameWithoutExtension}-spectrogram-diff.png");
        spectrogramDiffFile.Delete();

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-i", processedFile.PathExport,
                "-filter_complex", "[0:a]showspectrumpic=s=3840x2160",
                "-y",
                spectrogramFile.PathExport
            ],
            TestContext.CancellationToken);

        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", spectrogramFile.PathExport,
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-i", originalFile.PathExport,
                "-filter_complex",
                    "[0:v]" +
                    "format=pix_fmts=rgb24:color_ranges=pc" +
                    "[0i];" +
                    "[1:a]" +
                    "showspectrumpic=s=3840x2160," +
                    "format=pix_fmts=rgb24:color_ranges=pc" +
                    "[1i];" +
                    "[0i][1i]" +
                    "blend=all_mode=difference," +
                    "format=gray," +
                    "geq='r=min(max(16*r(X,Y)-510,0),255):g=min(max(16*g(X,Y),0),255):b=min(max(16*b(X,Y)-255,0),255)'",
                "-y",
                spectrogramDiffFile.PathExport
            ],
            TestContext.CancellationToken);
#endif
    }

    [TestMethod]
    public async Task TestAudioQualityLibFDKAAC()
    {
        // Tests AAC audio quality at different levels using libfdk_aac encoder. Outputs spectrograms for visual comparison (not the most accurate method, but
        // useful for a quick comparison within a specific encoder).
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        AudioQuality[] qualities = [AudioQuality.Lowest, AudioQuality.Low, AudioQuality.Medium, AudioQuality.High, AudioQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestAudioQualityLibFDKAACImpl(item.Quality, ct));

        // Verify file sizes increase with quality (note: Lowest happens to be larger than Low due to random chance, so we skip that check)
        for (int i = 2; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    private async Task<long> TestAudioQualityLibFDKAACImpl(AudioQuality quality, CancellationToken cancellationToken)
    {
        // Helper to encode audio with libfdk_aac at a given quality and generate spectrograms.
        var resultFile = _appDir.CombineDirectory("TestAudioQualityLibFDKAACResults").CombineFile($"{quality}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
#if DEBUG
            ForceLibFDKAACUsage = true,
#endif
            AudioReencodeMode = StreamReencodeMode.Always,
            AudioQuality = quality,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, cancellationToken)).FileId;
        await txn.CommitAsync(cancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateAudioSpectrogramFile(resultFile, origFile);

        return resultFile.Length;
    }

#if DEBUG
    [TestMethod]
    public async Task TestAudioQualityAAC()
    {
        // Tests AAC audio quality at different levels using default aac encoder. Outputs spectrograms for visual comparison (not the most accurate method, but
        // useful for a quick comparison within a specific encoder).
        // Runs all quality levels in parallel, then verifies file sizes increase with quality.

        AudioQuality[] qualities = [AudioQuality.Lowest, AudioQuality.Low, AudioQuality.Medium, AudioQuality.High, AudioQuality.Highest];
        long[] results = new long[qualities.Length];

        await Parallel.ForEachAsync(
            qualities.Select((q, i) => (Quality: q, Index: i)),
            TestContext.CancellationToken,
            async (item, ct) => results[item.Index] = await TestAudioQualityAACImpl(item.Quality, ct));

        // Verify file sizes increase with quality
        for (int i = 1; i < results.Length; i++)
        {
            results[i].ShouldBeGreaterThan(
                results[i - 1],
                $"Expected {qualities[i]} ({results[i]} bytes) to be larger than {qualities[i - 1]} ({results[i - 1]} bytes)");
        }
    }

    private async Task<long> TestAudioQualityAACImpl(AudioQuality quality, CancellationToken cancellationToken)
    {
        // Helper to encode audio with native AAC at a given quality and generate spectrograms.
        var resultFile = _appDir.CombineDirectory("TestAudioQualityAACResults").CombineFile($"{quality}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ForceLibFDKAACUsage = false,
            AudioReencodeMode = StreamReencodeMode.Always,
            AudioQuality = quality,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, cancellationToken)).FileId;
        await txn.CommitAsync(cancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        await GenerateAudioSpectrogramFile(resultFile, origFile);

        return resultFile.Length;
    }
#endif

#if !CI
    [TestMethod]
    [DataRow(1920, 1080)]
    [DataRow(1280, 720)]
    [DataRow(1024, 576)]
    [DataRow(960, 540)]
    [DataRow(640, 360)]
    [DataRow(320, 180)]
    public async Task TestVideoResizeH264(int width, int height)
    {
        // Note: this test is disabled on CI as it's for visual inspection primarily (video resizing is also tested elsewhere).
        // Tests H.264 video resizing at various target dimensions. Outputs resized result files.

        var resultFile = _appDir.CombineDirectory("TestVideoResizeH264Results").CombineFile(
            string.Create(CultureInfo.InvariantCulture, $"{width}x{height}.mp4"));
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, width, height),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);
    }

    [TestMethod]
    [DataRow(1920, 1080)]
    [DataRow(1280, 720)]
    [DataRow(1024, 576)]
    [DataRow(960, 540)]
    [DataRow(640, 360)]
    [DataRow(320, 180)]
    public async Task TestVideoResizeHEVC(int width, int height)
    {
        // Note: this test is disabled on CI as it's for visual inspection primarily (video resizing is also tested elsewhere).
        // Tests HEVC video resizing at various target dimensions. Outputs resized result files.

        var resultFile = _appDir.CombineDirectory("TestVideoResizeHEVCResults").CombineFile(
            string.Create(CultureInfo.InvariantCulture, $"{width}x{height}.mp4"));
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.HEVC],
            VideoReencodeMode = StreamReencodeMode.Always,
            ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, width, height),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);
    }
#endif

    public static IEnumerable<object?[]> TestSquarePixelHandlingData => field ??=
    [
        ["video1.mp4", true, null, (1, 1), (1, 1), (128, 72), (128, 72)],
        ["video1.mp4", false, null, (1, 1), (1, 1), (128, 72), (128, 72)],
        ["video1.mp4", true, (100, 100), (1, 1), (1, 1), (128, 72), (100, 56)],
        ["video1.mp4", false, (100, 100), (1, 1), (224, 225), (128, 72), (100, 56)],
        ["video1.mp4", true, (10, 10), (1, 1), (1, 1), (128, 72), (10, 6)],
        ["video1.mp4", false, (10, 10), (1, 1), (16, 15), (128, 72), (10, 6)],
        ["video111.mp4", true, null, (4, 3), (1, 1), (128, 128), (170, 128)],
        ["video111.mp4", false, null, (4, 3), (4, 3), (128, 128), (128, 128)],
        ["video111.mp4", true, (100, 100), (4, 3), (1, 1), (128, 128), (100, 76)],
        ["video111.mp4", false, (100, 100), (4, 3), (4, 3), (128, 128), (100, 100)],
        ["video111.mp4", true, (10, 10), (4, 3), (1, 1), (128, 128), (10, 8)],
        ["video111.mp4", false, (10, 10), (4, 3), (4, 3), (128, 128), (10, 10)],
        ["video166.mp4", true, null, (4, 3), (1, 1), (96, 128), (128, 128)],
        ["video166.mp4", false, null, (4, 3), (4, 3), (96, 128), (96, 128)],
        ["video166.mp4", true, (100, 100), (4, 3), (1, 1), (96, 128), (100, 100)],
        ["video166.mp4", false, (100, 100), (4, 3), (25, 19), (96, 128), (76, 100)],
        ["video166.mp4", true, (10, 10), (4, 3), (1, 1), (96, 128), (10, 10)],
        ["video166.mp4", false, (10, 10), (4, 3), (5, 4), (96, 128), (8, 10)],
    ];

    [TestMethod]
    [DynamicData(nameof(TestSquarePixelHandlingData))]
    public async Task TestSquarePixelHandling(
        string fileName,
        bool forceSquarePixels,
        (int W, int H)? maxSize,
        (int W, int H) inputSar,
        (int W, int H) outputSar,
        (int W, int H) inputSize,
        (int W, int H) outputSize)
    {
        // Tests handling of non-square pixel aspect ratios (SAR) and ForceSquarePixels option.
        // Verifies correct dimension/SAR transformations for various input files and resize settings.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceSquarePixels = forceSquarePixels,
            ResizeOptions = maxSize is not null ? new VideoResizeOptions(VideoResizeMode.FitDown, maxSize.Value.W, maxSize.Value.H) : null,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Validate our expectations:

        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {inputSize.W}"), StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {inputSize.H}"), StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"sample_aspect_ratio\": \"{inputSar.W}:{inputSar.H}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with original info: " + originalInfo, ex);
        }

        try
        {
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {outputSize.W}"), StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {outputSize.H}"), StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"sample_aspect_ratio\": \"{outputSar.W}:{outputSar.H}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with processed info: " + processedInfo, ex);
        }
    }

    public static IEnumerable<(bool H264, bool HEVC, object?[] Value)> TestResizeHandlingData => field ??=
    [
        (H264: true, HEVC: true, ["video1.mp4", null, null, (128, 72), (128, 72)]),
        (H264: true, HEVC: true, ["video1.mp4", null, (100, 100), (128, 72), (100, 56)]),
        (H264: true, HEVC: true, ["video1.mp4", null, (28, 28), (128, 72), (28, 16)]),
        (H264: true, HEVC: false, ["video1.mp4", null, (10, 10), (128, 72), (10, 6)]),
        (H264: false, HEVC: true, ["video1.mp4", "Cannot re-encode video to fit within specified dimensions.", (10, 10), (128, 72), null]),
        (H264: true, HEVC: true, ["video1.mp4", null, (50, 1000), (128, 72), (50, 28)]),
        (H264: true, HEVC: true, ["video1.mp4", null, (1000, 50), (128, 72), (88, 50)]),
        (H264: true, HEVC: true, ["video111.mp4", null, null, (128, 128), (128, 128)]),
        (H264: true, HEVC: true, ["video111.mp4", null, (100, 100), (128, 128), (100, 100)]),
        (H264: true, HEVC: true, ["video111.mp4", null, (16, 16), (128, 128), (16, 16)]),
        (H264: true, HEVC: false, ["video111.mp4", null, (10, 10), (128, 128), (10, 10)]),
        (H264: false, HEVC: true, ["video111.mp4", "Cannot re-encode video to fit within specified dimensions.", (10, 10), (128, 128), null]),
        (H264: true, HEVC: true, ["video111.mp4", "Cannot re-encode video to fit within specified dimensions.", (1, 1), (128, 128), null]),
        (H264: true, HEVC: true, ["video111.mp4", null, (50, 1000), (128, 128), (50, 50)]),
        (H264: true, HEVC: true, ["video111.mp4", null, (1000, 50), (128, 128), (50, 50)]),
        (H264: true, HEVC: true, ["video166.mp4", null, null, (96, 128), (96, 128)]),
        (H264: true, HEVC: true, ["video166.mp4", null, (100, 100), (96, 128), (76, 100)]),
        (H264: true, HEVC: true, ["video166.mp4", null, (22, 22), (96, 128), (16, 22)]),
        (H264: true, HEVC: false, ["video166.mp4", null, (10, 10), (96, 128), (8, 10)]),
        (H264: false, HEVC: true, ["video166.mp4", "Cannot re-encode video to fit within specified dimensions.", (10, 10), (96, 128), null]),
        (H264: true, HEVC: true, ["video166.mp4", null, (50, 1000), (96, 128), (50, 66)]),
        (H264: true, HEVC: true, ["video166.mp4", null, (1000, 50), (96, 128), (38, 50)]),
        (H264: true, HEVC: false, ["video143.mp4", null, null, (64, 65534), (16, 16384)]),
        (H264: false, HEVC: true, ["video143.mp4", null, null, (64, 65534), (64, 65534)]),
        (H264: true, HEVC: false, ["video146.mp4", null, null, (65535, 64), (16384, 16)]),
        (H264: false, HEVC: true, ["video146.mp4", null, null, (65535, 64), (65535, 64)]),
        (H264: true, HEVC: false, ["video136.mp4", null, null, (2, 16384), (2, 16384)]),
        (H264: false, HEVC: true, ["video136.mp4", "Cannot re-encode video to fit within specified dimensions.", null, (2, 16384), null]),
        (H264: false, HEVC: true, ["video185.mkv", null, null, (64, 65536), (64, 65534)]),
        (H264: true, HEVC: false, ["video185.mkv", null, null, (64, 65536), (16, 16384)]),
    ];

    public static IEnumerable<object?[]> TestH264ResizeHandlingData => field ??= TestResizeHandlingData.Where((x) => x.H264).Select((x) => x.Value);
    public static IEnumerable<object?[]> TestHEVCResizeHandlingData => field ??= TestResizeHandlingData.Where((x) => x.HEVC).Select((x) => x.Value);

    [TestMethod]
    [DynamicData(nameof(TestH264ResizeHandlingData))]
    public async Task TestResizeHandlingH264(
        string fileName, string? expectedError, (int W, int H)? maxSize, (int W, int H) inputSize, (int W, int H)? outputSize)
    {
        await TestResizeHandlingImpl(VideoCodec.H264, fileName, expectedError, maxSize, inputSize, outputSize);
    }

    [TestMethod]
    [DynamicData(nameof(TestHEVCResizeHandlingData))]
    public async Task TestResizeHandlingHEVC(
        string fileName, string? expectedError, (int W, int H)? maxSize, (int W, int H) inputSize, (int W, int H)? outputSize)
    {
        await TestResizeHandlingImpl(VideoCodec.HEVC, fileName, expectedError, maxSize, inputSize, outputSize);
    }

    private async Task TestResizeHandlingImpl(
        VideoCodec resultCodec, string fileName, string? expectedError, (int W, int H)? maxSize, (int W, int H) inputSize, (int W, int H)? outputSize)
    {
        // Tests H.264 / HEVC video resizing and validation of minimum dimension requirements for various input sizes.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            ResultVideoCodecs = [resultCodec],
            ResizeOptions = maxSize is not null ? new VideoResizeOptions(VideoResizeMode.FitDown, maxSize.Value.W, maxSize.Value.H) : null,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        IAbsoluteFilePath videoPath;

        if (expectedError is not null)
        {
            var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
            {
                await using var txn = await repo.BeginTransactionAsync();
                await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
                await txn.CommitAsync(TestContext.CancellationToken);
            });

            ex.Message.ShouldBe(expectedError);
            videoPath = null;
        }
        else
        {
            await using var txn = await repo.BeginTransactionAsync();
            var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);
            videoPath = await repo.GetAsync(fileId);
            videoPath.Exists.ShouldBeTrue();
        }

        // Validate our expectations:

        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {inputSize.W}"), StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {inputSize.H}"), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with original info: " + originalInfo, ex);
        }

        if (videoPath is null)
            return;

        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"width\": {outputSize!.Value.W}"), StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture, $"\"height\": {outputSize.Value.H}"), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed with processed info: " + processedInfo, ex);
        }
    }

    public static IEnumerable<object?[]> TestFpsLimitHandlingData => field ??=
    [
        ["video1.mp4", true, VideoFpsMode.LimitToExact, 24, (30, 1), (24, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitToExact, 15, (30, 1), (15, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitToExact, 10, (30, 1), (10, 1)],
        ["video1.mp4", false, VideoFpsMode.LimitToExact, 30, (30, 1), null],
        ["video1.mp4", false, VideoFpsMode.LimitToExact, 60, (30, 1), null],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 24, (30, 1), (15, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 15, (30, 1), (15, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 14, (30, 1), (10, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 10, (30, 1), (10, 1)],
        ["video1.mp4", true, VideoFpsMode.LimitByIntegerDivision, 9, (30, 1), (15, 2)],
        ["video1.mp4", false, VideoFpsMode.LimitByIntegerDivision, 30, (30, 1), null],
        ["video1.mp4", false, VideoFpsMode.LimitByIntegerDivision, 60, (30, 1), null],
        ["video95.mp4", true, VideoFpsMode.LimitToExact, 30, (48000, 1001), (30, 1)],
        ["video95.mp4", true, VideoFpsMode.LimitToExact, 24, (48000, 1001), (24, 1)],
        ["video95.mp4", false, VideoFpsMode.LimitToExact, 48, (48000, 1001), null],
        ["video95.mp4", false, VideoFpsMode.LimitToExact, 60, (48000, 1001), null],
        ["video95.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (48000, 1001), (24000, 1001)],
        ["video95.mp4", true, VideoFpsMode.LimitByIntegerDivision, 24, (48000, 1001), (24000, 1001)],
        ["video95.mp4", true, VideoFpsMode.LimitByIntegerDivision, 23, (48000, 1001), (16000, 1001)],
        ["video95.mp4", false, VideoFpsMode.LimitByIntegerDivision, 48, (48000, 1001), null],
        ["video95.mp4", false, VideoFpsMode.LimitByIntegerDivision, 60, (48000, 1001), null],
        ["video100.mp4", true, VideoFpsMode.LimitToExact, 60, (300, 1), (60, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitToExact, 30, (300, 1), (30, 1)],
        ["video100.mp4", false, VideoFpsMode.LimitToExact, 300, (300, 1), null],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 60, (300, 1), (60, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 59, (300, 1), (50, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (300, 1), (30, 1)],
        ["video100.mp4", true, VideoFpsMode.LimitByIntegerDivision, 29, (300, 1), (300, 11)],
        ["video100.mp4", false, VideoFpsMode.LimitByIntegerDivision, 300, (300, 1), null],
        ["video101.mp4", true, VideoFpsMode.LimitToExact, 60, (299999, 1000), (60, 1)],
        ["video101.mp4", true, VideoFpsMode.LimitToExact, 30, (299999, 1000), (30, 1)],
        ["video101.mp4", false, VideoFpsMode.LimitToExact, 300, (299999, 1000), null],
        ["video101.mp4", true, VideoFpsMode.LimitByIntegerDivision, 60, (299999, 1000), (299999, 5000)],
        ["video101.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (299999, 1000), (299999, 10000)],
        ["video101.mp4", false, VideoFpsMode.LimitByIntegerDivision, 300, (299999, 1000), null],
        ["video102.mp4", true, VideoFpsMode.LimitToExact, 60, (1000573, 4001), (60, 1)],
        ["video102.mp4", true, VideoFpsMode.LimitToExact, 30, (1000573, 4001), (30, 1)],
        ["video102.mp4", false, VideoFpsMode.LimitToExact, 251, (1000573, 4001), null],
        ["video102.mp4", true, VideoFpsMode.LimitByIntegerDivision, 60, (1000573, 4001), (1000573, 20005)],
        ["video102.mp4", true, VideoFpsMode.LimitByIntegerDivision, 30, (1000573, 4001), (1000573, 36009)],
        ["video102.mp4", false, VideoFpsMode.LimitByIntegerDivision, 251, (1000573, 4001), null],
        ["video103.mp4", false, VideoFpsMode.LimitByIntegerDivision, 2, (1001000, 1000999), null],
        ["video103.mp4", true, VideoFpsMode.LimitByIntegerDivision, 1, (1001000, 1000999), (500500, 1000999)],
        ["video167.mp4", false, VideoFpsMode.LimitByIntegerDivision, 2, (1000999, 1000998), null],
        ["video167.mp4", true, VideoFpsMode.LimitByIntegerDivision, 1, (1000999, 1000998), (500500, 1000999)], // Note: ffmpeg rounds values above 1001000
    ];

    [TestMethod]
    [DynamicData(nameof(TestFpsLimitHandlingData))]
    public async Task TestFpsLimitHandling(
        string fileName, bool shouldReencode, VideoFpsMode mode, int targetFps, (int Num, int Den) inputFps, (int Num, int Den)? outputFps)
    {
        // Tests FPS limiting with both LimitToExact and LimitByIntegerDivision modes.
        // Verifies correct frame rate reduction for various input FPS values and target limits.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            FpsOptions = new VideoFpsOptions(mode, targetFps),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);
        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        if (!shouldReencode)
        {
            stream.Position = 0;
            await using var stream2 = videoPath.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
            (await AreStreamsEqual(stream, stream2, TestContext.CancellationToken)).ShouldBeTrue();
            return;
        }

        // Validate input FPS expectation:
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"r_frame_rate\": \"{inputFps.Num}/{inputFps.Den}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to validate input FPS with original info: " + originalInfo, ex);
        }

        // Validate output FPS expectation:
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        try
        {
            processedInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"r_frame_rate\": \"{outputFps!.Value.Num}/{outputFps.Value.Den}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception(
                $"Failed to validate output FPS (expected {outputFps!.Value.Num}/{outputFps.Value.Den}) with processed info: " + processedInfo,
                ex);
        }
    }

    [TestMethod]
    [DataRow("video16", ".mkv", false)]
    [DataRow("video17", ".mkv", false)]
    [DataRow("video18", ".mkv", false)]
    [DataRow("video19", ".mkv", false)]
    [DataRow("video20", ".mp4", false)]
    [DataRow("video169", ".mkv", true)]
    [DataRow("video170", ".mkv", true)]
    public async Task TestSubtitleReencode(string fileName, string extension, bool makeMkvCopy)
    {
        // All of these files should end up with subtitles that are playable in VLC after re-encoding (note: you may have to try playing the video more than
        // once, due to VLC struggling with short subtitles near the start).
        // Note: the subtitles for 169 won't look right when playing as mp4 necessarily, as support for dvd_subtitles in mp4 in some players is poor, but if
        // remuxed back to mkv, it should look correct (which this test does for you); file 170 is the same, but it doesn't look entirely correct even after.

        var resultFile = _appDir.CombineDirectory("TestSubtitleReencodeResults").CombineFile(string.Create(CultureInfo.InvariantCulture, $"{fileName}.mp4"));
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName + extension);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);

        // If extension was .mkv, also make a copy remuxed back to mkv to validate in case of weird mp4 dvd_subtitle playback issues:
        if (makeMkvCopy)
        {
            var resultFileMkv = _appDir.CombineDirectory("TestSubtitleReencodeResults").CombineFile(
                string.Create(CultureInfo.InvariantCulture, $"{fileName}.mkv"));
            resultFileMkv.Delete();
            await RunFFtoolProcessWithErrorHandling(
                "ffmpeg",
                ["-i", videoPath.PathExport, "-c", "copy", "-y", resultFileMkv.PathExport],
                TestContext.CancellationToken);
        }

        // Validate we have 3 streams still:
        (await GetStreamCount(videoPath.PathExport, TestContext.CancellationToken)).ShouldBe(3);
    }

    [TestMethod]
    [DataRow("tff")]
    [DataRow("bff")]
    public async Task TestDeinterlacing(string interlaceMode)
    {
        // This test creates an interlaced version of the big buck bunny file using ffmpeg's interlace filter,
        // then processes it through the library with ForceProgressiveFrames = true to validate de-interlacing works,
        // and copies both the interlaced and de-interlaced versions to a folder for manual inspection.

        var resultsDir = _appDir.CombineDirectory("TestDeinterlacingResults");
        resultsDir.Create();

        var interlacedFile = resultsDir.CombineFile($"interlaced_{interlaceMode}.mp4");
        var deinterlacedFile = resultsDir.CombineFile($"deinterlaced_{interlaceMode}.mp4");
        interlacedFile.Delete();
        deinterlacedFile.Delete();

        var origFile = _videoFilesDir.CombineFile(BigBuckBunnyFullVideoFileName);

        // Create an interlaced version of the original file using ffmpeg's interlace filter:
        // The interlace filter converts progressive video to interlaced, with tff (top field first) or bff (bottom field first).
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", $"interlace=scan={interlaceMode}:lowpass=complex",
                "-c:v", "libx264",
                "-x264-params", $"{interlaceMode}=1",
                "-c:a", "copy",
                "-y", interlacedFile.PathExport,
            ],
            TestContext.CancellationToken);

        // Verify the interlaced file has interlaced field order:
        var (interlacedProbeOutput, _, interlacedProbeReturnCode) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", interlacedFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        interlacedProbeReturnCode.ShouldBe(0);

        // The interlaced file should have field_order set to "tt" (top first) or "bb" (bottom first):
        string expectedFieldOrder = interlaceMode == "tff" ? "\"field_order\": \"tt\"" : "\"field_order\": \"bb\"";
        interlacedProbeOutput.Contains(expectedFieldOrder, StringComparison.Ordinal).ShouldBeTrue();

        // Process the interlaced file through the library with ForceProgressiveFrames = true:
        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            ResultVideoCodecs = [VideoCodec.H264],
            VideoReencodeMode = StreamReencodeMode.Always,
            ForceProgressiveFrames = true,
        }).ToPipeline();

        await using var stream = interlacedFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Copy the de-interlaced file to the results directory:
        File.Copy(videoPath.PathExport, deinterlacedFile.PathExport);

        // Verify the de-interlaced file has progressive field order:
        var (deinterlacedProbeOutput, _, deinterlacedProbeReturnCode) = await RunFFtoolProcess(
            "ffprobe",
            ["-i", deinterlacedFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);
        deinterlacedProbeReturnCode.ShouldBe(0);

        // The de-interlaced file should have field_order set to "progressive":
        deinterlacedProbeOutput.Contains(
            "\"field_order\": \"progressive\"", StringComparison.Ordinal).ShouldBeTrue("Expected de-interlaced file to be progressive");
    }

    [TestMethod]
    [DataRow("video1.mp4")]
    [DataRow("video9.avi")]
    [DataRow("video15.mp4")]
    [DataRow("video161.mp4")]
    public async Task TestProgressiveFileNotUnnecessarilyDeinterlaced(string fileName)
    {
        // This test verifies that progressive files are not unnecessarily processed when ForceProgressiveFrames is enabled.
        // The file should remain identical (not re-encoded or modified).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ForceProgressiveFrames = true,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestSquarePixelsFileNotUnnecessarilyReencoded()
    {
        // This test verifies that files with already square pixels are not unnecessarily processed when ForceSquarePixels is enabled.
        // video1.mp4 already has 1:1 SAR (square pixels), so it should remain identical.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ForceSquarePixels = true,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRemapHDRToSDRNotUnnecessarilyReencoded()
    {
        // This test verifies that SDR files are not unnecessarily processed when RemapHDRToSDR is enabled.
        // video1.mp4 is SDR, so it should remain identical.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                RemapHDRToSDR = true,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestResizeOptionsNotUnnecessarilyReencoded()
    {
        // This test verifies that files that already fit within the resize bounds are not unnecessarily processed.
        // video1.mp4 is 128x72, so specifying a larger max size should not cause re-encoding.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1920, 1080),
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestMaxChannelsNotUnnecessarilyReencoded()
    {
        // This test verifies that audio files that already have fewer or equal channels are not unnecessarily processed.
        // video1.mp4 has stereo audio, so specifying Stereo or higher max should not cause re-encoding.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaxChannels = AudioChannels.Stereo,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestMaxSampleRateNotUnnecessarilyReencoded()
    {
        // This test verifies that audio files that already have a lower or equal sample rate are not unnecessarily processed.
        // video1.mp4 has 44.1kHz audio, so specifying 44.1kHz or higher max should not cause re-encoding.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MaxSampleRate = AudioSampleRate.Hz44100,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRandomBytesAsVideoThrows()
    {
        // This test generates random bytes with a specific seed, which forms an invalid an mp4,
        // and verifies that a FileProcessingException is thrown because the file is invalid.

        using var repoCtx = GetRepo(out var repo);

        var random = new Random(42); // Use a fixed seed for reproducibility
        byte[] randomBytes = new byte[1024];
        random.NextBytes(randomBytes);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve).ToPipeline();

        await using var stream = new MemoryStream(randomBytes);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
        {
            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, ".mp4", true, pipeline, TestContext.CancellationToken);
        });

        // The exception message should indicate the file is invalid/unrecognized
        ex.Message.ShouldNotBeNullOrEmpty();
    }

    // video1.mp4 is stereo (2 channels), video57.mp4 is mono (1 channel), video60.mp4 is 5.1 surround sound (6 channels)
    [TestMethod]
    [DataRow("video1.mp4", AudioChannels.Mono, true)]
    [DataRow("video1.mp4", AudioChannels.Stereo, false)]
    [DataRow("video57.mp4", AudioChannels.Mono, false)]
    [DataRow("video57.mp4", AudioChannels.Stereo, false)]
    [DataRow("video60.mp4", AudioChannels.Mono, true)]
    [DataRow("video60.mp4", AudioChannels.Stereo, true)]
    public async Task TestMaxChannelsRespected(string fileName, AudioChannels maxChannels, bool shouldReencode)
    {
        // Tests MaxChannels enforcement: audio exceeding the limit is re-encoded/downmixed, compliant files remain unchanged.
        // Covers mono, stereo, and 5.1 surround source files against mono and stereo limits.

        using var repoCtx = GetRepo(out var repo);

        if (!shouldReencode)
        {
            // Should not re-encode - file should remain identical.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxChannels = maxChannels,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: null);
        }
        else
        {
            // Should re-encode audio only.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxChannels = maxChannels,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: 2, StreamMapping:
                [
                    (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream unchanged
                    (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream re-encoded
                ]), afterFinishedAction: async (newPath) =>
                {
                    // Validate that the result now meets the max channels constraint:
                    await CheckProcessing(
                        repo,
                        VideoProcessingOptions.Preserve with
                        {
                            ForceValidateAllStreams = DefaultForceValidateAllStreams,
                            MaxChannels = maxChannels,
                        },
                        newPath,
                        exceptionMessage: null,
                        expectedChanges: null,
                        pathIsAbsolute: true);
                });
        }
    }

#if !CI
    [TestMethod]
    [DataRow(BigBuckBunnyFullVideoFileName, AudioChannels.Stereo, AudioChannels.Mono)]
    public async Task TestAudioChannelDownmixQuality(string fileName, AudioChannels inputChannels, AudioChannels maxChannels)
    {
        // Note: this test is excluded from CI runs since it is intended for manual inspection only (audio channel downmixing is also tested elsewhere).
        // Tests audio channel downmixing quality for manual inspection. Outputs downmixed result files.

        var resultFile = _appDir.CombineDirectory("TestAudioChannelDownmixQualityResults").CombineFile($"{inputChannels}To{maxChannels}.mp4");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MaxChannels = maxChannels,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId!);
        videoPath.Exists.ShouldBeTrue();

        File.Copy(videoPath.PathExport, resultFile.PathExport);
    }
#endif

    [TestMethod]
    [DataRow("video1.mp4", AudioSampleRate.Preserve, false, false, 44100, 44100)]
    [DataRow("video67.mp4", AudioSampleRate.Preserve, false, false, 8000, 8000)]
    [DataRow("video68.mp4", AudioSampleRate.Preserve, false, false, 11025, 11025)]
    [DataRow("video69.mp4", AudioSampleRate.Preserve, false, false, 12000, 12000)]
    [DataRow("video70.mp4", AudioSampleRate.Preserve, false, false, 16000, 16000)]
    [DataRow("video71.mp4", AudioSampleRate.Preserve, false, false, 22050, 22050)]
    [DataRow("video72.mp4", AudioSampleRate.Preserve, false, false, 24000, 24000)]
    [DataRow("video73.mp4", AudioSampleRate.Preserve, false, false, 32000, 32000)]
    [DataRow("video74.mp4", AudioSampleRate.Preserve, false, false, 48000, 48000)]
    [DataRow("video75.mp4", AudioSampleRate.Preserve, false, false, 64000, 64000)]
    [DataRow("video76.mp4", AudioSampleRate.Preserve, false, false, 88200, 88200)]
    [DataRow("video77.mp4", AudioSampleRate.Preserve, false, false, 96000, 96000)]
    [DataRow("video78.mp4", AudioSampleRate.Preserve, false, false, 192000, 192000)]
    [DataRow("video79.mp4", AudioSampleRate.Preserve, false, false, 200000, 200000)]
    [DataRow("video80.mp4", AudioSampleRate.Preserve, false, false, 4000, 4000)]
    [DataRow("video81.mp4", AudioSampleRate.Preserve, false, false, 22, 22)]
    [DataRow("video82.mp4", AudioSampleRate.Preserve, false, false, 45000, 45000)]
    [DataRow("video1.mp4", AudioSampleRate.Preserve, true, true, 44100, 44100)]
    [DataRow("video67.mp4", AudioSampleRate.Preserve, true, true, 8000, 8000)]
    [DataRow("video68.mp4", AudioSampleRate.Preserve, true, true, 11025, 11025)]
    [DataRow("video69.mp4", AudioSampleRate.Preserve, true, true, 12000, 12000)]
    [DataRow("video70.mp4", AudioSampleRate.Preserve, true, true, 16000, 16000)]
    [DataRow("video71.mp4", AudioSampleRate.Preserve, true, true, 22050, 22050)]
    [DataRow("video72.mp4", AudioSampleRate.Preserve, true, true, 24000, 24000)]
    [DataRow("video73.mp4", AudioSampleRate.Preserve, true, true, 32000, 32000)]
    [DataRow("video74.mp4", AudioSampleRate.Preserve, true, true, 48000, 48000)]
    [DataRow("video75.mp4", AudioSampleRate.Preserve, true, true, 64000, 64000)]
    [DataRow("video76.mp4", AudioSampleRate.Preserve, true, true, 88200, 88200)]
    [DataRow("video77.mp4", AudioSampleRate.Preserve, true, true, 96000, 96000)]
    [DataRow("video78.mp4", AudioSampleRate.Preserve, true, true, 192000, 96000)]
    [DataRow("video80.mp4", AudioSampleRate.Preserve, true, true, 4000, 8000)]
    [DataRow("video82.mp4", AudioSampleRate.Preserve, true, true, 45000, 48000)]
    [DataRow("video77.mp4", AudioSampleRate.Hz48000, true, false, 96000, 48000)]
    [DataRow("video74.mp4", AudioSampleRate.Hz48000, false, false, 48000, 48000)]
    [DataRow("video1.mp4", AudioSampleRate.Hz48000, false, false, 44100, 44100)]
    [DataRow("video82.mp4", AudioSampleRate.Hz48000, false, false, 45000, 45000)]
    [DataRow("video82.mp4", AudioSampleRate.Hz48000, true, true, 45000, 48000)]
    [DataRow("video1.mp4", AudioSampleRate.Hz44100, false, false, 44100, 44100)]
    [DataRow("video74.mp4", AudioSampleRate.Hz44100, true, false, 48000, 44100)]
    [DataRow("video82.mp4", AudioSampleRate.Hz44100, true, false, 45000, 44100)]
    public async Task TestMaxSampleRateRespected(
        string fileName, AudioSampleRate maxSampleRate, bool shouldReencode, bool forceReencode, int inputSampleRate, int expectedOutputSampleRate)
    {
        // Tests MaxSampleRate enforcement and sample rate resampling. Validates that audio sample rates exceeding the limit
        // are resampled, unusual rates are converted to valid AAC rates, and compliant files remain unchanged.

        using var repoCtx = GetRepo(out var repo);

        var origFile = _videoFilesDir.CombineFile(fileName);

        // Validate the input file has the expected sample rate:
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                $"\"sample_rate\": \"{inputSampleRate}\""), StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception($"Expected input sample rate {inputSampleRate} not found in original file info: " + originalInfo, ex);
        }

        if (!shouldReencode)
        {
            // Should not re-encode - file should remain identical.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxSampleRate = maxSampleRate,
                    AudioReencodeMode = forceReencode ? StreamReencodeMode.Always : StreamReencodeMode.AvoidReencoding,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: null);
        }
        else
        {
            // Should re-encode audio only.
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    MaxSampleRate = maxSampleRate,
                    AudioReencodeMode = forceReencode ? StreamReencodeMode.Always : StreamReencodeMode.AvoidReencoding,
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: 2, StreamMapping:
                [
                    (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream unchanged
                    (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream re-encoded
                ]), afterFinishedAction: async (newPath) =>
                {
                    // Validate the output file has the expected sample rate:
                    string processedInfo = await RunFFtoolProcessWithErrorHandling(
                        "ffprobe",
                        ["-i", newPath, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                        TestContext.CancellationToken);

                    try
                    {
                        processedInfo.Contains(string.Create(CultureInfo.InvariantCulture,
                            $"\"sample_rate\": \"{expectedOutputSampleRate}\""), StringComparison.Ordinal).ShouldBeTrue();
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Expected output sample rate {expectedOutputSampleRate} not found in processed file info: " + processedInfo, ex);
                    }

                    // Validate that the result now meets the constraints (valid AAC rate and within max):
                    await CheckProcessing(
                        repo,
                        VideoProcessingOptions.Preserve with
                        {
                            ForceValidateAllStreams = DefaultForceValidateAllStreams,
                            MaxSampleRate = maxSampleRate,
                        },
                        newPath,
                        exceptionMessage: null,
                        expectedChanges: null,
                        pathIsAbsolute: true);
                });
        }
    }

    [TestMethod]
    [DataRow("video1.mp4", "h264")]
    [DataRow("video4.webm", "vp9")]
    [DataRow("video6.ts", "mpeg2video")]
    [DataRow("video7.mpeg", "mpeg1video")]
    [DataRow("video8.3gp", "h263")]
    [DataRow("video9.avi", "h263")]
    [DataRow("video11.mkv", "vp8")]
    [DataRow("video12.mp4", "av1")]
    [DataRow("video15.mp4", "vvc")]
    [DataRow("video164.mp4", "hevc")]
    public async Task TestSourceVideoCodecDetection(string fileName, string expectedVideoCodecName)
    {
        // Tests that SourceVideoCodecs correctly identifies video codecs by iterating through all known codecs.
        // Each test file should only match its expected codec and reject all others.

        using var repoCtx = GetRepo(out var repo);

        foreach (var codec in VideoCodec.AllSourceCodecs)
        {
            // Skip HEVCAnyTag as it is a special case.
            if (codec == VideoCodec.HEVCAnyTag)
                continue;

            bool isCorrect = codec.Name == expectedVideoCodecName;

            if (isCorrect)
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceVideoCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: null);
            }
            else
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceVideoCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
                    expectedChanges: null);
            }
        }
    }

    [TestMethod]
    [DataRow("video1.mp4", "aac", "LC")]
    [DataRow("video4.webm", "opus", null)]
    [DataRow("video6.ts", "mp3", null)]
    [DataRow("video7.mpeg", "mp2", null)]
    [DataRow("video13.mp4", "aac", "HE-AAC")]
    [DataRow("video14.mp4", "vorbis", null)]
    public async Task TestSourceAudioCodecDetection(string fileName, string expectedAudioCodecName, string? expectedAudioCodecProfile)
    {
        // Tests that SourceAudioCodecs correctly identifies audio codecs by iterating through all known codecs.
        // Each test file should only match its expected codec and reject all others.

        using var repoCtx = GetRepo(out var repo);

        foreach (var codec in AudioCodec.AllSourceCodecs)
        {
            // Match using the same logic as VideoProcessor.MatchAudioCodecByName:
            bool isCorrect = codec.Name == expectedAudioCodecName && (codec.Profile == null || codec.Profile == expectedAudioCodecProfile);

            if (isCorrect)
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceAudioCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: null);
            }
            else
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceAudioCodecs = [codec],
                    },
                    fileName,
                    exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
                    expectedChanges: null);
            }
        }
    }

    [TestMethod]
    [DataRow("video1.mp4", "mov,mp4,m4a,3gp,3g2,mj2")]
    [DataRow("video2.mkv", "matroska,webm")]
    [DataRow("video3.mov", "mov,mp4,m4a,3gp,3g2,mj2")]
    [DataRow("video4.webm", "matroska,webm")]
    [DataRow("video5.avi", "avi")]
    [DataRow("video6.ts", "mpegts")]
    [DataRow("video7.mpeg", "mpeg")]
    [DataRow("video8.3gp", "mov,mp4,m4a,3gp,3g2,mj2")]
    public async Task TestSourceFormatDetection(string fileName, string expectedFormatName)
    {
        // Tests that SourceFormats correctly identifies container formats by iterating through all known formats.
        // Each test file should only match its expected format and reject all others.

        using var repoCtx = GetRepo(out var repo);

        foreach (var format in MediaContainerFormat.AllSourceFormats)
        {
            bool isCorrect = format.Name == expectedFormatName;

            if (isCorrect)
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceFormats = [format],
                    },
                    fileName,
                    exceptionMessage: null,
                    expectedChanges: null);
            }
            else
            {
                await CheckProcessing(
                    repo,
                    VideoProcessingOptions.Preserve with
                    {
                        ForceValidateAllStreams = DefaultForceValidateAllStreams,
                        SourceFormats = [format],
                    },
                    fileName,
                    exceptionMessage: $"The source video format '{expectedFormatName}' is not supported by this processor.",
                    expectedChanges: null,
                    addAsyncExtensionOverride: format.CommonExtensions.First());
            }
        }
    }

    [TestMethod]
    [DataRow("video10.mp4", false)]
    [DataRow("video164.mp4", true)]
    public async Task TestSourceVideoCodecHEVCTagDetection(string fileName, bool isHvc1)
    {
        // Tests HEVC tag detection: VideoCodec.HEVC matches only hvc1-tagged files, while HEVCAnyTag matches any HEVC file.
        // video10.mp4 is hev1-tagged, video164.mp4 is hvc1-tagged.

        using var repoCtx = GetRepo(out var repo);

        // HEVC has TagName="hvc1", so it only matches hvc1 tagged files.
        // HEVCAnyTag has TagName=null, so it matches any hevc file regardless of tag.

        // Test HEVC (hvc1 only):
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.HEVC],
            },
            fileName,
            exceptionMessage: isHvc1 ? null : "One or more streams use a codec that is not supported by this processor.",
            expectedChanges: null);

        // Test HEVCAnyTag (matches any tag):
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                SourceVideoCodecs = [VideoCodec.HEVCAnyTag],
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    [DataRow("video10.mp4", false)]
    [DataRow("video164.mp4", true)]
    public async Task TestHEVCTagRemuxing(string fileName, bool isHvc1)
    {
        // Tests HEVC tag remuxing: using a result video codec of HEVC causes hev1-tagged files to be remuxed to hvc1,
        // while hvc1 files remain unchanged. HEVCAnyTag never triggers tag remuxing.

        using var repoCtx = GetRepo(out var repo);

        // When ResultVideoCodecs includes HEVC (hvc1), hev1 files should be remuxed to hvc1.
        // hvc1 files should remain unchanged.

        if (isHvc1)
        {
            // hvc1 file should not be changed:
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    ResultVideoCodecs = [VideoCodec.HEVC],
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: null);
        }
        else
        {
            // hev1 file should be remuxed to hvc1 (streams preserved but container changed):
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    ResultVideoCodecs = [VideoCodec.HEVC],
                },
                fileName,
                exceptionMessage: null,
                expectedChanges: (NewStreamCount: 2, StreamMapping:
                [
                    (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream was minorly changed (tag)
                    (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream unchanged
                ]));

            // Also, validate our exception handling by specifying only HEVC (hvc1) as source codec - should throw:
            await CheckProcessing(
                repo,
                VideoProcessingOptions.Preserve with
                {
                    ForceValidateAllStreams = DefaultForceValidateAllStreams,
                    SourceVideoCodecs = [VideoCodec.HEVC],
                },
                fileName,
                exceptionMessage: "One or more streams use a codec that is not supported by this processor.",
                expectedChanges: null);
        }

        // HEVCAnyTag should never remux just for tag purposes:
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResultVideoCodecs = [VideoCodec.HEVCAnyTag],
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestRotationMetadataHandling()
    {
        // This test creates a video that is physically rotated 90 degrees clockwise, but has rotation metadata set to -90 (to display correctly). It then
        // processes it with both preserve (both re-encoding & remuxing) and strip metadata modes to verify that rotation handling works correctly in all
        // cases.

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestRotationMetadataResults");
        resultsDir.Create();

        var tempRotatedInputFile = resultsDir.CombineFile("temp_input_rotated.mp4");
        var rotatedInputFile = resultsDir.CombineFile("input_rotated_with_metadata.mp4");
        var outputPreserveMetadataRemuxed = resultsDir.CombineFile("output_preserve_metadata_remuxed.mp4");
        var outputPreserveMetadataReencoded = resultsDir.CombineFile("output_preserve_metadata_reencoded.mp4");
        var outputStripMetadata = resultsDir.CombineFile("output_strip_metadata.mp4");
        tempRotatedInputFile.Delete();
        rotatedInputFile.Delete();
        outputPreserveMetadataRemuxed.Delete();
        outputPreserveMetadataReencoded.Delete();
        outputStripMetadata.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a video that is physically rotated 90 degrees clockwise, with rotation metadata set to -90 so that it displays correctly when played.
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "transpose=1",
                "-c:v", "libx264",
                "-c:a", "copy",
                "-y", tempRotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-display_rotation", "90",
                "-i", tempRotatedInputFile.PathExport,
                "-c", "copy",
                "-y", rotatedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        tempRotatedInputFile.Delete();

        // Process with metadata preservation (MetadataStrippingMode.None) and forced re-encoding:
        var pipeline1 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream1 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream1, true, pipeline1, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);

        var videoPath1 = await repo.GetAsync(fileId1);
        videoPath1.Exists.ShouldBeTrue();
        File.Copy(videoPath1.PathExport, outputPreserveMetadataReencoded.PathExport);

        // Process with metadata preservation (MetadataStrippingMode.None) and forced remuxing:
        var pipeline2 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var stream2 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream2, true, pipeline2, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);

        var videoPath2 = await repo.GetAsync(fileId2);
        videoPath2.Exists.ShouldBeTrue();
        File.Copy(videoPath2.PathExport, outputPreserveMetadataRemuxed.PathExport);

        // Process with metadata stripping (MetadataStrippingMode.Required):
        var pipeline3 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();

        await using var stream3 = rotatedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn3 = await repo.BeginTransactionAsync();
        var fileId3 = (await txn3.AddAsync(stream3, true, pipeline3, TestContext.CancellationToken)).FileId;
        await txn3.CommitAsync(TestContext.CancellationToken);

        var videoPath3 = await repo.GetAsync(fileId3);
        videoPath3.Exists.ShouldBeTrue();
        File.Copy(videoPath3.PathExport, outputStripMetadata.PathExport);
    }

    [TestMethod]
    public async Task TestMP4FileWithMkvExtensionWithCopying()
    {
        // Tests handling of an MP4 file masquerading with .mkv extension when file is unchanged (direct copy).
        // Verifies the output gets the correct .mp4 extension based on actual container format, but doesn't cause remuxing.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null,
            addAsyncExtensionOverride: ".mkv",
            afterFinishedAction: async (newPath) => FilePath.ParseAbsolute(newPath).Extension.ShouldBe(".mp4"));
    }

    [TestMethod]
    public async Task TestMP4FileWithMkvExtensionWithRemuxing()
    {
        // Tests handling of an MP4 file masquerading with .mkv extension when remuxing (via ForceProgressiveDownload).
        // Verifies the output gets the correct .mp4 extension based on actual container format.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ForceProgressiveDownload = true,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true),
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true),
            ]),
            addAsyncExtensionOverride: ".mkv",
            afterFinishedAction: async (newPath) => FilePath.ParseAbsolute(newPath).Extension.ShouldBe(".mp4"));
    }

    [TestMethod]
    public async Task TestMP4FileWithMkvExtensionWithReencoding()
    {
        // Tests handling of an MP4 file masquerading with .mkv extension when re-encoding.
        // Verifies the output gets the correct .mp4 extension based on actual container format.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
                AudioReencodeMode = StreamReencodeMode.Always,
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false),
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false),
            ]),
            addAsyncExtensionOverride: ".mkv",
            afterFinishedAction: async (newPath) => FilePath.ParseAbsolute(newPath).Extension.ShouldBe(".mp4"));
    }

#if !CI
    [TestMethod]
    [DataRow("video114")]
    [DataRow("Y0__auYqGXY-5s")]
    [DataRow("Y0__auYqGXY-20s")]
    public async Task TestHDRToSDRMapping(string fileName)
    {
        // Note: this test is excluded from CI runs since it is intended for manual inspection only (HDR->SDR logic is also tested in TestStandardizedOptions).
        // Tests HDR to SDR color mapping. Outputs the result files for manual inspection.

        var resultFileH264 = _appDir.CombineDirectory("TestHDRToSDRMappingResults").CombineFile(fileName + "-H264.mp4");
        resultFileH264.ParentDirectory?.Create();
        resultFileH264.Delete();

        var resultFileHEVC = _appDir.CombineDirectory("TestHDRToSDRMappingResults").CombineFile(fileName + "-HEVC.mp4");
        resultFileHEVC.ParentDirectory?.Create();
        resultFileHEVC.Delete();

        using var repoCtx = GetRepo(out var repo);

        var origFile = _videoFilesDir.CombineFile(fileName + ".mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        var pipelineH264 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            RemapHDRToSDR = true,
            ResultVideoCodecs = [VideoCodec.H264],
        }).ToPipeline();

        await using var txnH264 = await repo.BeginTransactionAsync();
        var fileIdH264 = (await txnH264.AddAsync(stream, true, pipelineH264, TestContext.CancellationToken)).FileId;
        await txnH264.CommitAsync(TestContext.CancellationToken);

        var videoPathH264 = await repo.GetAsync(fileIdH264);
        videoPathH264.Exists.ShouldBeTrue();

        File.Copy(videoPathH264.PathExport, resultFileH264.PathExport);

        var pipelineHEVC = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            RemapHDRToSDR = true,
            ResultVideoCodecs = [VideoCodec.HEVC],
        }).ToPipeline();

        await using var txnHEVC = await repo.BeginTransactionAsync();
        var fileIdHEVC = (await txnHEVC.AddAsync(stream, true, pipelineHEVC, TestContext.CancellationToken)).FileId;
        await txnHEVC.CommitAsync(TestContext.CancellationToken);

        var videoPathHEVC = await repo.GetAsync(fileIdHEVC);
        videoPathHEVC.Exists.ShouldBeTrue();

        File.Copy(videoPathHEVC.PathExport, resultFileHEVC.PathExport);
    }
#endif

    [TestMethod]
    public async Task TestNoUpscalingWithoutReencode()
    {
        // Tests that videos are not upscaled when resize options specify dimensions larger than the source.
        // video1.mp4 has dimensions 128x72 - we request 1920x1080 and expect no change.
        // Without re-encoding, the file should remain byte-for-byte identical (stream copy).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1920, 1080),
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestNoUpscalingWithForcedReencode()
    {
        // Tests that videos are not upscaled when resize options specify dimensions larger than the source.
        // video1.mp4 has dimensions 128x72 - we request 1920x1080 and expect no change to dimensions.
        // With forced re-encoding, the file will be re-encoded but should NOT be upscaled.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.Always,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 1920, 1080),
            },
            "video1.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video re-encoded
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true),  // Audio unchanged
            ]),
            afterFinishedAction: async (newPath) =>
            {
                // Verify dimensions are unchanged (not upscaled)
                string processedInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", newPath, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    processedInfo.Contains("\"width\": 128", StringComparison.Ordinal).ShouldBeTrue();
                    processedInfo.Contains("\"height\": 72", StringComparison.Ordinal).ShouldBeTrue();
                }
                catch (Exception ex)
                {
                    throw new Exception("Video was unexpectedly upscaled. Processed info: " + processedInfo, ex);
                }
            });
    }

    [TestMethod]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.Always, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.Always, StreamReencodeMode.Always, "video1.mp4")]
    [DataRow(false, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(true, true, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video1.mp4")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video8.3gp")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video8.3gp")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video8.3gp")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video8.3gp")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video8.3gp")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video8.3gp")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video16.mkv")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video16.mkv")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video16.mkv")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video16.mkv")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video16.mkv")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video16.mkv")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video20.mp4")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video20.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video20.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video20.mp4")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video20.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video20.mp4")]
    [DataRow(false, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video134.mkv")]
    [DataRow(true, false, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.AvoidReencoding, "video134.mkv")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video134.mkv")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video134.mkv")]
    [DataRow(false, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video134.mkv")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, "video134.mkv")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video171.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video171.mp4")]
    [DataRow(false, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video172.mp4")]
    [DataRow(true, false, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest, "video172.mp4")]
    [DataRow(true, false, StreamReencodeMode.Always, StreamReencodeMode.Always, BigBuckBunnyFullVideoFileName)]
    public async Task TestProgressCallbackMonotonicallyIncreasing(
        bool forceValidation,
        bool removeAudioStreams,
        StreamReencodeMode videoReencode,
        StreamReencodeMode audioReencode,
        string fileName)
    {
        // Tests progress callback is monotonically increasing across different processing configurations.
        // Tests important combos of: ForceValidateAllStreams, VideoReencodeMode, AudioReencodeMode, incompatible streams.

        using var repoCtx = GetRepo(out var repo);

        double lastProgress = double.MinValue;
        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = forceValidation,
            RemoveAudioStreams = removeAudioStreams,
            VideoReencodeMode = videoReencode,
            AudioReencodeMode = audioReencode,
            ResultFormats = [MediaContainerFormat.MP4],
            ProgressCallback = async (_, progress) =>
            {
                progress.ShouldBeGreaterThanOrEqualTo(0.0, "Progress should not be negative");
                progress.ShouldBeLessThanOrEqualTo(1.0, "Progress should not exceed 1.0");
                progress.ShouldBeGreaterThan(lastProgress, $"Progress did not increase from {lastProgress} to {progress}");
                lastProgress = progress;
            },
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task TestSelectSmallestReencodesOversizedStreams()
    {
        // Tests that SelectSmallest re-encodes oversized streams (where re-encoding produces smaller output)
        // and preserves undersized streams (where original is already smaller than re-encoded would be).

        using var repoCtx = GetRepo(out var repo);

        // video171.mp4: Both streams are oversized, both should be re-encoded.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video171.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));

        // video172.mp4: Both streams are undersized, file should be preserved.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video172.mp4",
            exceptionMessage: null,
            expectedChanges: null);

        // video173.mp4: Oversized video, undersized audio.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video173.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video174.mp4: Undersized video, oversized audio.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video174.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestSelectSmallestOnlyAffectsConfiguredStream()
    {
        // Tests that setting SelectSmallest for only video or only audio doesn't cause the other stream to change.

        using var repoCtx = GetRepo(out var repo);

        // video171.mp4 (oversized video & audio): SelectSmallest for video only - audio should be preserved.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video171.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: true), // Audio stream
            ]));

        // video171.mp4 (oversized video & audio): SelectSmallest for audio only - video should be preserved.
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            "video171.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: true), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: false), // Audio stream
            ]));
    }

    [TestMethod]
    public async Task TestForceValidateAllStreams()
    {
        // Tests that ForceValidateAllStreams causes invalid streams to be detected even when they would be missed otherwise.

        using var repoCtx = GetRepo(out var repo);

        // video175.mp4 has a validation error that is only found with ForceValidateAllStreams set to true (or if re-encoding).
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = false,
            },
            "video175.mp4",
            exceptionMessage: null,
            expectedChanges: null);
        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = true,
            },
            "video175.mp4",
            exceptionMessage: "An error occurred while validating the source video streams.",
            expectedChanges: null);
    }

    [TestMethod]
    public async Task TestStartTimeMetadataHandling()
    {
        // This test creates a video that starts at 5s into the timeline, with start time metadata set accordingly. It then processes it with all of preserve (
        // both re-encoding & remuxing), strip metadata mode, and unrecognized stream stripping to verify that start time handling works correctly in all cases
        // we expect.
        // Note: we re-encode the video to 30fps, as quicktime seems to struggle with non-zero start times at 60fps.
        // The expected result is that only 'output_strip_metadata.mp4' loses the start time metadata, while all other outputs retain it.

        async Task ValidateMetadata(IAbsoluteFilePath file, bool shouldHaveStartTime)
        {
            string videoInfo = await RunFFtoolProcessWithErrorHandling(
                "ffprobe",
                ["-i", file.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                TestContext.CancellationToken);

            try
            {
                videoInfo.Contains("\"timecode\": \"00:00:05;00\"", StringComparison.Ordinal).ShouldBe(shouldHaveStartTime);
            }
            catch (Exception ex)
            {
                throw new Exception("Video's metadata validation failed. Info: " + videoInfo, ex);
            }
        }

        using var repoCtx = GetRepo(out var repo);

        var resultsDir = _appDir.CombineDirectory("TestStartTimeMetadataResults");
        resultsDir.Create();

        var offsettedInputFile = resultsDir.CombineFile("input_offsetted_with_metadata.mp4");
        var outputPreserveMetadataRemuxed = resultsDir.CombineFile("output_preserve_metadata_remuxed.mp4");
        var outputPreserveMetadataReencoded = resultsDir.CombineFile("output_preserve_metadata_reencoded.mp4");
        var outputStripMetadata = resultsDir.CombineFile("output_strip_metadata.mp4");
        var outputStripUnrecognizedStreams = resultsDir.CombineFile("output_strip_unrecognized_streams.mp4");
        offsettedInputFile.Delete();
        outputPreserveMetadataRemuxed.Delete();
        outputPreserveMetadataReencoded.Delete();
        outputStripMetadata.Delete();
        outputStripUnrecognizedStreams.Delete();

        var origFile = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");

        // Create a video with the timecode set to start at 5s.
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", origFile.PathExport,
                "-vf", "fps=fps=30",
                "-timecode", "00:00:05.00",
                "-r", "30",
                "-c:a", "copy",
                "-c:v", "libx264",
                "-y", offsettedInputFile.PathExport
            ],
            TestContext.CancellationToken);
        await ValidateMetadata(offsettedInputFile, shouldHaveStartTime: true);

        await using var stream = offsettedInputFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        // Process with metadata preservation (MetadataStrippingMode.None) and forced re-encoding:
        var pipeline1 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            VideoReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();

        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream, true, pipeline1, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);

        var videoPath1 = await repo.GetAsync(fileId1);
        videoPath1.Exists.ShouldBeTrue();
        File.Copy(videoPath1.PathExport, outputPreserveMetadataReencoded.PathExport);
        await ValidateMetadata(outputPreserveMetadataReencoded, shouldHaveStartTime: true);

        // Process with metadata preservation (MetadataStrippingMode.None) and forced remuxing:
        var pipeline2 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.None,
            AudioReencodeMode = StreamReencodeMode.Always,
        }).ToPipeline();
        stream.Position = 0;

        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream, true, pipeline2, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);

        var videoPath2 = await repo.GetAsync(fileId2);
        videoPath2.Exists.ShouldBeTrue();
        File.Copy(videoPath2.PathExport, outputPreserveMetadataRemuxed.PathExport);
        await ValidateMetadata(outputPreserveMetadataRemuxed, shouldHaveStartTime: true);

        // Process with metadata stripping (MetadataStrippingMode.Required):
        var pipeline3 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            MetadataStrippingMode = VideoMetadataStrippingMode.Required,
        }).ToPipeline();
        stream.Position = 0;

        await using var txn3 = await repo.BeginTransactionAsync();
        var fileId3 = (await txn3.AddAsync(stream, true, pipeline3, TestContext.CancellationToken)).FileId;
        await txn3.CommitAsync(TestContext.CancellationToken);

        var videoPath3 = await repo.GetAsync(fileId3);
        videoPath3.Exists.ShouldBeTrue();
        File.Copy(videoPath3.PathExport, outputStripMetadata.PathExport);
        await ValidateMetadata(outputStripMetadata, shouldHaveStartTime: false);

        // Process with unrecognized stream stripping (TryPreserveUnrecognizedStreams = false):
        var pipeline4 = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            TryPreserveUnrecognizedStreams = false,
        }).ToPipeline();
        stream.Position = 0;

        await using var txn4 = await repo.BeginTransactionAsync();
        var fileId4 = (await txn4.AddAsync(stream, true, pipeline4, TestContext.CancellationToken)).FileId;
        await txn4.CommitAsync(TestContext.CancellationToken);

        var videoPath4 = await repo.GetAsync(fileId4);
        videoPath4.Exists.ShouldBeTrue();
        File.Copy(videoPath4.PathExport, outputStripUnrecognizedStreams.PathExport);
        await ValidateMetadata(outputStripUnrecognizedStreams, shouldHaveStartTime: true);
    }

    public static IEnumerable<object?[]> TestHEVCScalingNearLimitsData => field ??= [
        ["video176.mp4", (2, 527), (30, 1), (16, 4216), null],
        ["video177.mp4", (16, 4216), (30, 1), (16, 4216), null],
        ["video178.mp4", (16, 4217), (30, 1), (32, 8434), null],
        ["video179.mp4", (16, 4218), (30, 1), (32, 8436), null],
        ["video180.mp4", (8, 2109), (30, 1), (32, 8436), null],
        ["video181.mp4", (32, 64799), (30, 1), (32, 64799), null],
        ["video182.mp4", (63, 64799), (30, 1), (63, 64799), null],
        ["video183.mp4", (32, 64798), (30, 1), (32, 64798), null],
        ["video184.mp4", (64, 64799), (30, 1), (64, 64799), null],
        ["video197.mp4", (16, 4208), (1985, 1), (16, 4208), null],
        ["video198.mp4", (16, 4208), (1986, 1), (32, 8416), null],
        ["video199.mp4", (26, 4208), (992, 1), (26, 4208), null],
        ["video200.mp4", (26, 4208), (993, 1), (32, 5180), null],
        ["video176.mp4", (2, 527), (30, 1), (16, 4216), (16, 4216)],
        ["video177.mp4", (16, 4216), (30, 1), (16, 4216), (16, 4216)],
        ["video178.mp4", (16, 4217), (30, 1), null, (16, 4217)],
        ["video179.mp4", (16, 4218), (30, 1), null, (16, 4218)],
        ["video180.mp4", (8, 2109), (30, 1), null, (16, 4218)],
        ["video181.mp4", (32, 64799), (30, 1), (32, 64799), (32, 64799)],
        ["video182.mp4", (63, 64799), (30, 1), (63, 64799), (63, 64799)],
        ["video183.mp4", (32, 64798), (30, 1), (32, 64798), (32, 64798)],
        ["video184.mp4", (64, 64799), (30, 1), (64, 64799), (64, 64799)],
        ["video197.mp4", (16, 4208), (1985, 1), (16, 4208), (16, 4208)],
        ["video198.mp4", (16, 4208), (1986, 1), null, (16, 4208)],
        ["video199.mp4", (26, 4208), (992, 1), (26, 4208), (26, 4208)],
        ["video200.mp4", (26, 4208), (993, 1), null, (26, 4208)],
    ];

    [TestMethod]
    [DynamicData(nameof(TestHEVCScalingNearLimitsData))]
    public async Task TestHEVCScalingNearLimits(
        string fileName,
        (int W, int H) inputSize,
        (int Num, int Den) inputFps,
        (int W, int H)? expectedOutputSize,
        (int W, int H)? maxSize)
    {
        // Tests HEVC video encoding near the codec's dimension limits, verifying that videos are scaled as expected to meet HEVC minimum dimension
        // requirements when the source dimensions are too small so that re-encoding these videos to HEVC doesn't fail (unless we expect it to do so due to a
        // max size limitation).

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            ResultVideoCodecs = [VideoCodec.HEVC],
            ResizeOptions = maxSize != null ? new VideoResizeOptions(VideoResizeMode.FitDown, maxSize.Value.W, maxSize.Value.H) : null,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        // Verify input dimensions match expected size:
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains($"\"width\": {inputSize.W}", StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains($"\"height\": {inputSize.H}", StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains($"\"r_frame_rate\": \"{inputFps.Num}/{inputFps.Den}\"", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify input video dimensions. Info: " + originalInfo, ex);
        }

        // Run processing
        FileId fileId;
        if (expectedOutputSize is null)
        {
            var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
            {
                await using var txn = await repo.BeginTransactionAsync();
                fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
                await txn.CommitAsync(TestContext.CancellationToken);
            });
            ex.Message.ShouldContain("Cannot re-encode video to fit within specified dimensions.", Case.Sensitive);
            return;
        }
        else
        {
            await using var txn = await repo.BeginTransactionAsync();
            fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);
        }

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Verify output dimensions match expected scaled size
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            processedInfo.Contains($"\"width\": {expectedOutputSize.Value.W}", StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains($"\"height\": {expectedOutputSize.Value.H}", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify output video dimensions. Info: " + processedInfo, ex);
        }
    }

    public static IEnumerable<object?[]> TestChromaSubsamplingResolutionRoundingData => field ??= [
        ["video186.mp4", ChromaSubsampling.Subsampling420, (97, 97), (96, 96)],
        ["video186.mp4", ChromaSubsampling.Subsampling422, (97, 97), (96, 97)],
        ["video186.mp4", ChromaSubsampling.Subsampling444, (97, 97), (97, 97)],
        ["video187.mp4", ChromaSubsampling.Subsampling420, (99, 99), (100, 100)],
        ["video187.mp4", ChromaSubsampling.Subsampling422, (99, 99), (100, 99)],
        ["video187.mp4", ChromaSubsampling.Subsampling444, (99, 99), (99, 99)],
    ];

    [TestMethod]
    [DynamicData(nameof(TestChromaSubsamplingResolutionRoundingData))]
    public async Task TestChromaSubsamplingResolutionRounding(
        string fileName, ChromaSubsampling maxChromaSubsampling, (int W, int H) inputSize, (int W, int H) expectedOutputSize)
    {
        // Tests that video dimensions are correctly rounded to even values as required by chroma subsampling settings.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new VideoProcessor(VideoProcessingOptions.Preserve with
        {
            ForceValidateAllStreams = DefaultForceValidateAllStreams,
            VideoReencodeMode = StreamReencodeMode.Always,
            MaximumChromaSubsampling = maxChromaSubsampling,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        // Verify input dimensions match expected size
        string originalInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", origFile.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            originalInfo.Contains($"\"width\": {inputSize.W}", StringComparison.Ordinal).ShouldBeTrue();
            originalInfo.Contains($"\"height\": {inputSize.H}", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify input video dimensions. Info: " + originalInfo, ex);
        }

        // Run processing
        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var videoPath = await repo.GetAsync(fileId);
        videoPath.Exists.ShouldBeTrue();

        // Verify output dimensions match expected rounded size
        string processedInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", videoPath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            processedInfo.Contains($"\"width\": {expectedOutputSize.W}", StringComparison.Ordinal).ShouldBeTrue();
            processedInfo.Contains($"\"height\": {expectedOutputSize.H}", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to verify output video dimensions. Info: " + processedInfo, ex);
        }
    }

    public static IEnumerable<object[]> TestSelectSmallestMetadataHandlingData => field ??= new bool[] { false, true }
        .SelectMany((x) => (IEnumerable<(string File, bool IsVideoOversized, bool IsAudioOversized, bool StripMetadata)>)[
            ("video188.mp4", true, true, x),
            ("video189.mp4", false, false, x),
            ("video190.mp4", true, false, x),
            ("video191.mp4", false, true, x),
        ])
        .SelectMany((x) => (IEnumerable<object[]>)[
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.SelectSmallest, StreamReencodeMode.Always],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.SelectSmallest],
            [x.File, x.IsVideoOversized, x.IsAudioOversized, x.StripMetadata, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest],
        ]);

    [TestMethod]
    [DynamicData(nameof(TestSelectSmallestMetadataHandlingData))]
    public async Task TestSelectSmallestMetadataHandling(
        string fileName,
        bool isVideoOversized,
        bool isAudioOversized,
        bool stripMetadata,
        StreamReencodeMode videoReencodeMode,
        StreamReencodeMode audioReencodeMode)
    {
        // This test checks that the SelectSmallest & metadata handling interact correctly. We take video171.mp4 (which has oversized video & audio streams),
        // and add metadata to both the container & the video stream, and then make a copy that is undersized (equivalent of video172.mp4) and copies that are
        // mixed oversized / undersized (equivalent of video173.mp4 and video174.mp4), but with the same metadata. We then test the combos of these files and
        // metadata stripping & SelectSmallest modes to check the interraction between the options works as intended.
        // Note: the modified versions are named 188-191.
        // Note: we run on all combos of modes (with at least 1 SelectSmallest) on the 4 input files.
        // Note: this also tests the interraction of SelectSmallest with non-SelectSmallest modes for other streams.

        using var repoCtx = GetRepo(out var repo);

        bool shouldVideoChange = videoReencodeMode switch
        {
            StreamReencodeMode.Always => true,
            StreamReencodeMode.SelectSmallest => isVideoOversized,
            StreamReencodeMode.AvoidReencoding => false,
            _ => throw new InvalidOperationException("Unexpected StreamReencodeMode value."),
        };
        bool shouldAudioChange = audioReencodeMode switch
        {
            StreamReencodeMode.Always => true,
            StreamReencodeMode.SelectSmallest => isAudioOversized,
            StreamReencodeMode.AvoidReencoding => false,
            _ => throw new InvalidOperationException("Unexpected StreamReencodeMode value."),
        };
        bool shouldFileChange = shouldVideoChange || shouldAudioChange || stripMetadata;

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                MetadataStrippingMode = stripMetadata ? VideoMetadataStrippingMode.Required : VideoMetadataStrippingMode.None,
                VideoReencodeMode = videoReencodeMode,
                AudioReencodeMode = audioReencodeMode,
            },
            _videoFilesDir.CombineFile(fileName).PathExport,
            exceptionMessage: null,
            expectedChanges: !shouldFileChange ? null : (NewStreamCount: stripMetadata ? 2 : 3, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: !shouldVideoChange), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: !shouldAudioChange), // Audio stream
            ]),
            pathIsAbsolute: true,
            afterFinishedAction: async (processedFile) =>
            {
                string videoInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", processedFile, "-hide_banner", "-print_format", "json", "-show_streams", "-show_format", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    videoInfo.Contains("\"timecode\": \"00:00:05;00\"", StringComparison.Ordinal).ShouldBe(!stripMetadata);
                    videoInfo.Contains("\"artist\": \"Test Artist\"", StringComparison.Ordinal).ShouldBe(!stripMetadata);
                }
                catch (Exception ex)
                {
                    throw new Exception("Video's metadata validation failed. Info: " + videoInfo, ex);
                }
            });
    }

    public static IEnumerable<object[]> TestSelectSmallestWithOversizedResolutionForMP4Data => field ??=
        ((IEnumerable<(string File, bool AreStreamsOversized, bool ForceRemux, bool LimitTo44100Hz)>)[
            ("video192.mkv", true, false, false),
            ("video192.mkv", true, false, true),
            ("video192.mkv", true, true, false),
            ("video192.mkv", true, true, true),
            ("video193.mkv", false, false, false),
            ("video193.mkv", false, false, true),
            ("video193.mkv", false, true, false),
            ("video193.mkv", false, true, true),
        ])
        .SelectMany((x) => (IEnumerable<object[]>)[
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.SelectSmallest, StreamReencodeMode.SelectSmallest],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.SelectSmallest, StreamReencodeMode.AvoidReencoding],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.SelectSmallest, StreamReencodeMode.Always],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.AvoidReencoding, StreamReencodeMode.SelectSmallest],
            [x.File, x.AreStreamsOversized, x.ForceRemux, x.LimitTo44100Hz, StreamReencodeMode.Always, StreamReencodeMode.SelectSmallest],
        ]);

    [TestMethod]
    [DynamicData(nameof(TestSelectSmallestWithOversizedResolutionForMP4Data))]
    public async Task TestSelectSmallestWithOversizedResolutionForMP4(
        string fileName,
        bool areStreamsOversized,
        bool forceRemux,
        bool limitTo44100Hz,
        StreamReencodeMode videoReencodeMode,
        StreamReencodeMode audioReencodeMode)
    {
        // Tests how SelectSmallest mode interacts with videos that have resolution too large for MP4 container.
        // When the resolution exceeds MP4 limits, the video must be resized regardless of SelectSmallest outcome when we are remuxing or re-encoding (but the
        // video stream should only cause re-encoding when set to StreamReencodeMode.Always).
        // video192.mp4: oversized video, oversized audio, resolution too large for MP4.
        // video193.mp4: undersized video, undersized audio, resolution too large for MP4.
        // Note: this also tests that audio streams being re-encoded for other reasons (e.g. sample rate limiting) interacts correctly with SelectSmallest, and
        // that this interacts correctly with oversized video streams for MP4.

        using var repoCtx = GetRepo(out var repo);

        bool shouldVideoChange = videoReencodeMode != StreamReencodeMode.AvoidReencoding;
        bool shouldAudioChange = limitTo44100Hz || audioReencodeMode switch
        {
            StreamReencodeMode.Always => true,
            StreamReencodeMode.SelectSmallest => areStreamsOversized,
            StreamReencodeMode.AvoidReencoding => false,
            _ => throw new InvalidOperationException("Unexpected StreamReencodeMode value."),
        };
        bool shouldFileChange = forceRemux || shouldVideoChange || shouldAudioChange;

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = videoReencodeMode,
                AudioReencodeMode = audioReencodeMode,
                ResultFormats = forceRemux ? [MediaContainerFormat.MP4] : [MediaContainerFormat.MP4, MediaContainerFormat.Mkv],
                MaxSampleRate = limitTo44100Hz ? AudioSampleRate.Hz44100 : AudioSampleRate.Preserve,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: !shouldFileChange ? null : (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mkv", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: !shouldAudioChange), // Audio stream
            ]));
    }

    [TestMethod]
    [DataRow("video194.mkv", true, StreamReencodeMode.Always)]
    [DataRow("video194.mkv", true, StreamReencodeMode.AvoidReencoding)]
    [DataRow("video195.mkv", false, StreamReencodeMode.Always)]
    [DataRow("video195.mkv", false, StreamReencodeMode.AvoidReencoding)]
    public async Task TestSelectSmallestWithIncompatibleVideoCodecForMP4(
        string fileName,
        bool isVideoStreamOversized,
        StreamReencodeMode audioReencodeMode)
    {
        // Tests how SelectSmallest mode interacts with videos that have codecs incompatible with MP4 container.
        // When the video codec is not MP4-compatible, the video must be re-encoded regardless of SelectSmallest outcome when we are outputting to MP4.
        // video194.mkv: oversized video (VP9), AAC audio - video needs re-encoding for MP4 compatibility.
        // video195.mkv: undersized video (VP9), AAC audio - video needs re-encoding for MP4 compatibility.
        // This also tests that audio re-encoding mode (Always vs AvoidReencoding) interacts correctly with forced video re-encoding.

        using var repoCtx = GetRepo(out var repo);
        bool shouldAudioChange = audioReencodeMode != StreamReencodeMode.AvoidReencoding;
        bool shouldFileReencode = shouldAudioChange || isVideoStreamOversized;

        // File always changes because we force MP4 output and video codec is incompatible.

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = audioReencodeMode,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: !shouldFileReencode ? null : (NewStreamCount: 2, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mkv", Equal: false), // Video stream
                (From: 1, To: 1, ExtensionToCheckWith: ".mp4", Equal: !shouldAudioChange), // Audio stream
            ]));
    }

    [TestMethod]
    [DataRow("Y0__auYqGXY-1s-low.mp4", false)]
    [DataRow("Y0__auYqGXY-1s-high.mp4", true)]
    public async Task TestSelectSmallestAndSideEffectsInteractions(
        string fileName,
        bool isVideoOversized)
    {
        // Tests that the side-effect of HDR->SDR conversion occurs correctly when a video is re-encoded for any reason
        // (but does not cause re-encoding by itself when RemapHDRToSDR is not set).
        // Y0__auYqGXY-1s-low.mp4: undersized HDR video - should not be re-encoded with SelectSmallest, so HDR should be preserved.
        // Y0__auYqGXY-1s-high.mp4: oversized HDR video - should be re-encoded with SelectSmallest, causing HDR->SDR conversion as a side effect.
        // When video is re-encoded (either via SelectSmallest choosing re-encode, or Always mode), color properties should become bt709.
        // When video is not re-encoded (AvoidReencoding, or SelectSmallest choosing copy), original HDR color properties should be preserved.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: !isVideoOversized ? null : (NewStreamCount: 1, StreamMapping:
            [
                (From: 0, To: 0, ExtensionToCheckWith: ".mp4", Equal: false), // Video stream
            ]),
            afterFinishedAction: async (processedFile) =>
            {
                string videoInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", processedFile, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    // Check color properties - bt709 indicates that we re-encoded to SDR, other values indicate HDR preserved.
                    bool hasSDRColorTransfer = videoInfo.Contains("\"color_transfer\": \"bt709\"", StringComparison.Ordinal);
                    bool hasSDRColorPrimaries = videoInfo.Contains("\"color_primaries\": \"bt709\"", StringComparison.Ordinal);
                    bool hasSDRColorSpace = videoInfo.Contains("\"color_space\": \"bt709\"", StringComparison.Ordinal);

                    if (isVideoOversized)
                    {
                        // When re-encoded, all three properties should be bt709 (SDR).
                        hasSDRColorTransfer.ShouldBeTrue("Expected color_transfer to be bt709 after re-encoding HDR->SDR");
                        hasSDRColorPrimaries.ShouldBeTrue("Expected color_primaries to be bt709 after re-encoding HDR->SDR");
                        hasSDRColorSpace.ShouldBeTrue("Expected color_space to be bt709 after re-encoding HDR->SDR");
                    }
                    else
                    {
                        // When not re-encoded, original HDR color properties should be preserved (not bt709).
                        hasSDRColorTransfer.ShouldBeFalse("Expected color_transfer to NOT be bt709 when HDR is preserved");
                        hasSDRColorPrimaries.ShouldBeFalse("Expected color_primaries to NOT be bt709 when HDR is preserved");
                        hasSDRColorSpace.ShouldBeFalse("Expected color_space to NOT be bt709 when HDR is preserved");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception("Video's HDR/SDR color property validation failed. Info: " + videoInfo, ex);
                }
            });
    }

    [TestMethod]
    [DataRow("video1.mp4", 2)]
    [DataRow("video2.mkv", 2)]
    [DataRow("video3.mov", 2)]
    [DataRow("video4.webm", 2)]
    [DataRow("video5.avi", 2)]
    [DataRow("video6.ts", 2)]
    [DataRow("video7.mpeg", 2)]
    [DataRow("video8.3gp", 2)]
    [DataRow("video9.avi", 2)]
    [DataRow("video10.mp4", 2)]
    [DataRow("video11.mkv", 2)]
    [DataRow("video12.mp4", 2)]
    [DataRow("video13.mp4", 2)]
    [DataRow("video14.mp4", 2)]
    [DataRow("video15.mp4", 2)]
    [DataRow("video16.mkv", 3)]
    [DataRow("video17.mkv", 3)]
    [DataRow("video18.mkv", 3)]
    [DataRow("video19.mkv", 3)]
    [DataRow("video20.mp4", 3)]
    [DataRow("video169.mkv", 3)]
    [DataRow("video170.mkv", 3)]
    public async Task TestAllInputCodecsAndFileFormatsForSelectSmallest(string fileName, int streamCount)
    {
        // Tests that SelectSmallest mode works correctly across all supported input codecs and file formats.
        // We run with 'ForceProgressiveDownload = true' to ensure that remuxing to mp4 occurs (we currently do not support skipping this when already done).

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
                ForceProgressiveDownload = true,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: streamCount, StreamMapping: []));
    }

    [TestMethod]
    public async Task TestComplexFile()
    {
        // Test processing a complex file with many streams with a complex processing scenario.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = true,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.Always,
                ResultFormats = [MediaContainerFormat.MP4],
                MaxSampleRate = AudioSampleRate.Hz44100,
                MaximumChromaSubsampling = ChromaSubsampling.Subsampling420,
                AudioQuality = AudioQuality.Lowest,
                VideoQuality = VideoQuality.High,
                VideoCompressionLevel = VideoCompressionLevel.Low,
                ForceProgressiveFrames = true,
                ForceSquarePixels = true,
                FpsOptions = new VideoFpsOptions(VideoFpsMode.LimitByIntegerDivision, 13),
                MaxChannels = AudioChannels.Mono,
                MaximumBitsPerChannel = BitsPerChannel.Bits8,
                RemapHDRToSDR = true,
                ResizeOptions = new VideoResizeOptions(VideoResizeMode.FitDown, 41, 37),
                TryPreserveUnrecognizedStreams = true,
                ProgressCallback = async (_, _) => { },
                ForceProgressiveDownload = true,
                VideoSourceValidation = VideoStreamValidationOptions.None with
                {
                    MinWidth = 4,
                    MaxWidth = 5341,
                    MinHeight = 7,
                    MaxHeight = 8001,
                    MinPixels = 52,
                    MaxPixels = 10_000_234,
                    MinStreams = 2,
                    MaxStreams = 8,
                    MinLength = TimeSpan.FromMilliseconds(63),
                    MaxLength = TimeSpan.FromDays(4),
                },
                AudioSourceValidation = AudioStreamValidationOptions.None with
                {
                    MinStreams = 3,
                    MaxStreams = 4,
                    MinLength = TimeSpan.FromMilliseconds(47),
                    MaxLength = TimeSpan.FromDays(13),
                },
                ResultAudioCodecs = [AudioCodec.AAC],
                ResultVideoCodecs = [VideoCodec.HEVCAnyTag, VideoCodec.H264],
                MetadataStrippingMode = VideoMetadataStrippingMode.ThumbnailOnly,
            },
            "video133.mp4",
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 15, StreamMapping: []));
    }

    [TestMethod]
    [DataRow("video188.mp4")]
    [DataRow("video189.mp4")]
    [DataRow("video190.mp4")]
    [DataRow("video191.mp4")]
    public async Task TestSelectSmallestWithMetadataStrippingPreservesLanguage(string fileName)
    {
        // Test our handling around the manual metadata we set in combination with our SelectSmallest logic.

        using var repoCtx = GetRepo(out var repo);

        await CheckProcessing(
            repo,
            VideoProcessingOptions.Preserve with
            {
                ForceValidateAllStreams = DefaultForceValidateAllStreams,
                VideoReencodeMode = StreamReencodeMode.SelectSmallest,
                AudioReencodeMode = StreamReencodeMode.SelectSmallest,
                MetadataStrippingMode = VideoMetadataStrippingMode.Required,
            },
            fileName,
            exceptionMessage: null,
            expectedChanges: (NewStreamCount: 2, StreamMapping: []),
            afterFinishedAction: async (processedFile) =>
            {
                string videoInfo = await RunFFtoolProcessWithErrorHandling(
                    "ffprobe",
                    ["-i", processedFile, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
                    TestContext.CancellationToken);

                try
                {
                    // Check we have 2 language metadata still.
                    int first = videoInfo.IndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
                    int last = videoInfo.LastIndexOf("\"language\": \"eng\"", StringComparison.Ordinal);
                    first.ShouldBeGreaterThanOrEqualTo(0);
                    last.ShouldBeGreaterThanOrEqualTo(0);
                    first.ShouldNotBe(last);
                }
                catch (Exception ex)
                {
                    throw new Exception("Video's language metadata validation failed. Info: " + videoInfo, ex);
                }
            });
    }

#if !CI
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExtractThumbnailFromHDRVideoTest(bool remapHDRToSDR)
    {
        // Test extracting a thumbnail from an HDR video, with and without remapping to SDR.
        // This is a visual inspection test - the resulting images should be checked manually to ensure correct color representation.

        var resultFile = _appDir.CombineDirectory("TestHDRThumbnailResults").CombineFile(remapHDRToSDR ? "thumbnail_sdr.png" : "thumbnail_hdr.png");
        resultFile.ParentDirectory?.Create();
        resultFile.Delete();

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard with
        {
            RemapHDRToSDR = remapHDRToSDR,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("Y0__auYqGXY-20s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        File.Copy(imagePath.PathExport, resultFile.PathExport);
    }
#endif

    [TestMethod]
    [DataRow("bbb_sunflower_1080p_60fps_normal-25s.mp4", 10, 0.5, false)]
    [DataRow("bbb_sunflower_1080p_60fps_normal-25s.mp4", 15, 0.5, true)]
    public async Task TestThumbnailTimestampSelection(string fileName, double seconds, double fraction, bool useFraction)
    {
        // Tests that ThumbnailProcessor selects the correct frame timestamp when both ImageTimestamp and ImageTimestampFraction are specified.
        // The processor should take the minimum of the two when both have meaning.

        using var repoCtx = GetRepo(out var repo);

        // Helper to extract thumbnail with given options and return the image path:
        async Task<IAbsoluteFilePath> ExtractThumbnail(ThumbnailProcessingOptions options)
        {
            var pipeline = new ThumbnailProcessor(options).ToPipeline();
            var origFile = _videoFilesDir.CombineFile(fileName);
            await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);

            return await repo.GetAsync(fileId);
        }

        // Extract with both options set
        var bothOptionsPath = await ExtractThumbnail(new ThumbnailProcessingOptions
        {
            ImageTimestamp = TimeSpan.FromSeconds(seconds),
            ImageTimestampFraction = fraction,
            RemapHDRToSDR = true,
        });

        // Extract with only absolute timestamp:
        var absoluteOnlyPath = await ExtractThumbnail(new ThumbnailProcessingOptions
        {
            ImageTimestamp = TimeSpan.FromSeconds(seconds),
            RemapHDRToSDR = true,
        });

        // Extract with only fraction:
        var fractionOnlyPath = await ExtractThumbnail(new ThumbnailProcessingOptions
        {
            ImageTimestampFraction = fraction,
            RemapHDRToSDR = true,
        });

        // Check if we used the expected image:
        AreImagePixelsEqual(bothOptionsPath.PathExport, fractionOnlyPath.PathExport).ShouldBe(useFraction);
        AreImagePixelsEqual(bothOptionsPath.PathExport, absoluteOnlyPath.PathExport).ShouldBe(!useFraction);
    }

    [TestMethod]
    public async Task TestThumbnailFromExplicitThumbnailStream()
    {
        // Tests that when IncludeThumbnailVideoStreams is true, the thumbnail is extracted from the embedded thumbnail stream.
        // Uses video53.mp4 which has an embedded PNG thumbnail stream.
        // When IncludeThumbnailVideoStreams is true, the result should match test_image_1.png.
        // When IncludeThumbnailVideoStreams is false, the result should be different (taken from the main video stream).

        using var repoCtx = GetRepo(out var repo);

        var expectedThumbnailPath = _videoFilesDir.CombineFile("test_image_1.png");

        // Helper to extract thumbnail with given options and return the image path:
        async Task<IAbsoluteFilePath> ExtractThumbnail(ThumbnailProcessingOptions options)
        {
            var pipeline = new ThumbnailProcessor(options).ToPipeline();
            var origFile = _videoFilesDir.CombineFile("video53.mp4");
            await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

            await using var txn = await repo.BeginTransactionAsync();
            var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
            await txn.CommitAsync(TestContext.CancellationToken);

            return await repo.GetAsync(fileId);
        }

        // Extract with IncludeThumbnailVideoStreams = true - should use embedded thumbnail stream:
        var withThumbnailStreamPath = await ExtractThumbnail(ThumbnailProcessingOptions.Standard with
        {
            IncludeThumbnailVideoStreams = true,
        });

        // Extract with IncludeThumbnailVideoStreams = false - should use main video stream:
        var withoutThumbnailStreamPath = await ExtractThumbnail(ThumbnailProcessingOptions.Standard with
        {
            IncludeThumbnailVideoStreams = false,
        });

        // When using thumbnail stream, should match the expected image:
        AreImagePixelsEqual(withThumbnailStreamPath.PathExport, expectedThumbnailPath.PathExport)
            .ShouldBeTrue("When IncludeThumbnailVideoStreams is true, the result should match the embedded thumbnail (test_image_1.png).");

        // When not using thumbnail stream, should be different from the expected image:
        AreImagePixelsEqual(withoutThumbnailStreamPath.PathExport, expectedThumbnailPath.PathExport)
            .ShouldBeFalse("When IncludeThumbnailVideoStreams is false, the result should differ from the embedded thumbnail.");
    }

    [TestMethod]
    [DynamicData(nameof(ValidVideosWithVideoStreamsToCheck))]
    public async Task TestThumbnailProcessorAllOptionsConfigurations(string fileName)
    {
        // Brute force test that verifies ThumbnailProcessor can successfully process all valid video files
        // with all important combinations of options.

        using var repoCtx = GetRepo(out var repo);

        // Define the option variations to test:
        bool[] includeThumbnailVideoStreamsValues = [true, false];
        bool[] remapHDRToSDRValues = [true, false];
        bool[] forceSquarePixelsValues = [true, false];

        foreach (bool includeThumbnailVideoStreams in includeThumbnailVideoStreamsValues)
        {
            foreach (bool remapHDRToSDR in remapHDRToSDRValues)
            {
                foreach (bool forceSquarePixels in forceSquarePixelsValues)
                {
                    var options = ThumbnailProcessingOptions.Standard with
                    {
                        IncludeThumbnailVideoStreams = includeThumbnailVideoStreams,
                        RemapHDRToSDR = remapHDRToSDR,
                        ForceSquarePixels = forceSquarePixels,
                    };

                    try
                    {
                        var pipeline = new ThumbnailProcessor(options).ToPipeline();
                        var origFile = _videoFilesDir.CombineFile(fileName);
                        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

                        await using var txn = await repo.BeginTransactionAsync();
                        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
                        await txn.CommitAsync(TestContext.CancellationToken);

                        var imagePath = await repo.GetAsync(fileId);
                        imagePath.Exists.ShouldBeTrue();

                        // Verify it's a valid image by loading it:
                        using var image = Image.Load(imagePath.PathExport);
                        image.Width.ShouldBeGreaterThan(0);
                        image.Height.ShouldBeGreaterThan(0);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(
                            $"Failed to process '{fileName}' with options: IncludeThumbnailVideoStreams={includeThumbnailVideoStreams}, " +
                            $"RemapHDRToSDR={remapHDRToSDR}, ForceSquarePixels={forceSquarePixels}",
                            ex);
                    }
                }
            }
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TestThumbnailHDRColorSpaceRemapping(bool remapHDRToSDR)
    {
        // Tests that the HDR to SDR color space remapping works correctly for thumbnail extraction.
        // Uses an HDR video and verifies the color properties of the resulting thumbnail using ffprobe.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(new ThumbnailProcessingOptions
        {
            ImageTimestampFraction = 0.3,
            RemapHDRToSDR = remapHDRToSDR,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("Y0__auYqGXY-5s.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Use ffprobe to check color properties of the resulting image:
        string imageInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", imagePath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        try
        {
            // Check color properties - bt709/bt709 indicates SDR, other values indicate HDR preserved or unexpected SDR colors.
            imageInfo.Contains("\"color_transfer\": \"bt709\"", StringComparison.Ordinal).ShouldBe(remapHDRToSDR);
            imageInfo.Contains("\"color_primaries\": \"bt709\"", StringComparison.Ordinal).ShouldBe(remapHDRToSDR);
            imageInfo.Contains("\"color_space\": \"gbr\"", StringComparison.Ordinal).ShouldBeTrue();
        }
        catch (Exception ex)
        {
            throw new Exception("Thumbnail HDR/SDR color property validation failed. Info: " + imageInfo, ex);
        }
    }

    [TestMethod]
    [DataRow("video159.ts")]
    [DataRow("video161.mp4")]
    [DataRow("video168.mp4")]
    public async Task TestThumbnailFailsWithNoVideoStreams(string fileName)
    {
        // Tests that ThumbnailProcessor fails with a clear error when the input file has no video streams.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(new ThumbnailProcessingOptions
        {
            ImageTimestampFraction = 0.5,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
        {
            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("No suitable video stream found to extract thumbnail from.");
    }

    [TestMethod]
    public async Task TestThumbnailFailsWhenTimestampExceedsDuration()
    {
        // Tests that ThumbnailProcessor throws an exception when the requested timestamp exceeds the video duration.
        // Uses video1.mp4 which is 1 second long, with a 2 second timestamp request.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(new ThumbnailProcessingOptions
        {
            ImageTimestamp = TimeSpan.FromSeconds(2),
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile("video1.mp4");
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () =>
        {
            await using var txn = await repo.BeginTransactionAsync();
            await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken);
        });

        ex.Message.ShouldBe("Specified thumbnail timestamp is beyond the end of the video.");
    }

    [TestMethod]
    [DataRow("video1.mp4", "rgb24")]
    [DataRow("video32.mp4", "rgb48be")]
    [DataRow("video43.mp4", "rgb48be")]
    public async Task TestThumbnailBitDepthPreservation(string videoFileName, string expectedPixelFormat)
    {
        // Tests that 8-bit input videos produce 8-bpc PNG thumbnails, and higher bit depth videos produce 16-bpc PNG thumbnails.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(videoFileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Use ffprobe to get pix_fmt from the PNG:
        string imageInfo = await RunFFtoolProcessWithErrorHandling(
            "ffprobe",
            ["-i", imagePath.PathExport, "-hide_banner", "-print_format", "json", "-show_streams", "-v", "error"],
            TestContext.CancellationToken);

        // Check pixel format:
        imageInfo.Contains($"\"pix_fmt\": \"{expectedPixelFormat}\"", StringComparison.Ordinal)
            .ShouldBeTrue($"Expected pixel format {expectedPixelFormat} for {videoFileName}.");
    }

    [TestMethod]
    public async Task TestThumbnailSelectsDefaultVideoStreamOverNonDefault()
    {
        // Tests that ThumbnailProcessor selects a non-first video stream that is marked as default over a first video stream that is not marked as default.
        // Creates a test video with two video streams: first is non-default, second is default.

        using var repoCtx = GetRepo(out var repo);

        // Create a combined video file with two video streams where the second is marked as default:
        // We'll use video1.mp4 as the first (non-default) stream and video2.mkv as the second (default) stream.
        string combinedVideoPath = GetUniqueTempFilePath(".mp4");

        // Use ffmpeg to create a file with two video streams, marking the second as default:
        var file1 = _videoFilesDir.CombineFile("video1.mp4");
        var file2 = _videoFilesDir.CombineFile("bbb_sunflower_1080p_60fps_normal-1s.mp4");
        await RunFFtoolProcessWithErrorHandling(
            "ffmpeg",
            [
                "-i", file1.PathExport,
                "-i", file2.PathExport,
                "-map", "0:v:0",
                "-map", "1:v:0",
                "-c", "copy",
                "-disposition:v:0", "-default",
                "-disposition:v:1", "+default",
                "-t", "1",
                "-y",
                combinedVideoPath,
            ],
            TestContext.CancellationToken);

        // Extract thumbnail from the combined video:
        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard).ToPipeline();

        await using var stream = FilePath.ParseAbsolute(combinedVideoPath).OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, ".mp4", true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var thumbnailPath = await repo.GetAsync(fileId);
        thumbnailPath.Exists.ShouldBeTrue();

        // Also extract thumbnails from the individual source videos for comparison:
        await using var stream1 = file1.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
        await using var txn1 = await repo.BeginTransactionAsync();
        var fileId1 = (await txn1.AddAsync(stream1, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn1.CommitAsync(TestContext.CancellationToken);
        var thumbnail1Path = await repo.GetAsync(fileId1);

        await using var stream2 = file2.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);
        await using var txn2 = await repo.BeginTransactionAsync();
        var fileId2 = (await txn2.AddAsync(stream2, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn2.CommitAsync(TestContext.CancellationToken);
        var thumbnail2Path = await repo.GetAsync(fileId2);

        // The thumbnail from the combined video should match video2 (the default stream), not video1:
        AreImagePixelsEqual(thumbnailPath.PathExport, thumbnail2Path.PathExport).ShouldBeTrue();
        AreImagePixelsEqual(thumbnailPath.PathExport, thumbnail1Path.PathExport).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("video111.mp4", true, 171, 128)]
    [DataRow("video111.mp4", false, 128, 128)]
    [DataRow("video166.mp4", true, 128, 128)]
    [DataRow("video166.mp4", false, 96, 128)]
    public async Task TestThumbnailNonSquarePixelHandling(string fileName, bool forceSquarePixels, int expectedWidth, int expectedHeight)
    {
        // Tests that ForceSquarePixels option correctly handles videos with non-square pixel aspect ratios (SAR).
        // When ForceSquarePixels is true, the output dimensions should be adjusted to account for the SAR.
        // When ForceSquarePixels is false, the original pixel dimensions should be preserved.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard with
        {
            ForceSquarePixels = forceSquarePixels,
        }).ToPipeline();

        var origFile = _videoFilesDir.CombineFile(fileName);
        await using var stream = origFile.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Verify the output dimensions:
        using var image = Image.Load(imagePath.PathExport);
        image.Width.ShouldBe(expectedWidth);
        image.Height.ShouldBe(expectedHeight);
    }

    [TestMethod]
    public async Task TestThumbnailMaxResolutionCapping()
    {
        // Tests that the ThumbnailProcessor correctly caps output dimensions to 32767x32767.

        using var repoCtx = GetRepo(out var repo);

        var pipeline = new ThumbnailProcessor(ThumbnailProcessingOptions.Standard).ToPipeline();

        // video143.mp4 has a resolution of 64x65534, which exceeds the max height.
        var videoPath = _videoFilesDir.CombineFile("video143.mp4");
        await using var stream = videoPath.OpenAsyncStream(access: FileAccess.Read, share: FileShare.Read);

        await using var txn = await repo.BeginTransactionAsync();
        var fileId = (await txn.AddAsync(stream, ".mp4", true, pipeline, TestContext.CancellationToken)).FileId;
        await txn.CommitAsync(TestContext.CancellationToken);

        var imagePath = await repo.GetAsync(fileId);
        imagePath.Exists.ShouldBeTrue();

        // Verify the output dimensions are capped:
        using var image = Image.Load(imagePath.PathExport);
        image.Width.ShouldBe(32);
        image.Height.ShouldBe(32767);
    }
}
