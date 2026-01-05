using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Singulink.IO;

namespace FulcrumFS.Videos;

/// <summary>
/// Provides utility methods for using ffprobe (for video file analysis, and extracting ffmpeg configuration info).
/// </summary>
internal static class FFprobeUtils
{
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
        bool AlphaMode)
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

    private static int? ReadInt32Property(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var propElement) &&
            propElement.ValueKind == JsonValueKind.Number &&
            propElement.TryGetInt32(out int value))
        {
            return value;
        }

        return null;
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var propElement) && propElement.ValueKind == JsonValueKind.String)
        {
            return propElement.GetString();
        }

        return null;
    }

    private static double? ReadStringPropertyAsDouble(JsonElement element, string propertyName)
    {
        string? strValue = ReadStringProperty(element, propertyName);
        return (strValue != null && double.TryParse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) ? value : null;
    }

    private static int? ReadStringPropertyAsInt32(JsonElement element, string propertyName)
    {
        string? strValue = ReadStringProperty(element, propertyName);
        return (strValue != null && int.TryParse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) ? value : null;
    }

    public static async Task<VideoFileInfo> GetVideoFileAsync(IAbsoluteFilePath filePath, CancellationToken cancellationToken = default)
    {
        // Get the ffprobe JSON output for the file:
        string json = await ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            VideoProcessor.FFprobeExePath,
            ["-show_format", "-show_streams", "-print_format", "json", "-v", "error", "-hide_banner", "-i", filePath.PathExport],
            isShortLived: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Parse the JSON output:
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json);
        var (streams, streamsCount) = document.RootElement.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array
            ? (streamsElement.EnumerateArray(), streamsElement.GetArrayLength())
            : (default, 0);
        cancellationToken.ThrowIfCancellationRequested();

        // Read each stream's info:
        double? duration;
        var builder = ImmutableArray.CreateBuilder<StreamInfo>(streamsCount);
        foreach (var stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? codecName = ReadStringProperty(stream, "codec_name");
            string? codecType = ReadStringProperty(stream, "codec_type");
            string? codecTagString = ReadStringProperty(stream, "codec_tag_string");
            string? profile = ReadStringProperty(stream, "profile");
            int width = ReadInt32Property(stream, "width") ?? -1;
            int height = ReadInt32Property(stream, "height") ?? -1;
            string? rFrameRate = ReadStringProperty(stream, "r_frame_rate");
            duration = ReadStringPropertyAsDouble(stream, "duration");
            string? pixelFormat = ReadStringProperty(stream, "pix_fmt");
            string? colorRange = ReadStringProperty(stream, "color_range");
            string? colorSpace = ReadStringProperty(stream, "color_space");
            string? colorTransfer = ReadStringProperty(stream, "color_transfer");
            string? colorPrimaries = ReadStringProperty(stream, "color_primaries");
            int bitsPerSample = ReadStringPropertyAsInt32(stream, "bits_per_raw_sample") ?? -1;
            int channels = ReadInt32Property(stream, "channels") ?? -1;
            int? sampleRate = ReadStringPropertyAsInt32(stream, "sample_rate");
            string? sar = ReadStringProperty(stream, "sample_aspect_ratio");
            string? fieldOrder = ReadStringProperty(stream, "field_order");
            string? channelLayout = ReadStringProperty(stream, "channel_layout");

            int attachedPicValue = 0, timedThumbnailsValue = 0, stillImageValue = 0, defaultStreamValue = 0, badCandidateForThumbnailValue = 0;
            if (stream.TryGetProperty("disposition", out var dispositionsProp) && dispositionsProp.ValueKind == JsonValueKind.Object)
            {
                attachedPicValue = ReadInt32Property(dispositionsProp, "attached_pic") ?? 0;
                timedThumbnailsValue = ReadInt32Property(dispositionsProp, "timed_thumbnails") ?? 0;
                stillImageValue = ReadInt32Property(dispositionsProp, "still_image") ?? 0;
                defaultStreamValue = ReadInt32Property(dispositionsProp, "default") ?? 0;
                badCandidateForThumbnailValue =
                    (ReadInt32Property(dispositionsProp, "dub") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "comment") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "lyrics") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "karaoke") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "forced") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "hearing_impaired") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "visual_impaired") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "clean_effects") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "non_diegetic") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "captions") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "descriptions") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "metadata") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "dependent") ?? 0) |
                    (ReadInt32Property(dispositionsProp, "multilayer") ?? 0);
            }

            string? language = null, title = null, alphaMode = null;
            if (stream.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Object)
            {
                language = ReadStringProperty(tagsProp, "language");
                title = ReadStringProperty(tagsProp, "title");
                alphaMode = ReadStringProperty(tagsProp, "alpha_mode");
            }

            switch (codecType)
            {
                case "video":
                    int fpsNum = 0, fpsDen = 0;
                    if (rFrameRate != null)
                    {
                        int idx = rFrameRate.IndexOf('/');
                        if (idx > 0 &&
                            int.TryParse(rFrameRate.AsSpan(0, idx), CultureInfo.InvariantCulture, out int num) &&
                            int.TryParse(rFrameRate.AsSpan(idx + 1), CultureInfo.InvariantCulture, out int den) &&
                            num > 0 &&
                            den > 0)
                        {
                            fpsNum = num;
                            fpsDen = den;
                        }
                    }

                    int sarNum = 1, sarDen = 1;
                    if (sar != null)
                    {
                        sarNum = -1;
                        sarDen = -1;
                        int idx = sar.IndexOf(':');
                        if (idx > 0 &&
                            int.TryParse(sar.AsSpan(0, idx), CultureInfo.InvariantCulture, out int num) &&
                            int.TryParse(sar.AsSpan(idx + 1), CultureInfo.InvariantCulture, out int den) &&
                            num > 0 &&
                            den > 0)
                        {
                            sarNum = num;
                            sarDen = den;
                        }
                    }

                    builder.Add(new VideoStreamInfo(
                        codecName!,
                        codecTagString!,
                        profile,
                        language,
                        attachedPicValue != 0,
                        timedThumbnailsValue != 0,
                        stillImageValue != 0,
                        defaultStreamValue != 0,
                        badCandidateForThumbnailValue != 0,
                        width,
                        height,
                        duration,
                        fpsNum,
                        fpsDen,
                        sarNum,
                        sarDen,
                        pixelFormat,
                        colorRange,
                        colorSpace,
                        colorTransfer,
                        colorPrimaries,
                        fieldOrder,
                        bitsPerSample,
                        alphaMode == "1"));
                    break;

                case "audio":
                    builder.Add(new AudioStreamInfo(codecName!, profile, language, duration, channels, sampleRate, channelLayout));
                    break;

                case "subtitle":
                    builder.Add(new SubtitleStreamInfo(codecName!, language, title));
                    break;

                default:
                    char codecChar = codecType switch
                    {
                        "data" => 'd',
                        "attachment" => 't',
                        _ => '\0',
                    };
                    builder.Add(new UnrecognizedStreamInfo(codecType!, codecName, language, codecChar, attachedPicValue != 0, timedThumbnailsValue != 0));
                    break;
            }
        }

        // Read format-level info:
        var formatElement = document.RootElement.GetProperty("format");
        string? formatName = ReadStringProperty(formatElement, "format_name");
        duration = ReadStringPropertyAsDouble(formatElement, "duration");

        // Return the result:
        return new(formatName!, duration, builder.DrainToImmutable());
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

    private static ConfigurationInfo _configInfo;
    private static volatile bool _configInfoInitialized;

    private static IEnumerable<(string Info, string Name)> RunFFprobeConfigurationExtraction(
        string command,
        bool noStartingLine,
        CancellationToken cancellationToken = default)
    {
        // Get the raw configuration output from ffprobe.
        string result = ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            VideoProcessor.FFprobeExePath,
            [command, "-hide_banner", "-v", "error"],
            isShortLived: true,
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

    public static ref readonly ConfigurationInfo Configuration
    {
        get
        {
            EnsureConfigurationInfoInitialized();
            return ref _configInfo;
        }
    }
}
