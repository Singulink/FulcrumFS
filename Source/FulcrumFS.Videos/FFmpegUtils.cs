using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Singulink.IO;

namespace FulcrumFS.Videos;

/// <summary>
/// Provides utility methods for using ffmpeg.
/// </summary>
internal static class FFmpegUtils
{
    // Note: by default, the command is set up such that no streams nor metadata are copied over unless explicitly specified.
    public sealed class FFmpegCommand(
        ImmutableArray<IAbsoluteFilePath> inputFiles,
        IAbsoluteFilePath outputFile,
        ImmutableArray<PerInputStreamOverride> perInputStreamOverrides,
        ImmutableArray<PerOutputStreamOverride> perOutputStreamOverrides,
        int mapChaptersFrom,
        bool forceProgressiveDownloadSupport)
    {
        public ImmutableArray<IAbsoluteFilePath> InputFiles { get; } = inputFiles;
        public IAbsoluteFilePath OutputFile { get; } = outputFile;
        public ImmutableArray<PerInputStreamOverride> PerInputStreamOverrides { get; } = perInputStreamOverrides;
        public ImmutableArray<PerOutputStreamOverride> PerOutputStreamOverrides { get; } = perOutputStreamOverrides;
        public int MapChaptersFrom { get; } = mapChaptersFrom;
        public bool ForceProgressiveDownloadSupport { get; } = forceProgressiveDownloadSupport;
    }

    // For streamIndexWithinKind, if set to -1, applies to all streams of that kind in the file.
    // Additionally, if streamKind is set to '\0', applies to all streams in the file.
    // Note: if you have streamKind set to '\0' while streamIndexWithinKind is not -1, then it means the index in the file overall.
    // Note: the indices here are on the output file, not on input file/s.
    public abstract class PerOutputStreamOverride(char streamKind, int streamIndexWithinKind)
    {
        public char StreamKind { get; } = streamKind;
        public int StreamIndexWithinKind { get; } = streamIndexWithinKind;

        public bool AppliesToAllStreamsKinds => StreamKind == '\0';
        public bool AppliesToAllStreamsOfKind => StreamIndexWithinKind == -1;

        protected void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(StreamIndexWithinKind, -1);
        }

        protected abstract string CommandName { get; }
        protected abstract string CommandArgument { get; }

        public virtual void PrepareArguments(List<string> args)
        {
            Validate();

            if (!AppliesToAllStreamsOfKind && !AppliesToAllStreamsKinds)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-{CommandName}:{StreamKind}:{StreamIndexWithinKind}"));
            }
            else if (AppliesToAllStreamsKinds && AppliesToAllStreamsOfKind)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-{CommandName}"));
            }
            else if (AppliesToAllStreamsKinds)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-{CommandName}:{StreamIndexWithinKind}"));
            }
            else // if (AppliesToAllStreamsOfKind)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-{CommandName}:{StreamKind}"));
            }

            args.Add(CommandArgument);
        }
    }

    public sealed class PerStreamCodecOverride(char streamKind, int streamIndexWithinKind, string codec)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string Codec { get; } = codec;
        protected override string CommandName => "c";
        protected override string CommandArgument => Codec;
    }

    public sealed class PerStreamPixelFormatOverride(char streamKind, int streamIndexWithinKind, string pixelFormat)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string PixelFormat { get; } = pixelFormat;
        protected override string CommandName => "pix_fmt";
        protected override string CommandArgument => PixelFormat;
    }

    public sealed class PerStreamProfileOverride(char streamKind, int streamIndexWithinKind, string profile)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string Profile { get; } = profile;
        protected override string CommandName => "profile";
        protected override string CommandArgument => Profile;
    }

    public sealed class PerStreamCRFOverride(char streamKind, int streamIndexWithinKind, int crf)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public int CRF { get; } = crf;
        protected override string CommandName => "crf";
        protected override string CommandArgument { get; } = crf.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PerStreamVBROverride(char streamKind, int streamIndexWithinKind, int vbr)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public int VBR { get; } = vbr;
        protected override string CommandName => "vbr";
        protected override string CommandArgument { get; } = vbr.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PerStreamCutoffOverride(char streamKind, int streamIndexWithinKind, int cutoff)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public int Cutoff { get; } = cutoff;
        protected override string CommandName => "cutoff";
        protected override string CommandArgument { get; } = cutoff.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PerStreamBitrateOverride(char streamKind, int streamIndexWithinKind, int bitrate)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public int Bitrate { get; } = bitrate;
        protected override string CommandName => "b";
        protected override string CommandArgument { get; } = bitrate.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PerStreamFPSOverride(char streamKind, int streamIndexWithinKind, long fpsNum, long fpsDen)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public long FPSNum { get; } = fpsNum;
        public long FPSDen { get; } = fpsDen;
        protected override string CommandName => "r";
        protected override string CommandArgument { get; }
            = fpsDen == 1 ? fpsNum.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{fpsNum}/{fpsDen}");
    }

    public sealed class PerStreamPresetOverride(char streamKind, int streamIndexWithinKind, string preset)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string Preset { get; } = preset;
        protected override string CommandName => "preset";
        protected override string CommandArgument => Preset;
    }

    public sealed class PerStreamFilterOverride(char streamKind, int streamIndexWithinKind)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public (long Num, long Den)? FPS { get; set; }
        public (int Width, int Height)? Scale { get; set; }
        public string? NewVideoRange { get; set; }
        public bool HDRToSDR { get; set; }
        public string? SDRPixelFormat { get; set; }
        protected override string CommandName => "filter";
        protected override string CommandArgument => field ??= string.Join(',', ((string?[])[HDRToSDR switch
        {
            // Remap to HDR first for accurate results - however, this could have a performance penalty if we're also then scaling / sampling it after.
            false => null,
            _ =>
                $"zscale=t=linear:npl=500," +
                $"format=gbrpf32le," +
                $"zscale=p=bt709," +
                $"tonemap=tonemap=mobius:param=0.3:desat=0," +
                $"zscale=t=bt709:m=bt709:r={NewVideoRange ?? "pc"}," +
                $"format={SDRPixelFormat}",
        }, NewVideoRange switch
        {
            null => null,
            _ when HDRToSDR => null, // We already handled this above.
            var range => string.Create(CultureInfo.InvariantCulture, $"scale=out_range={range}"),
        }, FPS switch
        {
            null => null,
            (long num, 1) => string.Create(CultureInfo.InvariantCulture, $"fps={num}"),
            var (num, den) => string.Create(CultureInfo.InvariantCulture, $"fps={num}/{den}"),
        }, Scale switch
        {
            null => null,
            var (w, h) => string.Create(CultureInfo.InvariantCulture, $"scale={w}x{h}"),
        }]).Where((x) => x is not null));
    }

    public sealed class PerStreamChannelsOverride(char streamKind, int streamIndexWithinKind, int channels)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public int Channels { get; } = channels;
        protected override string CommandName => "ac";
        protected override string CommandArgument { get; } = channels.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PerStreamSampleRateOverride(char streamKind, int streamIndexWithinKind, int sampleRate)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public int SampleRate { get; } = sampleRate;
        protected override string CommandName => "ar";
        protected override string CommandArgument { get; } = sampleRate.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PerStreamColorRangeOverride(char streamKind, int streamIndexWithinKind, string colorRange)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string ColorRange { get; } = colorRange;
        protected override string CommandName => "color_range";
        protected override string CommandArgument => ColorRange;
    }

    public sealed class PerStreamColorTransferOverride(char streamKind, int streamIndexWithinKind, string colorTransfer)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string ColorTransfer { get; } = colorTransfer;
        protected override string CommandName => "color_trc";
        protected override string CommandArgument => ColorTransfer;
    }

    public sealed class PerStreamColorPrimariesOverride(char streamKind, int streamIndexWithinKind, string colorPrimaries)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string ColorPrimaries { get; } = colorPrimaries;
        protected override string CommandName => "color_primaries";
        protected override string CommandArgument => ColorPrimaries;
    }

    public sealed class PerStreamColorSpaceOverride(char streamKind, int streamIndexWithinKind, string colorSpace)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string ColorSpace { get; } = colorSpace;
        protected override string CommandName => "colorspace";
        protected override string CommandArgument => ColorSpace;
    }

    // For now we only support setting metadata by overall index to a stream, as we only need that currently.
    public sealed class PerStreamMetadataOverride(char streamKind, int streamIndexWithinKind)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string? Language { get; set; }
        public string? TitleOrHandlerName { get; set; }
        protected override string CommandName => string.Empty;
        protected override string CommandArgument => string.Empty;

        public override void PrepareArguments(List<string> args)
        {
            if (StreamKind != '\0' || StreamIndexWithinKind < 0)
            {
                throw new InvalidOperationException("PerStreamMetadataOverride currently only supports setting metadata by overall index to a stream.");
            }

            if (Language is not null)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-metadata:s:{StreamIndexWithinKind}"));
                args.Add(string.Create(CultureInfo.InvariantCulture, $"language={Language}"));
            }

            if (TitleOrHandlerName is not null)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-metadata:s:{StreamIndexWithinKind}"));
                args.Add(string.Create(CultureInfo.InvariantCulture, $"title={TitleOrHandlerName}"));

                args.Add(string.Create(CultureInfo.InvariantCulture, $"-metadata:s:{StreamIndexWithinKind}"));
                args.Add(string.Create(CultureInfo.InvariantCulture, $"handler_name={TitleOrHandlerName}"));
            }
        }
    }

    // For streamIndexWithinKind, if set to -1, applies to all streams of that kind in the file.
    // Additionally, if streamKind is set to '\0', applies to all streams in the file.
    // Note: if you have streamKind set to '\0' while streamIndexWithinKind is not -1, then it means the index in the file overall.
    // Note: the indices here are on the input file/s, not on output file.
    public abstract class PerInputStreamOverride(int fileIndex, char streamKind, int streamIndexWithinKind)
    {
        public int FileIndex { get; } = fileIndex;
        public char StreamKind { get; } = streamKind;
        public int StreamIndexWithinKind { get; } = streamIndexWithinKind;

        public bool AppliesToAllStreamsKinds => StreamKind == '\0';
        public bool AppliesToAllStreamsOfKind => StreamIndexWithinKind == -1;

        protected void Validate(bool allowFileIndexMinusOne)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(StreamIndexWithinKind, -1);
            ArgumentOutOfRangeException.ThrowIfLessThan(FileIndex, allowFileIndexMinusOne ? -1 : 0);
        }

        public abstract void PrepareArguments(List<string> args);
    }

    public sealed class PerStreamMapOverride(int fileIndex, char streamKind, int streamIndexWithinKind, bool mapToOutput)
        : PerInputStreamOverride(fileIndex, streamKind, streamIndexWithinKind)
    {
        public bool MapToOutput { get; } = mapToOutput;

        public override void PrepareArguments(List<string> args)
        {
            Validate(allowFileIndexMinusOne: false);

            args.Add("-map");

            string argumentPrefix = MapToOutput ? string.Empty : "-";
            if (!AppliesToAllStreamsOfKind && !AppliesToAllStreamsKinds)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"{argumentPrefix}{FileIndex}:{StreamKind}:{StreamIndexWithinKind}"));
            }
            else if (AppliesToAllStreamsKinds && AppliesToAllStreamsOfKind)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"{argumentPrefix}{FileIndex}"));
            }
            else if (AppliesToAllStreamsKinds)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"{argumentPrefix}{FileIndex}:{StreamIndexWithinKind}"));
            }
            else // if (AppliesToAllStreamsOfKind)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"{argumentPrefix}{FileIndex}:{StreamKind}"));
            }
        }
    }

    // File index -1 means to discard the metadata.
    // Note: for global metadata, 'g' should be the streamKind and streamIndexWithinKind and outputIndex should be -1.
    // Note: streamIndexWithinKind is for the input file, outputIndex is for the output file.
    // Note: we only expose basic metadata remapping options here, as it's all we need currently.
    // Note: we currently have metadata copying set up to be opt-in.
    public sealed class PerStreamMapMetadataOverride(int fileIndex, char streamKind, int streamIndexWithinKind, int outputIndex)
        : PerInputStreamOverride(fileIndex, streamKind, streamIndexWithinKind)
    {
        public int OutputIndex { get; } = outputIndex;

        public override void PrepareArguments(List<string> args)
        {
            Validate(allowFileIndexMinusOne: true);
            if (StreamKind != 'g')
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(OutputIndex, StreamIndexWithinKind == -1 ? -1 : 0);
            }
            else if (OutputIndex != -1 || StreamIndexWithinKind != -1)
            {
                throw new ArgumentException("If StreamKind is 'g' (global), then StreamIndexWithinKind and OutputIndex must both be -1.");
            }

            if (StreamKind == 'g')
            {
                args.Add("-map_metadata:g");
                if (FileIndex != -1) args.Add(string.Create(CultureInfo.InvariantCulture, $"{FileIndex}:g"));
                else args.Add("-1");
            }
            else if (!AppliesToAllStreamsOfKind && !AppliesToAllStreamsKinds)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-map_metadata:s:{StreamKind}:{OutputIndex}"));
                if (FileIndex != -1) args.Add(string.Create(CultureInfo.InvariantCulture, $"{FileIndex}:s:{StreamKind}:{StreamIndexWithinKind}"));
                else args.Add("-1");
            }
            else if (AppliesToAllStreamsKinds && AppliesToAllStreamsOfKind)
            {
                throw new ArgumentException("Cannot map metadata for all stream kinds and all streams of kind at once.");
            }
            else if (AppliesToAllStreamsKinds)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-map_metadata:s:{OutputIndex}"));
                if (FileIndex != -1) args.Add(string.Create(CultureInfo.InvariantCulture, $"{FileIndex}:s:{StreamIndexWithinKind}"));
                else args.Add("-1");
            }
            else // if (AppliesToAllStreamsOfKind)
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-map_metadata:s:{StreamKind}"));
                if (FileIndex != -1) args.Add(string.Create(CultureInfo.InvariantCulture, $"{FileIndex}:s:{StreamKind}"));
                else args.Add("-1");
            }
        }
    }

    private static IEnumerable<string> CreateArguments(FFmpegCommand command, string? progressFilePath)
    {
        List<string> args = [];

        // Input files:
        for (int i = 0; i < command.InputFiles.Length; i++)
        {
            args.Add("-i");
            args.Add(command.InputFiles[i].PathExport);
        }

        // Specifies how to map chapter metadata:
        args.Add("-map_chapters");
        args.Add(command.MapChaptersFrom.ToString(CultureInfo.InvariantCulture));

        // Per-input-stream overrides:
        foreach (var perInputOverride in command.PerInputStreamOverrides)
        {
            perInputOverride.PrepareArguments(args);
        }

        // Per-output-stream overrides:
        foreach (var perOutputOverride in command.PerOutputStreamOverrides)
        {
            perOutputOverride.PrepareArguments(args);
        }

        // Emit option to force progressive download support if requested:
        args.Add("-movflags");
        if (command.ForceProgressiveDownloadSupport)
        {
            args.Add("+faststart+use_metadata_tags");
        }
        else
        {
            args.Add("+use_metadata_tags");
        }

        // Progress reporting:
        if (progressFilePath != null)
        {
            args.Add("-progress");
            args.Add(new Uri(Path.GetFullPath(progressFilePath), UriKind.Absolute).AbsoluteUri);

            // Update stats every 16ms
            args.Add("-stats_period");
            args.Add("0.016");
        }

        // Unrecognized stream handling, error handling, and stdout handling:
        args.Add("-copy_unknown");
        args.Add("-xerror");
        args.Add("-hide_banner");

        // Output file:
        args.Add("-y");
        args.Add(command.OutputFile.PathExport);
        return args;
    }

    public static async Task RunFFmpegCommandAsync(
        FFmpegCommand command,
        Func<double, ValueTask>? progressCallback,
        IAbsoluteFilePath? progressFilePath,
        CancellationToken cancellationToken = default)
    {
        // Validate progress callback and progress temp file path args:
        // Note: RunRawFFmpegCommandAsync also checks this, but we do it here also to fail fast before running CreateArguments.
        if (progressCallback is null != progressFilePath is null)
        {
            throw new ArgumentException("If a progress callback or progress file path is provided, both must be provided and non-null.");
        }

        // Run actual ffmpeg command:
        await RunRawFFmpegCommandAsync(
            CreateArguments(command, progressFilePath?.PathExport),
            progressCallback,
            progressFilePath,
            ensureAllProgressRead: false,
            cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    }

    public static async Task RunRawFFmpegCommandAsync(
        IEnumerable<string> args,
        Func<double, ValueTask>? progressCallback,
        IAbsoluteFilePath? progressFilePath,
        bool ensureAllProgressRead,
        CancellationToken cancellationToken = default)
    {
        // Validate progress callback and progress temp file path args:
        if (progressCallback is null != progressFilePath is null)
        {
            throw new ArgumentException("If a progress callback or progress file path is provided, both must be provided and non-null.");
        }

        // Set up progress callback reading if needed:
        using var progressCallbackCts = new CancellationTokenSource();
        var progressCallbackCt = progressCallbackCts.Token;
        progressFilePath?.Delete();
        FileStream? fs = progressFilePath?.OpenAsyncStream(FileMode.CreateNew, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var progressCallbackTask = fs != null ? Task.Run(async () =>
            {
                List<byte> lineBuffer = [];
                byte[] buffer = new byte[32];
                int bytesRead;
                bool justRead = false;
                for (int i = 0; i < (ensureAllProgressRead ? 2 : 1); i++) // This loop is for ensuring we read all progress (even post-exit) if requested.
                {
                    do
                    {
                        while ((bytesRead = await fs.ReadAsync(buffer, ensureAllProgressRead ? default : progressCallbackCt).ConfigureAwait(false)) > 0)
                        {
                            justRead = true;
                            int beginIdx = 0;
                            while (true)
                            {
                                int eolIdx = buffer.AsSpan()[beginIdx..bytesRead].IndexOf((byte)'\n');
                                if (eolIdx >= 0)
                                {
                                    lineBuffer.AddRange(buffer.AsSpan().Slice(beginIdx, eolIdx));
                                    beginIdx = beginIdx + eolIdx + 1;

                                    // Check if line begins with out_time_us=, and send the seconds to the progress callback if so.
                                    ReadOnlySpan<byte> lineSpan = CollectionsMarshal.AsSpan(lineBuffer);
                                    if (lineSpan is [.., (byte)'\r']) lineSpan = lineSpan[..^1];
                                    if (lineSpan.StartsWith("out_time_us="u8))
                                    {
                                        ReadOnlySpan<byte> timeSpan = lineSpan["out_time_us="u8.Length..];
                                        if (long.TryParse(timeSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long outTimeUs))
                                        {
                                            double progress = outTimeUs / 1_000_000.0;
                                            if (progressCallback != null) await progressCallback(progress).ConfigureAwait(false);
                                        }
                                    }

                                    lineBuffer.Clear();
                                }
                                else
                                {
                                    lineBuffer.AddRange(buffer.AsSpan()[beginIdx..bytesRead]);
                                    break;
                                }
                            }
                        }

                        if (i != 0)
                        {
                            // If we're in the second iteration (ensuring all progress read), we don't want to wait as there won't be any more.
                            break;
                        }
                        else if (justRead)
                        {
                            // If we just read something, yield to allow more data to be written, but don't delay.
                            await Task.Yield();
                        }
                        else if (ensureAllProgressRead)
                        {
                            // If we're wanting to ensure all progress is read, we need to ensure we don't get a OperationCanceledException, as that would stop
                            // the outer loop from being able to run its second iteration.
                            try
                            {
                                await Task.Delay(5, progressCallbackCt).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Break out of the loop, as we're ready to do the i = 1 loop to finish reading the file & exit.
                                break;
                            }
                        }
                        else
                        {
                            // If we didn't read anything twice in a row, wait a bit before trying again.
                            await Task.Delay(5, progressCallbackCt).ConfigureAwait(false);
                        }
                    }
                    while (!progressCallbackCt.IsCancellationRequested);
                }
            }, ensureAllProgressRead ? default : progressCallbackCt) : null;

            // Run ffmpeg:
            try
            {
                await ProcessUtils.RunProcessWithErrorHandlingAsync(
                    VideoProcessor.FFmpegExePath,
                    args,
                    standardOutputWriter: null,
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Clean up progress callback resources:
                progressCallbackCts?.Cancel();
                if (progressCallbackTask != null)
                {
                    try
                    {
                        await progressCallbackTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Ignore cancellation exception, since we caused it to make the task exit.
                    }
                }
            }
        }
        finally
        {
            if (fs != null)
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
