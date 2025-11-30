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

    public abstract class StreamInfo;

    public sealed class VideoStreamInfo(
        string codecName,
        string? profileName,
        bool isAttachedPic,
        bool isTimedThumbnail,
        int width,
        int height,
        double? duration,
        int fpsNum,
        int fpsDen,
        string? pixelFormat,
        string? colorRange,
        string? colorSpace,
        string? colorTransfer,
        string? colorPrimaries,
        int bitsPerSample)
        : StreamInfo
    {
        public string CodecName { get; } = codecName;
        public string? ProfileName { get; } = profileName;
        public bool IsAttachedPic { get; } = isAttachedPic;
        public bool IsTimedThumbnail { get; } = isTimedThumbnail;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public double? Duration { get; } = duration;
        public int FpsNum { get; } = fpsNum;
        public int FpsDen { get; } = fpsDen;
        public string? PixelFormat { get; } = pixelFormat;
        public string? ColorRange { get; } = colorRange;
        public string? ColorSpace { get; } = colorSpace;
        public string? ColorTransfer { get; } = colorTransfer;
        public string? ColorPrimaries { get; } = colorPrimaries;
        public int BitsPerSample { get; } = bitsPerSample;
    }

    public sealed class AudioStreamInfo(
        string codecName,
        string? profileName,
        double? duration,
        int channels,
        int sampleRate)
        : StreamInfo
    {
        public string CodecName { get; } = codecName;
        public string? ProfileName { get; } = profileName;
        public double? Duration { get; } = duration;
        public int Channels { get; } = channels;
        public int SampleRate { get; } = sampleRate;
    }

    public sealed class UnrecognisedStreamInfo(string codecType, char streamShorthand, bool isAttachedPic, bool isTimedThumbnail) : StreamInfo
    {
        public string CodecType { get; } = codecType;
        public char StreamShorthand { get; } = streamShorthand;
        public bool IsAttachedPic { get; } = isAttachedPic;
        public bool IsTimedThumbnail { get; } = isTimedThumbnail;
    }

    private static int? ReadInt32Property(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var propElement) && propElement.TryGetInt32(out int value))
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

    public static async Task<VideoFileInfo> GetVideoFileAsync(IAbsoluteFilePath filePath, CancellationToken cancellationToken = default)
    {
        // Get the ffprobe JSON output for the file:
        string json = await ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            VideoProcessor.FFprobeExePath,
            ["-show_format", "-show_streams", "-print_format", "json", "-v", "error", "-hide_banner", "-i", filePath.PathExport],
            isShortLived: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Parse the JSON output:
        using var document = JsonDocument.Parse(json);
        var (streams, streamsCount) = document.RootElement.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array
            ? (streamsElement.EnumerateArray(), streamsElement.GetArrayLength())
            : (default, 0);

        // Read each stream's info:
        double? duration;
        var builder = ImmutableArray.CreateBuilder<StreamInfo>(streamsCount);
        foreach (var stream in streams)
        {
            string? codecName = ReadStringProperty(stream, "codec_name");
            string? codecType = ReadStringProperty(stream, "codec_type");
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
            int bitsPerSample = ReadInt32Property(stream, "bits_per_sample") ?? -1;
            int channels = ReadInt32Property(stream, "channels") ?? -1;
            int sampleRate = ReadInt32Property(stream, "sample_rate") ?? -1;
            int attachedPicValue = 0, timedThumbnailValue = 0;
            if (stream.TryGetProperty("disposition", out var dispositionsProp) && dispositionsProp.ValueKind == JsonValueKind.Object)
            {
                attachedPicValue = ReadInt32Property(dispositionsProp, "attached_pic") ?? 0;
                timedThumbnailValue = ReadInt32Property(dispositionsProp, "timed_thumbnail") ?? 0;
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

                    builder.Add(new VideoStreamInfo(
                        codecName!,
                        profile,
                        attachedPicValue != 0,
                        timedThumbnailValue != 0,
                        width,
                        height,
                        duration,
                        fpsNum,
                        fpsDen,
                        pixelFormat,
                        colorRange,
                        colorSpace,
                        colorTransfer,
                        colorPrimaries,
                        bitsPerSample));
                    break;

                case "audio":
                    builder.Add(new AudioStreamInfo(codecName!, profile, duration, channels, sampleRate));
                    break;

                default:
                    char codecChar = codecType switch
                    {
                        "subtitle" => 's',
                        "data" => 'd',
                        "attachment" => 't',
                        _ => '\0',
                    };
                    builder.Add(new UnrecognisedStreamInfo(codecType!, codecChar, attachedPicValue != 0, timedThumbnailValue != 0));
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
        public bool SupportsLibFDKAACEncoder { get; set; }
        public bool SupportsAACEncoder { get; set; }

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
    }

    private static ConfigurationInfo _configInfo;
    private static volatile bool _configInfoInitialized;

    private static IEnumerable<(string Info, string Name)> RunFFprobeConfigurationExtraction(string command, CancellationToken cancellationToken = default)
    {
        // Get the raw configuration output from ffprobe.
        string result = ProcessUtils.RunProcessToStringWithErrorHandlingAsync(
            VideoProcessor.FFprobeExePath,
            [command, "-hide_banner", "-v", "error"],
            isShortLived: true,
            cancellationToken: cancellationToken,
            runAsynchronously: false).GetAwaiter().GetResult();

        // Skip to the line formatted as '<space*>-----<space*>' with some number of dashes.
        // We will use the number of dashes to determine the length of the configuration info section, and the number of preceding spaces.
        using var lineReader = new StringReader(result);
        string line;
        int removeStart = -1;
        int configLength = -1;
        while ((line = lineReader.ReadLine()) != null)
        {
            var sp = line.AsSpan().Trim(' ');
            if (sp.Length > 0 && !sp.ContainsAnyExcept('-'))
            {
                removeStart = line.AsSpan().IndexOf('-');
                configLength = sp.Length;
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
            foreach (var (info, name) in RunFFprobeConfigurationExtraction("-encoders"))
            {
                switch (name)
                {
                    case "libx264" when info is ['V', ..]: _configInfo.SupportsLibX264Encoder = true; break;
                    case "libx265" when info is ['V', ..]: _configInfo.SupportsLibX265Encoder = true; break;
                    case "libfdk_aac" when info is ['A', ..]: _configInfo.SupportsLibFDKAACEncoder = true; break;
                    case "aac" when info is ['A', ..]: _configInfo.SupportsAACEncoder = true; break;
                }
            }

            // Initialize codecs
            foreach (var (info, name) in RunFFprobeConfigurationExtraction("-codecs"))
            {
                switch (name)
                {
                    // Video decoders
                    case "mpeg1video" when info is ['D', _, 'V']: _configInfo.SupportsMpeg1VideoDecoder = true; break;
                    case "mpeg2video" when info is ['D', _, 'V']: _configInfo.SupportsMpeg2VideoDecoder = true; break;
                    case "mpeg4" when info is ['D', _, 'V']: _configInfo.SupportsMpeg4Decoder = true; break;
                    case "h263" when info is ['D', _, 'V']: _configInfo.SupportsH263Decoder = true; break;
                    case "h264" when info is ['D', _, 'V']: _configInfo.SupportsH264Decoder = true; break;
                    case "hevc" when info is ['D', _, 'V']: _configInfo.SupportsHEVCDecoder = true; break;
                    case "vvc" when info is ['D', _, 'V']: _configInfo.SupportsVVCDecoder = true; break;
                    case "vp8" when info is ['D', _, 'V']: _configInfo.SupportsVP8Decoder = true; break;
                    case "vp9" when info is ['D', _, 'V']: _configInfo.SupportsVP9Decoder = true; break;
                    case "av1" when info is ['D', _, 'V']: _configInfo.SupportsAV1Decoder = true; break;

                    // Audio decoders
                    case "aac" when info is ['D', _, 'A']: _configInfo.SupportsAACDecoder = true; break;
                    case "mp2" when info is ['D', _, 'A']: _configInfo.SupportsMP2Decoder = true; break;
                    case "mp3" when info is ['D', _, 'A']: _configInfo.SupportsMP3Decoder = true; break;
                    case "vorbis" when info is ['D', _, 'A']: _configInfo.SupportsVorbisDecoder = true; break;
                    case "opus" when info is ['D', _, 'A']: _configInfo.SupportsOpusDecoder = true; break;
                }
            }

            // Initialize muxing support
            foreach (var (info, name) in RunFFprobeConfigurationExtraction("-muxers"))
            {
                switch (name)
                {
                    // Muxers
                    case "mp4" when info is [_, 'E', ..]: _configInfo.SupportsMP4Muxing = true; break;

                    // Demuxers
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
