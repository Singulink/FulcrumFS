using System.Runtime.ExceptionServices;
using Singulink.IO;

namespace FulcrumFS.Videos;

#pragma warning disable SA1642 // Constructor summary documentation should begin with standard text

/// <summary>
/// Provides functionality to extract thumbnails from video files with specified options.
/// </summary>
public sealed class ThumbnailProcessor : FileProcessor
{
    /// <summary>
    /// <para>
    /// Initializes a new instance of the <see cref="ThumbnailProcessor"/> class with the specified options.</para>
    /// <para>
    /// Note: you must configure the ffmpeg executable paths by calling <see cref="VideoProcessor.ConfigureWithFFmpegExecutables"/> before creating an instance
    /// of this class.</para>
    /// <para>
    /// Note: if you want to do source video validation, you need to use <see cref="VideoProcessor" /> first and chain this after it, as this class does not
    /// perform any validation itself, it just extracts a thumbnail from the provided video.</para>
    /// </summary>
    public ThumbnailProcessor(ThumbnailProcessingOptions options)
    {
        Options = options;

        // Check if ffmpeg is configured:
        _ = VideoProcessor.FFmpegExePath;

        // Check that ffmpeg supports png encoding:
        if (!FFprobeUtils.Configuration.SupportsPngEncoder)
        {
            throw new NotSupportedException("The required encoder for 'png' is not supported by the configured ffmpeg installation.");
        }
    }

    /// <summary>
    /// Gets the options used to configure this <see cref="ThumbnailProcessor" />.
    /// </summary>
    public ThumbnailProcessingOptions Options { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<string> AllowedFileExtensions => [];

    /// <inheritdoc/>
    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        // Get temp file for video:
        var inputFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);

        // Check file name is not going to be potentially problematic for ffmpeg (e.g., contains special characters):
        // Note: we assume that paths given by 'GetNewWorkFile' are safe if at least one is, so we only check the original source and an unused GetNewWorkFile
        // file here.
        FilePath.ParseAbsolute(inputFile.PathExport, PathOptions.NoUnfriendlyNames);
        FilePath.ParseAbsolute(context.GetNewWorkFile(string.Empty).PathExport, PathOptions.NoUnfriendlyNames);

        // Read info of source video:
        FFprobeUtils.VideoFileInfo sourceInfo;
        try
        {
            sourceInfo = await FFprobeUtils.GetVideoFileAsync(inputFile, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileProcessingException("Failed to read source video file information.", ex);
        }

        // Select the video stream to extract thumbnail from (we prefer thumbnail streams, still image streams, then others that are not bad candidates for
        // thumbnails (due to weird disposition), then any video stream - and within those categories we prefer default streams, except the last):
        var (stream, idx) = sourceInfo.Streams
            .Select((x, i) => (x, i))
            .Where((x) => x.x is FFprobeUtils.VideoStreamInfo)
            .Select((x) => (Stream: (FFprobeUtils.VideoStreamInfo)x.x, Index: x.i))
            .OrderBy((x) => x.Stream switch
            {
                { IsTimedThumbnails: true } or { IsAttachedPic: true } => Options.IncludeThumbnailVideoStreams ? (x.Stream.IsDefaultStream ? 0 : 1) : 7,
                { IsStillImage: true } => x.Stream.IsDefaultStream ? 2 : 3,
                { IsBadCandidateForThumbnail: true } => 6,
                _ => x.Stream.IsDefaultStream ? 4 : 5,
            })
            .FirstOrDefault();

        // If no suitable stream found, throw:
        if (stream is null)
            throw new FileProcessingException("No suitable video stream found to extract thumbnail from.");

        // Determine timestamp to extract at:
        double? duration = stream.Duration ?? sourceInfo.Duration;
        double? timestampSeconds = null;
        bool usingFraction = false;
        if (duration is double dur && !stream.IsAttachedPic && !stream.IsTimedThumbnails && !stream.IsStillImage)
        {
            double? o1 = Options.ImageTimestamp?.TotalSeconds;
            double? o2 = Options.ImageTimestampFraction * dur;

            (timestampSeconds, usingFraction) = (o1, o2) switch
            {
                (double v1, double v2) => (double.Min(v1, v2), v2 < v1),
                (double v1, null) => (v1, false),
                (null, double v2) => (v2, true),
                _ => throw new FileProcessingException("No timestamp specified to extract thumbnail at."),
            };

            if (timestampSeconds.HasValue && timestampSeconds.Value > dur)
                throw new FileProcessingException("Specified thumbnail timestamp is beyond the end of the video.");
        }

        // Create temp file for output image:
        var outputImageFile = context.GetNewWorkFile(".png");

        // Prepare the filter if we need it:
        IEnumerable<FFmpegUtils.PerOutputStreamOverride> additionalOutputStreamOverrides = [];
        FFmpegUtils.PerStreamFilterOverride? filterOverride = new(streamKind: 'v', streamIndexWithinKind: 0);
        bool useOverride = false;

        // Check if we need to remap HDR to SDR:
        if (Options.RemapHDRToSDR && !VideoProcessor.IsKnownSDRColorProfile(stream.ColorTransfer, stream.ColorPrimaries, stream.ColorSpace))
        {
            // The tonemap uses a high-fidelity RGB{A} format for accurate color conversion & preserving any alpha information; callers can use additional
            // processing using a different image processor to remove alpha channel or reduce the bit count.
            useOverride = true;
            filterOverride.HDRToSDR = true;
            filterOverride.AssumePotentialAlphaChannelForHDRToSDR = stream.PixelFormat is null || DoesPixelFormatHaveAlphaChannel(stream.PixelFormat);
        }

        // Check if we need to resize:
        var (w, h) = CalculateThumbnailResize(
            streamWidth: stream.Width,
            streamHeight: stream.Height,
            streamSarNum: Options.ForceSquarePixels ? stream.SarNum : -1,
            streamSarDen: Options.ForceSquarePixels ? stream.SarDen : -1,
            cancellationToken: context.CancellationToken);
        if (w != stream.Width || h != stream.Height)
        {
            useOverride = true;
            filterOverride.ResizeTo = (w, h);
        }

        // Check for square pixels:
        if (Options.ForceSquarePixels && VideoProcessor.HasNonSquarePixels(stream.SarNum, stream.SarDen))
        {
            useOverride = true;
            filterOverride.MakePixelsSquareMode = stream.SarNum > stream.SarDen ? 2 : 3;
        }

        // Ensure our pixel shape mode matches our intent:
        if (filterOverride.MakePixelsSquareMode < 2)
        {
            // Mode 0 means "set to 1:1", which we want to do if we're potentially resizing & aren't setting elsewhere; and mode 1 means "automatically
            // adjust", which we want to do if we're not converting to square pixels.
            filterOverride.MakePixelsSquareMode = Options.ForceSquarePixels ? 0 : 1;
        }

        // Create an enumerable so we can use spread syntax later:
        if (useOverride) additionalOutputStreamOverrides = [filterOverride];

        // Helper to run the thumbnail extraction ffmpeg command:
        async Task<Exception?> TryRun((double Offset, bool FromEnd)? seek)
        {
            try
            {
                try
                {
                    await FFmpegUtils.RunFFmpegCommandAsync(
                        new FFmpegUtils.FFmpegCommand(
                            inputFiles: [(inputFile, Seek: seek)],
                            outputFile: outputImageFile,
                            perInputStreamOverrides:
                            [
                                new FFmpegUtils.PerStreamMapOverride(fileIndex: 0, streamKind: '\0', streamIndexWithinKind: idx, mapToOutput: true),
                            ],
                            perOutputStreamOverrides:
                            [
                                new FFmpegUtils.PerStreamCodecOverride(streamKind: 'v', streamIndexWithinKind: 0, codec: "png"),
                                new FFmpegUtils.PerStreamFramesOverride(streamKind: 'v', streamIndexWithinKind: 0, frames: 1),
                                .. additionalOutputStreamOverrides,
                            ],
                            mapChaptersFrom: -1,
                            forceProgressiveDownloadSupport: false,
                            isToMov: false),
                        null,
                        null,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new FileProcessingException("Failed to extract thumbnail image from video.", ex);
                }

                if (!outputImageFile.Exists || outputImageFile.Length == 0)
                    throw new FileProcessingException("Failed to extract thumbnail image from video (result file was not written).");

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ex;
            }
        }

        // Run ffmpeg to extract thumbnail:
        var originalException = await TryRun(timestampSeconds.HasValue ? (Offset: timestampSeconds.Value, FromEnd: false) : null).ConfigureAwait(false);
        if (originalException is null)
        {
            // Success:
            return FileProcessingResult.File(outputImageFile, hasChanges: true);
        }
        else if (timestampSeconds is null || duration is null)
        {
            // Nothing else to try in this case, re-throw original exception:
            ExceptionDispatchInfo.Capture(originalException).Throw();
        }

        // If we got an error, try again from end (since our duration info is only approximate):
        if (await TryRun((Offset: timestampSeconds.Value - duration.Value, FromEnd: true)).ConfigureAwait(false) == null)
        {
            // Success:
            return FileProcessingResult.File(outputImageFile, hasChanges: true);
        }

        // Finally, just try using the very first or last frame (depending on what we're meant to be closer to):
        bool fromEnd = (usingFraction, duration) switch
        {
            (true, double d) => Options.ImageTimestampFraction > 0.5,
            (false, double d) => Options.ImageTimestamp!.Value.TotalSeconds > 0.5 * d,
            _ => false,
        };
        if (await TryRun((Offset: 0.0, FromEnd: fromEnd)).ConfigureAwait(false) == null)
        {
            // Success:
            return FileProcessingResult.File(outputImageFile, hasChanges: true);
        }

        // All attempts failed, re-throw original exception:
        ExceptionDispatchInfo.Capture(originalException).Throw();
        return null;
    }

    // Helper to share the logic for resizing a video thumbnail (similar to VideoProcessor.CalculateVideoResize, but simpler).
    private static (int Width, int Height) CalculateThumbnailResize(
        int streamWidth,
        int streamHeight,
        int streamSarNum,
        int streamSarDen,
        CancellationToken cancellationToken)
    {
        // We use 2^15 - 1 as the max dimensions, since some image viewers/editors have trouble with images larger than that (and it's more than large enough
        // for most uses of thumbnails anyway):
        int maxW = 32767;
        int maxH = 32767;

        // Determine the resulting size of the video:
        int resultWidth, resultHeight;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resize to square pixels if wanted:

            if (streamSarNum <= 0 || streamSarDen <= 0)
            {
                streamSarNum = 1;
                streamSarDen = 1;
            }

            double w0 = streamWidth, h0 = streamHeight;
            if (streamSarNum != 1 || streamSarDen != 1)
            {
                if (streamSarNum > streamSarDen)
                {
                    w0 *= (double)streamSarNum / streamSarDen;
                }
                else
                {
                    h0 *= (double)streamSarDen / streamSarNum;
                }
            }

            // Apply min & max scaling if any:
            {
                // Declare variables:
                double w1, w2, h1, h2;

                // Apply max:
                if (w0 > maxW || h0 > maxH)
                {
                    w1 = double.Min(maxW, (double)w0 / h0 * maxH);
                    h1 = double.Min(maxH, (double)h0 / w0 * maxW);
                }
                else
                {
                    w1 = w0;
                    h1 = h0;
                }

                // Pre-round to 8dp, so that we are likely to round at midpoint correctly if we were to calculate exactly:
                // Note: the rounding is here to ensure that we do not end up with tiny fractions that cause us to round the wrong way later in the case of
                // what should end up nice values (e.g., 128x128 at 4:3 becoming 170.67x128, and then limiting to 100x100 becoming 100x75, which should round
                // to 100x76, not 100x74).
                w2 = double.Round(w1, 8);
                h2 = double.Round(h1, 8);

                // Round as required:
                double w3 = double.Round(w2);
                double h3 = double.Round(h2);
                if (w3 > maxW) w3 = double.Floor(w2);
                if (h3 > maxH) h3 = double.Floor(h2);

                // Store final size:
                resultWidth = int.CreateSaturating(w3);
                resultHeight = int.CreateSaturating(h3);
                if (resultWidth < 1) resultWidth = 1;
                if (resultHeight < 1) resultHeight = 1;
            }

            // Ensure we stay under ffmpeg's max image pixel count limits (similar logic to VideoProcessor.CalculateVideoResize):
            // This also has the benefit that we will stay under the limits of many image viewers in most cases, which often have trouble with > 2^31 - 1 byte
            // images.
            long bytesRequiredForPixelsDiv8 = (((resultWidth + 63L) & ~63) + 128L) * (resultHeight + 128L);
            if (bytesRequiredForPixelsDiv8 > int.MaxValue / 8)
            {
                // Set max dimensions to current size first
                maxW = resultWidth;
                maxH = resultHeight;

                // Now, reduce the max dimensions to what should be correct approximately, and ensure at least 1 dimension is reduced:
                double scaleFactor = double.Sqrt(int.MaxValue / 8.0 / bytesRequiredForPixelsDiv8);
                int newMaxW = int.Min((int)double.Ceiling(resultWidth * scaleFactor), maxW);
                int newMaxH = int.Min((int)double.Ceiling(resultHeight * scaleFactor), maxH);
                if (newMaxW == maxW && newMaxH == maxH)
                {
                    if (newMaxW > newMaxH) newMaxW--;
                    else newMaxH--;
                }

                // Update our max dimensions and try again:
                maxW = newMaxW;
                maxH = newMaxH;
                continue;
            }

            break;
        }

        return (resultWidth, resultHeight);
    }

    // Note: this API might give some false positives; our usages of it will not be incorrect if it does though.
    private static bool DoesPixelFormatHaveAlphaChannel(string pixelFormat) =>
        !pixelFormat.StartsWith("bayer_", StringComparison.Ordinal) &&
        !pixelFormat.StartsWith("gray", StringComparison.Ordinal) &&
        pixelFormat.Contains('a');
}
