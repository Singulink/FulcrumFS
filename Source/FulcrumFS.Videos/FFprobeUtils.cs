using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Singulink.IO;

namespace FulcrumFS.Videos;

/// <summary>
/// Provides utility methods for using ffprobe (for video file analysis, and extracting ffmpeg configuration info).
/// </summary>
internal static class FFprobeUtils
{
    private static ConfigurationInfo _configInfo;
    private static volatile bool _configInfoInitialized;
    private static readonly Lock _configInitLock = new();

    public static ref readonly ConfigurationInfo Configuration
    {
        get
        {
            EnsureConfigurationInfoInitialized();
            return ref _configInfo;
        }
    }

    /// <summary>
    /// Throws if the configured ffmpeg/ffprobe binaries are missing any encoder, decoder, muxer, demuxer or filter the video pipeline may require for the unit
    /// tests (i.e., include anything we may opportunistically depend on, or outright require).
    /// </summary>
    internal static void EnsureAllFeaturesPresent()
    {
        ref readonly var c = ref Configuration;

        static void Require(bool supported, [CallerArgumentExpression(nameof(supported))] string? feature = null)
        {
            if (!supported)
                throw new InvalidOperationException($"The configured ffmpeg/ffprobe build does not support a required feature: {feature}.");
        }

        // Encoders
        Require(c.SupportsLibX264Encoder);
        Require(c.SupportsLibX265Encoder);
        Require(c.SupportsPngEncoder);
        Require(c.SupportsLibFDKAACEncoder);
        Require(c.SupportsAACEncoder);
        Require(c.SupportsMovTextEncoder);
        Require(c.SupportsDvdSubEncoder);

        // Video decoders
        Require(c.SupportsMpeg1VideoDecoder);
        Require(c.SupportsMpeg2VideoDecoder);
        Require(c.SupportsMpeg4Decoder);
        Require(c.SupportsH263Decoder);
        Require(c.SupportsH264Decoder);
        Require(c.SupportsHEVCDecoder);
        Require(c.SupportsVVCDecoder);
        Require(c.SupportsVP8Decoder);
        Require(c.SupportsVP9Decoder);
        Require(c.SupportsAV1Decoder);
        Require(c.SupportsLibDav1dDecoder);
        Require(c.SupportsLibVpxDecoder);
        Require(c.SupportsLibVpxVp9Decoder);

        // Audio decoders
        Require(c.SupportsAACDecoder);
        Require(c.SupportsMP2Decoder);
        Require(c.SupportsMP3Decoder);
        Require(c.SupportsVorbisDecoder);
        Require(c.SupportsOpusDecoder);
        Require(c.SupportsAmrNbDecoder);
        Require(c.SupportsAmrWbDecoder);

        // Muxers
        Require(c.SupportsMP4Muxing);

        // Demuxers
        Require(c.SupportsMovGroupDemuxing);
        Require(c.SupportsMatroskaGroupDemuxing);
        Require(c.SupportsAviDemuxing);
        Require(c.SupportsMpegTSGroupDemuxing);
        Require(c.SupportsMpegDemuxing);

        // Filters
        Require(c.SupportsZscaleFilter);
        Require(c.SupportsScaleFilter);
        Require(c.SupportsFpsFilter);
        Require(c.SupportsTonemapFilter);
        Require(c.SupportsFormatFilter);
        Require(c.SupportsBwdifFilter);
        Require(c.SupportsSetsarFilter);
        Require(c.SupportsTransposeFilter);
        Require(c.SupportsHFlipFilter);
        Require(c.SupportsVFlipFilter);
        Require(c.SupportsSidedataFilter);
    }

    public sealed class VideoFileInfo(string formatName, double? duration, ImmutableArray<StreamInfo> streams)
    {
        public string FormatName { get; } = formatName;
        public double? Duration { get; } = duration;
        public ImmutableArray<StreamInfo> Streams { get; } = streams;
    }

    public abstract record StreamInfo;

    public sealed record VideoStreamInfo(
        string CodecName,
        string CodecTagString,
        string? ProfileName,
        string? Language,
        bool IsAttachedPic,
        bool IsTimedThumbnails,
        bool IsStillImage,
        bool IsDefaultStream,
        bool IsBadCandidateForThumbnail,
        int Width,
        int Height,
        double? Duration,
        int FpsNum,
        int FpsDen,
        int SarNum,
        int SarDen,
        string? PixelFormat,
        string? ColorRange,
        string? ColorSpace,
        string? ColorTransfer,
        string? ColorPrimaries,
        string? FieldOrder,
        int BitsPerSample,
        bool AlphaMode,
        int Rotation,
        bool HasNonStandardDisplayMatrix)
    : StreamInfo;

    public sealed record AudioStreamInfo(
        string CodecName,
        string? ProfileName,
        string? Language,
        double? Duration,
        int Channels,
        int? SampleRate,
        string? ChannelLayout)
    : StreamInfo;

    public sealed record SubtitleStreamInfo(
        string CodecName,
        string? Language,
        string? Title)
    : StreamInfo;

    public sealed record UnrecognizedStreamInfo(
        string CodecType,
        string? CodecName,
        string? Language,
        char StreamShorthand,
        bool IsAttachedPic,
        bool IsTimedThumbnails)
    : StreamInfo;

    public static async Task<VideoFileInfo> GetVideoFileAsync(IAbsoluteFilePath filePath, CancellationToken cancellationToken = default)
    {
        // Get the ffprobe JSON output for the file:
        string json = await ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            VideoProcessor.FFprobeExePath,
            ["-show_format", "-show_streams", "-print_format", "json", "-v", "error", "-hide_banner", "-i", filePath.PathExport],
            lifetime: ProcessLifetime.ShortLived,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var dto = JsonSerializer.Deserialize(json, FFprobeJsonContext.Default.FFprobeOutputData)
            ?? throw new InvalidOperationException("ffprobe returned empty JSON output.");

        var streamDtos = dto.Streams ?? [];
        var builder = ImmutableArray.CreateBuilder<StreamInfo>(streamDtos.Count);
        foreach (var s in streamDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(ConvertStream(s));
        }

        return new VideoFileInfo(dto.Format?.FormatName ?? string.Empty, dto.Format?.Duration, builder.DrainToImmutable());
    }

    private static StreamInfo ConvertStream(FFprobeStreamData s)
    {
        var d = s.Disposition;
        var t = s.Tags;

        bool attachedPic = (d?.AttachedPic ?? 0) != 0;
        bool timedThumbnails = (d?.TimedThumbnails ?? 0) != 0;
        string? language = t?.Language;

        switch (s.CodecType)
        {
            case "video":
                (int fpsNum, int fpsDen) = ParseFraction(s.RFrameRate, '/', defaultIfMissing: (0, 0));
                (int sarNum, int sarDen) = ParseFraction(s.SampleAspectRatio, ':', defaultIfMissing: (1, 1));

                var displayMatrix = s.SideDataList?.FirstOrDefault((sd) => sd?.SideDataType == "Display Matrix");
                int rotation = (displayMatrix?.Rotation ?? 0) % 360;

                if (rotation <= -180)
                    rotation += 360;
                else if (rotation > 180)
                    rotation -= 360;

                return new VideoStreamInfo(
                    s.CodecName!,
                    s.CodecTagString!,
                    s.Profile,
                    language,
                    attachedPic,
                    timedThumbnails,
                    (d?.StillImage ?? 0) != 0,
                    (d?.Default ?? 0) != 0,
                    d?.IsBadThumbnailCandidate ?? false,
                    s.Width ?? -1,
                    s.Height ?? -1,
                    s.Duration,
                    fpsNum,
                    fpsDen,
                    sarNum,
                    sarDen,
                    s.PixFmt,
                    s.ColorRange,
                    s.ColorSpace,
                    s.ColorTransfer,
                    s.ColorPrimaries,
                    s.FieldOrder,
                    s.BitsPerRawSample ?? -1,
                    t?.IsAlphaMode ?? false,
                    rotation,
                    HasNonStandardDisplayMatrix(displayMatrix, rotation));

            case "audio":
                return new AudioStreamInfo(s.CodecName!, s.Profile, language, s.Duration, s.Channels ?? -1, s.SampleRate, s.ChannelLayout);

            case "subtitle":
                return new SubtitleStreamInfo(s.CodecName!, language, t?.Title);

            default:
                char codecChar = s.CodecType switch
                {
                    "data" => 'd',
                    "attachment" => 't',
                    _ => '\0',
                };
                return new UnrecognizedStreamInfo(s.CodecType!, s.CodecName, language, codecChar, attachedPic, timedThumbnails);
        }
    }

    private static (int Num, int Den) ParseFraction(string? value, char separator, (int Num, int Den) defaultIfMissing)
    {
        if (value is null)
            return defaultIfMissing;

        int idx = value.IndexOf(separator);
        if (idx > 0 &&
            int.TryParse(value.AsSpan(0, idx), CultureInfo.InvariantCulture, out int num) &&
            int.TryParse(value.AsSpan(idx + 1), CultureInfo.InvariantCulture, out int den) &&
            num > 0 &&
            den > 0)
        {
            return (num, den);
        }

        return (-1, -1);
    }

    private static bool HasNonStandardDisplayMatrix(FFprobeSideData? displayMatrix, int rotation)
    {
        if (displayMatrix is null)
            return false;

        // The display matrix string is formatted as rows of '0000000N:' labels (which fail to parse and are skipped) followed by three values each, giving
        // [a, b, u, c, d, v, x, y, w]. The rotation/scale values are 16.16 fixed point (65536 = 1) and w is 2.30 fixed point (1073741824 = 1).

        Span<long> values = [0, 0, 0, 0, 0, 0, 0, 0, 0];
        int idx = 0;

        foreach (string token in (displayMatrix.DisplayMatrix ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(token, CultureInfo.InvariantCulture, out long value))
            {
                if (idx >= 9)
                    return true;

                values[idx++] = value;
            }
        }

        if (idx != 9)
            return true;

        // Zero out the translation entries ([x, y]) since they are legitimately used in combination with standard rotations:
        values[6] = 0;
        values[7] = 0;

        // Compare against the standard matrix that ffmpeg writes for the reported rotation - anything else (e.g. flips / mirroring, scaling, or
        // non-quarter-turn rotations) is non-standard.
        ReadOnlySpan<long> expected = rotation switch
        {
            0 => [65536, 0, 0, 0, 65536, 0, 0, 0, 1073741824],
            90 => [0, -65536, 0, 65536, 0, 0, 0, 0, 1073741824],
            180 => [-65536, 0, 0, 0, -65536, 0, 0, 0, 1073741824],
            -90 => [0, 65536, 0, -65536, 0, 0, 0, 0, 1073741824],
            _ => default,
        };

        return !values.SequenceEqual(expected);
    }

    public struct ConfigurationInfo
    {
        // Library-specific encoder support
        public bool SupportsLibX264Encoder { get; set; }
        public bool SupportsLibX265Encoder { get; set; }
        public bool SupportsPngEncoder { get; set; }
        public bool SupportsLibFDKAACEncoder { get; set; }
        public bool SupportsAACEncoder { get; set; }
        public bool SupportsMovTextEncoder { get; set; }
        public bool SupportsDvdSubEncoder { get; set; }

        // Video codec decoder support
        public bool SupportsMpeg1VideoDecoder { get; set; }
        public bool SupportsMpeg2VideoDecoder { get; set; }
        public bool SupportsMpeg4Decoder { get; set; }
        public bool SupportsH263Decoder { get; set; }
        public bool SupportsH264Decoder { get; set; }
        public bool SupportsHEVCDecoder { get; set; }
        public bool SupportsVVCDecoder { get; set; }
        public bool SupportsVP8Decoder { get; set; }
        public bool SupportsVP9Decoder { get; set; }
        public bool SupportsAV1Decoder { get; set; }
        public bool SupportsLibDav1dDecoder { get; set; }
        public bool SupportsLibVpxDecoder { get; set; }
        public bool SupportsLibVpxVp9Decoder { get; set; }

        // Audio codec decoder support
        public bool SupportsAACDecoder { get; set; }
        public bool SupportsMP2Decoder { get; set; }
        public bool SupportsMP3Decoder { get; set; }
        public bool SupportsVorbisDecoder { get; set; }
        public bool SupportsOpusDecoder { get; set; }
        public bool SupportsAmrNbDecoder { get; set; }
        public bool SupportsAmrWbDecoder { get; set; }

        // Muxing support
        public bool SupportsMP4Muxing { get; set; }

        // Demuxing support
        public bool SupportsMovGroupDemuxing { get; set; }
        public bool SupportsMatroskaGroupDemuxing { get; set; }
        public bool SupportsAviDemuxing { get; set; }
        public bool SupportsMpegTSGroupDemuxing { get; set; }
        public bool SupportsMpegDemuxing { get; set; }

        // Filter support
        public bool SupportsZscaleFilter { get; set; }
        public bool SupportsScaleFilter { get; set; }
        public bool SupportsFpsFilter { get; set; }
        public bool SupportsTonemapFilter { get; set; }
        public bool SupportsFormatFilter { get; set; }
        public bool SupportsBwdifFilter { get; set; }
        public bool SupportsSetsarFilter { get; set; }
        public bool SupportsTransposeFilter { get; set; }
        public bool SupportsHFlipFilter { get; set; }
        public bool SupportsVFlipFilter { get; set; }
        public bool SupportsSidedataFilter { get; set; }
        public bool SupportsScaleVtFilter { get; set; }
        public bool SupportsScaleCudaFilter { get; set; }
        public bool SupportsBwdifCudaFilter { get; set; }
        public bool SupportsColorspaceCudaFilter { get; set; }
        public bool SupportsVppQsvFilter { get; set; }
        public bool SupportsVppAmfFilter { get; set; }
        public bool SupportsScaleD3D12Filter { get; set; }
        public bool SupportsDeinterlaceD3D12Filter { get; set; }
        public bool SupportsTransposeVtFilter { get; set; }
        public bool SupportsTransposeCudaFilter { get; set; }

        // Hardware acceleration support
        public bool SupportsVideoToolboxHWAccel { get; set; }
        public bool SupportsCudaHWAccel { get; set; }
        public bool SupportsQsvHWAccel { get; set; }
        public bool SupportsAmfHWAccel { get; set; }
        public bool SupportsD3D12VAHWAccel { get; set; }

        // Pixel format support
        public bool SupportsVideoToolboxVLDPixelFormat { get; set; }
        public bool SupportsCudaPixelFormat { get; set; }
        public bool SupportsQsvPixelFormat { get; set; }
        public bool SupportsAmfPixelFormat { get; set; }
        public bool SupportsD3D12PixelFormat { get; set; }
    }

    private static IEnumerable<(string Info, string Name)> RunFFprobeConfigurationExtraction(
        string command,
        bool noStartingLine,
        bool nameOnly = false,
        bool useFfmpegExe = false)
    {
        // We only support nameOnly in noStartingLine mode.
        if (nameOnly && !noStartingLine)
            throw new ArgumentException("nameOnly can only be used in noStartingLine mode.", nameof(nameOnly));

        // Get the raw configuration output from ffprobe.
        string result = ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            useFfmpegExe ? VideoProcessor.FFmpegExePath : VideoProcessor.FFprobeExePath,
            [command, "-hide_banner", "-v", "error"],
            lifetime: ProcessLifetime.ShortLived,
            cancellationToken: CancellationToken.None,
            runAsynchronously: false).GetAwaiter().GetResult();

        // Handle skipping the starting line if needed.
        using var lineReader = new StringReader(result);
        string line;
        if (!noStartingLine)
        {
            // Skip to the line formatted as '<space*>-----<space*>' with some number of dashes.
            // We will use the number of dashes to determine the length of the configuration info section, and the number of preceding spaces.
            int removeStart = -1;
            int configLength = -1;
            while ((line = lineReader.ReadLine()) != null)
            {
                var sp = line.AsSpan().Trim(' ');
                if (sp.Length > 0 && !sp.ContainsAnyExcept('-'))
                {
                    removeStart = line.AsSpan().IndexOf('-');
                    configLength = sp.Length;
                    break;
                }
            }

            // Handle the case where we could not find the configuration info.
            if (removeStart < 0)
            {
                throw new InvalidOperationException(
                    $"Could not find ffprobe configuration info section for command '{command}' - output was missing or in an unexpected format.")
                    {
                        Data =
                        {
                            ["Command"] = command,
                            ["Output"] = result,
                        },
                    };
            }

            // Now, enumerate through each line in the configuration info section and return them to the caller.
            while ((line = lineReader.ReadLine()) != null)
            {
                var sp = line.AsSpan();
                if (sp.Length == 0) continue;
                var info = sp.Slice(removeStart, configLength);
                var name = sp[(removeStart + configLength)..].TrimStart(' ');
                int spIdx = name.IndexOf(' ');
                if (spIdx >= 0) name = name[..spIdx];
                yield return (info.ToString(), name.ToString());
            }
        }
        else if (nameOnly)
        {
            // Enumerate through each line in the configuration info section and return only the names to the caller.
            while ((line = lineReader.ReadLine()) != null)
            {
                var sp = line.AsSpan().Trim(' ');
                if (sp.Length == 0) continue;
                yield return (string.Empty, sp.ToString());
            }
        }
        else
        {
            // Enumerate through each line in the configuration info section and return them to the caller.
            while ((line = lineReader.ReadLine()) != null)
            {
                var sp = line.AsSpan().TrimStart(' ');
                if (sp.Length == 0) continue;
                int idx = sp.IndexOf(' ');
                if (idx < 0) continue;
                var info = sp[..idx];
                var name = sp[idx..].TrimStart(' ');
                if (name.Length == 0) continue;
                idx = name.IndexOf(' ');
                if (idx >= 0) name = name[..idx];
                yield return (info.ToString(), name.ToString());
            }
        }
    }

    private static bool CheckHWAccelActuallySupported(string mode)
    {
        // Creating a device proves very little, since it does not touch the video decoding hardware at all: videotoolbox always succeeds (its device creation
        // is a no-op), d3d12va succeeds against a software adapter, and amf only checks that the runtime library loads. The remaining modes need a real driver
        // to create a device, but still do not establish that the decoding engine supports what we throw at it. We assume that any useful hardware accelerator
        // will support decoding H.264 at 128x128 resolution. The frames are downloaded back out of hardware and hashed so that the accelerator has to actually
        // produce pixel data instead of merely accepting the stream.

        string hwAccelOutputFormat = FFmpegUtils.MapHWAccelNameToFormatName(mode);
        var testFile = FilePath.CreateTempFile();

        try
        {
            var (_, _, encodeReturnCode) = ProcessUtils.RunProcessToStringAsync(
                VideoProcessor.FFmpegExePath,
                [
                    "-hide_banner", "-f", "lavfi", "-r", "10", "-i", "testsrc2=s=128x128", "-frames:v", "10", "-c:v", "libx264",
                    "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-f", "h264", "-y", testFile.PathExport,
                ],
                lifetime: ProcessLifetime.ShortLived,
                cancellationToken: CancellationToken.None,
                runAsynchronously: false).GetAwaiter().GetResult();

            Debug.Assert(encodeReturnCode == 0);

            if (encodeReturnCode != 0)
                return false;

            var (_, _, decodeReturnCode) = ProcessUtils.RunProcessToStringAsync(
                VideoProcessor.FFmpegExePath,
                [
                    "-hide_banner", "-hwaccel", mode, "-hwaccel_output_format", hwAccelOutputFormat, "-f", "h264", "-i",
                    testFile.PathExport, "-filter:v", "hwdownload,format=nv12", "-f", "framemd5", "-",
                ],
                lifetime: ProcessLifetime.ShortLived,
                cancellationToken: CancellationToken.None,
                runAsynchronously: false).GetAwaiter().GetResult();

            return decodeReturnCode == 0;
        }
        finally
        {
            try
            {
                testFile.Delete(ignoreNotFound: true);
            }
            catch
            {
                // Not worth failing over.
            }
        }
    }

    private static void EnsureConfigurationInfoInitialized()
    {
        // Fast path: the volatile read acquires the fully published struct.
        if (_configInfoInitialized) return;

        lock (_configInitLock)
        {
            // Double-checked: a concurrent caller may have published while this one waited for the lock.
            if (_configInfoInitialized) return;

            // The struct is built privately and published in one step (before the volatile flag write), so a caller holding a ref to
            // _configInfo can never observe a partially populated value. Previously two overlapping initializers could reset the shared
            // struct in place while other threads (that had already seen the flag) were reading it. If probing throws, nothing has been
            // published and the next caller simply retries. This assumes that the user doesn't swap out their ffprobe binary while we're
            // running, but we're already assuming this in many spots.
            _configInfo = BuildConfigurationInfo();
            _configInfoInitialized = true;
        }
    }

    private static ConfigurationInfo BuildConfigurationInfo()
    {
        var config = default(ConfigurationInfo);

        // Initialize encoders
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-encoders", noStartingLine: false))
        {
            switch (name)
            {
                case "libx264" when info is ['V', ..]: config.SupportsLibX264Encoder = true; break;
                case "libx265" when info is ['V', ..]: config.SupportsLibX265Encoder = true; break;
                case "png" when info is ['V', ..]: config.SupportsPngEncoder = true; break;
                case "libfdk_aac" when info is ['A', ..]: config.SupportsLibFDKAACEncoder = true; break;
                case "aac" when info is ['A', ..]: config.SupportsAACEncoder = true; break;
                case "mov_text" when info is ['S', ..]: config.SupportsMovTextEncoder = true; break;
                case "dvdsub" when info is ['S', ..]: config.SupportsDvdSubEncoder = true; break;
            }
        }

        // Initialize codecs
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-codecs", noStartingLine: false))
        {
            switch (name)
            {
                // Video decoders
                case "mpeg1video" when info is ['D', _, 'V', ..]: config.SupportsMpeg1VideoDecoder = true; break;
                case "mpeg2video" when info is ['D', _, 'V', ..]: config.SupportsMpeg2VideoDecoder = true; break;
                case "mpeg4" when info is ['D', _, 'V', ..]: config.SupportsMpeg4Decoder = true; break;
                case "h263" when info is ['D', _, 'V', ..]: config.SupportsH263Decoder = true; break;
                case "h264" when info is ['D', _, 'V', ..]: config.SupportsH264Decoder = true; break;
                case "hevc" when info is ['D', _, 'V', ..]: config.SupportsHEVCDecoder = true; break;
                case "vvc" when info is ['D', _, 'V', ..]: config.SupportsVVCDecoder = true; break;
                case "vp8" when info is ['D', _, 'V', ..]: config.SupportsVP8Decoder = true; break;
                case "vp9" when info is ['D', _, 'V', ..]: config.SupportsVP9Decoder = true; break;
                case "av1" when info is ['D', _, 'V', ..]: config.SupportsAV1Decoder = true; break;

                // Audio decoders
                case "aac" when info is ['D', _, 'A', ..]: config.SupportsAACDecoder = true; break;
                case "mp2" when info is ['D', _, 'A', ..]: config.SupportsMP2Decoder = true; break;
                case "mp3" when info is ['D', _, 'A', ..]: config.SupportsMP3Decoder = true; break;
                case "vorbis" when info is ['D', _, 'A', ..]: config.SupportsVorbisDecoder = true; break;
                case "opus" when info is ['D', _, 'A', ..]: config.SupportsOpusDecoder = true; break;
                case "amr_nb" when info is ['D', _, 'A', ..]: config.SupportsAmrNbDecoder = true; break;
                case "amr_wb" when info is ['D', _, 'A', ..]: config.SupportsAmrWbDecoder = true; break;
            }
        }

        // Initialize decoders
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-decoders", noStartingLine: false))
        {
            switch (name)
            {
                case "libdav1d" when info is ['V', ..]: config.SupportsLibDav1dDecoder = true; break;
                case "libvpx" when info is ['V', ..]: config.SupportsLibVpxDecoder = true; break;
                case "libvpx-vp9" when info is ['V', ..]: config.SupportsLibVpxVp9Decoder = true; break;
            }
        }

        // Initialize muxing support
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-muxers", noStartingLine: false))
        {
            switch (name)
            {
                case "mp4" when info is [_, 'E', ..]: config.SupportsMP4Muxing = true; break;
            }
        }

        // Initialize demuxing support
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-demuxers", noStartingLine: false))
        {
            switch (name)
            {
                case { } when info is ['D', ..]:
                    foreach (var fmt in name.AsSpan().Split(','))
                    {
                        switch (name.AsSpan()[fmt])
                        {
                            case "mov": config.SupportsMovGroupDemuxing = true; break;
                            case "matroska": config.SupportsMatroskaGroupDemuxing = true; break;
                            case "avi": config.SupportsAviDemuxing = true; break;
                            case "mpegts": config.SupportsMpegTSGroupDemuxing = true; break;
                            case "mpeg": config.SupportsMpegDemuxing = true; break;
                        }
                    }

                    break;
            }
        }

        // Initialize filter support
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-filters", noStartingLine: true))
        {
            switch (name)
            {
                case "zscale": config.SupportsZscaleFilter = true; break;
                case "scale": config.SupportsScaleFilter = true; break;
                case "fps": config.SupportsFpsFilter = true; break;
                case "tonemap": config.SupportsTonemapFilter = true; break;
                case "format": config.SupportsFormatFilter = true; break;
                case "bwdif": config.SupportsBwdifFilter = true; break;
                case "setsar": config.SupportsSetsarFilter = true; break;
                case "transpose": config.SupportsTransposeFilter = true; break;
                case "hflip": config.SupportsHFlipFilter = true; break;
                case "vflip": config.SupportsVFlipFilter = true; break;
                case "sidedata": config.SupportsSidedataFilter = true; break;
                case "scale_vt": config.SupportsScaleVtFilter = true; break;
                case "scale_cuda": config.SupportsScaleCudaFilter = true; break;
                case "bwdif_cuda": config.SupportsBwdifCudaFilter = true; break;
                case "colorspace_cuda": config.SupportsColorspaceCudaFilter = true; break;
                case "vpp_qsv": config.SupportsVppQsvFilter = true; break;
                case "vpp_amf": config.SupportsVppAmfFilter = true; break;
                case "scale_d3d12": config.SupportsScaleD3D12Filter = true; break;
                case "deinterlace_d3d12": config.SupportsDeinterlaceD3D12Filter = true; break;
                case "transpose_vt": config.SupportsTransposeVtFilter = true; break;
                case "transpose_cuda": config.SupportsTransposeCudaFilter = true; break;
            }
        }

        // Initialize pixel format support
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-pix_fmts", noStartingLine: false))
        {
            if (info is [_, _, 'H', ..])
            {
                switch (name)
                {
                    case "videotoolbox_vld": config.SupportsVideoToolboxVLDPixelFormat = true; break;
                    case "cuda": config.SupportsCudaPixelFormat = true; break;
                    case "qsv": config.SupportsQsvPixelFormat = true; break;
                    case "amf": config.SupportsAmfPixelFormat = true; break;
                    case "d3d12": config.SupportsD3D12PixelFormat = true; break;
                }
            }
        }

        // Initialize hardware acceleration support (note: command output also includes a 'Hardware acceleration methods:' line, and empty line after)
        // Note: it being listed in '-hwaccels' only means that ffmpeg was built with support for it, not that it is actually usable on the current system.
        // Note: the 'auto' forced mode (used for testing) runs the same detection as normal builds, since it tests automatic hardware acceleration selection.
#if !CUSTOM_HWACCEL_MODE || CUSTOM_HWACCEL_MODE_AUTO
        foreach (var (info, name) in RunFFprobeConfigurationExtraction("-hwaccels", noStartingLine: true, nameOnly: true, useFfmpegExe: true))
        {
            switch (name)
            {
                case "videotoolbox": config.SupportsVideoToolboxHWAccel = config.SupportsVideoToolboxVLDPixelFormat && CheckHWAccelActuallySupported(name); break;
                case "cuda": config.SupportsCudaHWAccel = config.SupportsCudaPixelFormat && CheckHWAccelActuallySupported(name); break;
                case "qsv": config.SupportsQsvHWAccel = config.SupportsQsvPixelFormat && CheckHWAccelActuallySupported(name); break;
                case "amf": config.SupportsAmfHWAccel = config.SupportsAmfPixelFormat && CheckHWAccelActuallySupported(name); break;
                case "d3d12va": config.SupportsD3D12VAHWAccel = config.SupportsD3D12PixelFormat && CheckHWAccelActuallySupported(name); break;
            }
        }

        // Special handling for forced hardware acceleration mode (used for testing):
#elif !CUSTOM_HWACCEL_MODE_DECODEONLY
#if CUSTOM_HWACCEL_MODE_VIDEOTOOLBOX
        string mode = "videotoolbox";
        config.SupportsVideoToolboxHWAccel = true;
        bool pixFmtSupported = config.SupportsVideoToolboxVLDPixelFormat;
        bool scaleFilterSupported = config.SupportsScaleVtFilter;
#elif CUSTOM_HWACCEL_MODE_CUDA
        string mode = "cuda";
        config.SupportsCudaHWAccel = true;
        bool pixFmtSupported = config.SupportsCudaPixelFormat;
        bool scaleFilterSupported = config.SupportsScaleCudaFilter;
#elif CUSTOM_HWACCEL_MODE_QSV
        string mode = "qsv";
        config.SupportsQsvHWAccel = true;
        bool pixFmtSupported = config.SupportsQsvPixelFormat;
        bool scaleFilterSupported = config.SupportsVppQsvFilter;
#elif CUSTOM_HWACCEL_MODE_AMF
        string mode = "amf";
        config.SupportsAmfHWAccel = true;
        bool pixFmtSupported = config.SupportsAmfPixelFormat;
        bool scaleFilterSupported = config.SupportsVppAmfFilter;
#elif CUSTOM_HWACCEL_MODE_D3D12VA
        string mode = "d3d12va";
        config.SupportsD3D12VAHWAccel = true;
        bool pixFmtSupported = config.SupportsD3D12PixelFormat;
        bool scaleFilterSupported = config.SupportsScaleD3D12Filter;
#else
#error Unrecognized CUSTOM_HWACCEL_MODE* value.
#endif

        if (!RunFFprobeConfigurationExtraction("-hwaccels", noStartingLine: true, nameOnly: true, useFfmpegExe: true).Any((x) => x.Name == mode))
            throw new InvalidOperationException($"The configured ffmpeg build does not support the forced hardware acceleration mode '{mode}'.");

        if (!CheckHWAccelActuallySupported(mode))
            throw new InvalidOperationException($"The system does not actually support the forced hardware acceleration mode '{mode}'.");

        if (!pixFmtSupported)
            throw new InvalidOperationException($"The configured ffmpeg build does not support the pixel format required for the forced hardware acceleration mode '{mode}'.");

        if (!scaleFilterSupported)
            throw new InvalidOperationException($"The configured ffmpeg build does not support the scale filter required for the forced hardware acceleration mode '{mode}'.");
#endif

        // Ensure we only mark it as initialized after (with a volatile write) we're certain the struct is fully initialized by using a volatile write.

        return config;
    }
}

internal sealed record FFprobeOutputData(FFprobeFormatData? Format, List<FFprobeStreamData>? Streams);

internal sealed record FFprobeFormatData(string? FormatName, double? Duration);

internal sealed record FFprobeStreamData(
    string? CodecName,
    string? CodecType,
    string? CodecTagString,
    string? Profile,
    int? Width,
    int? Height,
    [property: JsonPropertyName("r_frame_rate")] string? RFrameRate,
    double? Duration,
    string? PixFmt,
    string? ColorRange,
    string? ColorSpace,
    string? ColorTransfer,
    string? ColorPrimaries,
    int? BitsPerRawSample,
    int? Channels,
    int? SampleRate,
    string? SampleAspectRatio,
    string? FieldOrder,
    string? ChannelLayout,
    FFprobeDispositionData? Disposition,
    FFprobeTagsData? Tags,
    FFprobeSideData?[]? SideDataList);

internal sealed record FFprobeDispositionData(
    int? AttachedPic,
    int? TimedThumbnails,
    int? StillImage,
    int? Default,
    int? Dub,
    int? Comment,
    int? Lyrics,
    int? Karaoke,
    int? Forced,
    int? HearingImpaired,
    int? VisualImpaired,
    int? CleanEffects,
    int? NonDiegetic,
    int? Captions,
    int? Descriptions,
    int? Metadata,
    int? Dependent,
    int? Multilayer)
{
    /// <summary>Gets a value indicating whether this stream's disposition flags mark it as a poor thumbnail candidate.</summary>
    [JsonIgnore]
    public bool IsBadThumbnailCandidate =>
        Dub is 1 || Comment is 1 || Lyrics is 1 || Karaoke is 1 || Forced is 1 || HearingImpaired is 1 || VisualImpaired is 1 || CleanEffects is 1 ||
        NonDiegetic is 1 || Captions is 1 || Descriptions is 1 || Metadata is 1 || Dependent is 1 || Multilayer is 1;
}

internal sealed record FFprobeTagsData(string? Language, string? Title, string? AlphaMode)
{
    /// <summary>Gets a value indicating whether the alpha_mode tag is set (value "1"); ffprobe emits all tag values as strings.</summary>
    [JsonIgnore]
    public bool IsAlphaMode => AlphaMode == "1";
}

internal sealed record FFprobeSideData(string? SideDataType, int? Rotation, [property: JsonPropertyName("displaymatrix")] string? DisplayMatrix);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FFprobeOutputData))]
internal sealed partial class FFprobeJsonContext : JsonSerializerContext;
