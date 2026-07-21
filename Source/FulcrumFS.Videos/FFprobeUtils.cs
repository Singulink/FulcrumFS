using System.Collections.Immutable;
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
        int Rotation)
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
                    s.SideDataList?.FirstOrDefault((sd) => sd?.SideDataType == "Display Matrix")?.Rotation ?? 0);

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
    }

    private static IEnumerable<(string Info, string Name)> RunFFprobeConfigurationExtraction(
        string command,
        bool noStartingLine,
        CancellationToken cancellationToken = default)
    {
        // Get the raw configuration output from ffprobe.
        string result = ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            VideoProcessor.FFprobeExePath,
            [command, "-hide_banner", "-v", "error"],
            lifetime: ProcessLifetime.ShortLived,
            cancellationToken: cancellationToken,
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

    private static void EnsureConfigurationInfoInitialized()
    {
        // Note: the volatile read here + write after we set all fields ensures that all threads see a fully initialized struct.
        // This assumes that the user doesn't swap out their ffprobe binary while we're running, but we're already assuming this in many spots.
        if (_configInfoInitialized) return;

        // Initialize the configuration info struct.
        InitImpl();
        static void InitImpl()
        {
            // Initialize encoders
            foreach (var (info, name) in RunFFprobeConfigurationExtraction("-encoders", noStartingLine: false))
            {
                switch (name)
                {
                    case "libx264" when info is ['V', ..]: _configInfo.SupportsLibX264Encoder = true; break;
                    case "libx265" when info is ['V', ..]: _configInfo.SupportsLibX265Encoder = true; break;
                    case "png" when info is ['V', ..]: _configInfo.SupportsPngEncoder = true; break;
                    case "libfdk_aac" when info is ['A', ..]: _configInfo.SupportsLibFDKAACEncoder = true; break;
                    case "aac" when info is ['A', ..]: _configInfo.SupportsAACEncoder = true; break;
                    case "mov_text" when info is ['S', ..]: _configInfo.SupportsMovTextEncoder = true; break;
                    case "dvdsub" when info is ['S', ..]: _configInfo.SupportsDvdSubEncoder = true; break;
                }
            }

            // Initialize codecs
            foreach (var (info, name) in RunFFprobeConfigurationExtraction("-codecs", noStartingLine: false))
            {
                switch (name)
                {
                    // Video decoders
                    case "mpeg1video" when info is ['D', _, 'V', ..]: _configInfo.SupportsMpeg1VideoDecoder = true; break;
                    case "mpeg2video" when info is ['D', _, 'V', ..]: _configInfo.SupportsMpeg2VideoDecoder = true; break;
                    case "mpeg4" when info is ['D', _, 'V', ..]: _configInfo.SupportsMpeg4Decoder = true; break;
                    case "h263" when info is ['D', _, 'V', ..]: _configInfo.SupportsH263Decoder = true; break;
                    case "h264" when info is ['D', _, 'V', ..]: _configInfo.SupportsH264Decoder = true; break;
                    case "hevc" when info is ['D', _, 'V', ..]: _configInfo.SupportsHEVCDecoder = true; break;
                    case "vvc" when info is ['D', _, 'V', ..]: _configInfo.SupportsVVCDecoder = true; break;
                    case "vp8" when info is ['D', _, 'V', ..]: _configInfo.SupportsVP8Decoder = true; break;
                    case "vp9" when info is ['D', _, 'V', ..]: _configInfo.SupportsVP9Decoder = true; break;
                    case "av1" when info is ['D', _, 'V', ..]: _configInfo.SupportsAV1Decoder = true; break;

                    // Audio decoders
                    case "aac" when info is ['D', _, 'A', ..]: _configInfo.SupportsAACDecoder = true; break;
                    case "mp2" when info is ['D', _, 'A', ..]: _configInfo.SupportsMP2Decoder = true; break;
                    case "mp3" when info is ['D', _, 'A', ..]: _configInfo.SupportsMP3Decoder = true; break;
                    case "vorbis" when info is ['D', _, 'A', ..]: _configInfo.SupportsVorbisDecoder = true; break;
                    case "opus" when info is ['D', _, 'A', ..]: _configInfo.SupportsOpusDecoder = true; break;
                }
            }

            // Initialize decoders
            foreach (var (info, name) in RunFFprobeConfigurationExtraction("-decoders", noStartingLine: false))
            {
                switch (name)
                {
                    case "libdav1d" when info is ['V', ..]: _configInfo.SupportsLibDav1dDecoder = true; break;
                    case "libvpx" when info is ['V', ..]: _configInfo.SupportsLibVpxDecoder = true; break;
                    case "libvpx-vp9" when info is ['V', ..]: _configInfo.SupportsLibVpxVp9Decoder = true; break;
                }
            }

            // Initialize muxing support
            foreach (var (info, name) in RunFFprobeConfigurationExtraction("-muxers", noStartingLine: false))
            {
                switch (name)
                {
                    case "mp4" when info is [_, 'E', ..]: _configInfo.SupportsMP4Muxing = true; break;
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
                                case "mov": _configInfo.SupportsMovGroupDemuxing = true; break;
                                case "matroska": _configInfo.SupportsMatroskaGroupDemuxing = true; break;
                                case "avi": _configInfo.SupportsAviDemuxing = true; break;
                                case "mpegts": _configInfo.SupportsMpegTSGroupDemuxing = true; break;
                                case "mpeg": _configInfo.SupportsMpegDemuxing = true; break;
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
                    case "zscale": _configInfo.SupportsZscaleFilter = true; break;
                    case "scale": _configInfo.SupportsScaleFilter = true; break;
                    case "fps": _configInfo.SupportsFpsFilter = true; break;
                    case "tonemap": _configInfo.SupportsTonemapFilter = true; break;
                    case "format": _configInfo.SupportsFormatFilter = true; break;
                    case "bwdif": _configInfo.SupportsBwdifFilter = true; break;
                    case "setsar": _configInfo.SupportsSetsarFilter = true; break;
                }
            }

            // Ensure we only mark it as initialized after (with a volatile write) we're certain the struct is fully initialized by using a volatile write.
            _configInfoInitialized = true;
        }
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

internal sealed record FFprobeSideData(string? SideDataType, int? Rotation);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FFprobeOutputData))]
internal sealed partial class FFprobeJsonContext : JsonSerializerContext;
