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
        ImmutableArray<(IAbsoluteFilePath File, (double Offset, bool FromEnd)? Seek)> inputFiles,
        IAbsoluteFilePath outputFile,
        ImmutableArray<PerInputStreamOverride> perInputStreamOverrides,
        ImmutableArray<PerOutputStreamOverride> perOutputStreamOverrides,
        int mapChaptersFrom,
        bool forceProgressiveDownloadSupport,
        bool isToMov)
    {
        public ImmutableArray<(IAbsoluteFilePath File, (double Offset, bool FromEnd)? Seek)> InputFiles { get; } = inputFiles;
        public IAbsoluteFilePath OutputFile { get; } = outputFile;
        public ImmutableArray<PerInputStreamOverride> PerInputStreamOverrides { get; } = perInputStreamOverrides;
        public ImmutableArray<PerOutputStreamOverride> PerOutputStreamOverrides { get; } = perOutputStreamOverrides;
        public int MapChaptersFrom { get; } = mapChaptersFrom;
        public bool ForceProgressiveDownloadSupport { get; } = forceProgressiveDownloadSupport;
        public bool IsToMov { get; } = isToMov;
        public string? HWAccel { get; set; } // Special values: 'null' means auto & not used for filters - 'none' means none & not used for filters
        public bool UseHWAccelFiltersWhenPossible { get; set; } = true;
        public bool HWAccelStrictMode { get; set; }
    }

    public static string MapHWAccelNameToFormatName(string hwaccel) => hwaccel switch
    {
        "videotoolbox" => "videotoolbox_vld",
        "cuda" => "cuda",
        "qsv" => "qsv",
        "amf" => "amf",
        "d3d12va" => "d3d12",
        "d3d11va" => "d3d11va_vld",
        "vulkan" => "vulkan",
        _ => throw new ArgumentException($"Unrecognized hardware acceleration mode: {hwaccel}", nameof(hwaccel)),
    };

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

        protected virtual void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(StreamIndexWithinKind, -1);
        }

        protected virtual bool ShouldInclude(string? hwaccel) => true;

        protected abstract string CommandName { get; }
        protected abstract string CommandArgument { get; }
        protected virtual string GetCommandArgument(string? hwaccel, bool hwaccelStrictMode) => CommandArgument;

        public virtual void PrepareArguments(List<string> args, string? hwaccel, bool hwaccelStrictMode)
        {
            Validate();

            if (!ShouldInclude(hwaccel))
            {
                return;
            }

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
            else
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-{CommandName}:{StreamKind}"));
            }

            args.Add(GetCommandArgument(hwaccel, hwaccelStrictMode));
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
        // Note: the setter is public, but the CommandArgument is cached - correct usage requires setting all properties before first call to CommandArgument.
        public long FPSNum { get; set; } = fpsNum;
        public long FPSDen { get; set; } = fpsDen;
        protected override string CommandName => "r";
        protected override string CommandArgument => field ??=
            FPSDen == 1 ? FPSNum.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{FPSNum}/{FPSDen}");
    }

    public sealed class PerStreamPresetOverride(char streamKind, int streamIndexWithinKind, string preset)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string Preset { get; } = preset;
        protected override string CommandName => "preset";
        protected override string CommandArgument => Preset;
    }

    public sealed class PerStreamX265ParamsOverride(char streamKind, int streamIndexWithinKind, string paramsToPass)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string ParamsToPass { get; } = paramsToPass;
        protected override string CommandName => "x265-params";
        protected override string CommandArgument => ParamsToPass;
    }

    public sealed class PerStreamFilterOverride(char streamKind, int streamIndexWithinKind)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        protected override string CommandArgument => throw new NotSupportedException();
        protected override bool ShouldInclude(string? hwaccel) => Critical || (hwaccel is not null && StreamKind == 'v');

        public bool Critical { get; set; } = true;
        public (long Num, long Den)? FPS { get; set; }
        public (int Width, int Height)? ResizeTo { get; set; }
        public bool HDRToSDR { get; set; }
        public string? PixelFormat { get; set; }
        public bool Deinterlace { get; set; }
        public int MakePixelsSquareMode { get; set; } // 0 - keep 1:1, 1 - ignore, 2 - currently wider, 3 - currently taller
        protected override string CommandName => "filter";
        public bool AssumePotentialAlphaChannelForHDRToSDR { get; set; }
        public bool ForceConvertToFullRange { get; set; }
        public bool Is10BitForHW { get; set; }
        public string? PixelFormatAfterHWDownload { get; set; }
        public string? SarAfterHWResize { get; set; }

        protected override string GetCommandArgument(string? hwaccel, bool hwaccelStrictMode)
        {
            // Suppress this warning - it is about our bool flags, but it is better for maintainability so they are actually accurate for future changes.
#pragma warning disable IDE0059 // Unnecessary assignment of a value
            List<string> steps = [];

            bool doneDeinterlace = false;
            bool doneHDRToSDR = false;
            bool doneResize = false;
            bool doneHWDownload = hwaccel is null;
            bool donePixelFormat = false;
            bool doneRangeConversion = false;
            bool doneFPS = false;
            bool doneMakePixelsSquare = false;
            bool resizeHW = false;

            void EnsureHWDownload()
            {
                if (!doneHWDownload)
                {
                    steps.Add($"format={MapHWAccelNameToFormatName(hwaccel!)}");
                    steps.Add("hwdownload");
                    steps.Add($"format={(Is10BitForHW ? "p010le" : "nv12")}");

                    if (PixelFormatAfterHWDownload is not null)
                    {
                        steps.Add($"format={PixelFormatAfterHWDownload}");
                        donePixelFormat = true;
                    }

                    doneHWDownload = true;
                }
            }

            if (Deinterlace && !doneDeinterlace)
            {
                if (!doneHWDownload && hwaccel == "cuda" && FFprobeUtils.Configuration.SupportsBwdifCudaFilter)
                {
                    // We need to set mode to send_field to match what normal bwdif does by default.
                    steps.Add("bwdif_cuda=mode=send_field");
                }
                else if (!doneHWDownload && hwaccel == "qsv" && FFprobeUtils.Configuration.SupportsVppQsvFilter)
                {
                    // Use bob mode to make more similar to what bwdif does, rather than advanced mode.
                    // Note: we also need to set to field to match what bwdif does by default.
                    string filterPart = "vpp_qsv=deinterlace=bob:rate=field";

                    // If we're also scaling, do that at the same time.
                    if (ResizeTo is var (w1, h1) && !hwaccelStrictMode && !doneResize)
                    {
                        // See comments in resize section for why it's set up this way.
                        filterPart += string.Create(CultureInfo.InvariantCulture, $":w={w1}:h={h1}:scale_mode=hq");
                        doneResize = true;
                        resizeHW = true;
                    }

                    // If we're also doing tv->pc range conversion (and not HDR->SDR conversion), do that at the same time too.
                    if (ForceConvertToFullRange && !HDRToSDR && !doneRangeConversion)
                    {
                        filterPart += ":out_range=full";
                        doneRangeConversion = true;
                    }

                    steps.Add(filterPart);
                }
                else if (!doneHWDownload && hwaccel == "d3d12va" && FFprobeUtils.Configuration.SupportsDeinterlaceD3D12Filter)
                {
                    // Need to specify both bob mode & mode to match what normal bwdif does by default.
                    steps.Add("deinterlace_d3d12=method=bob:mode=field");
                }
                else if (!doneHWDownload && hwaccel == "vulkan" && FFprobeUtils.Configuration.SupportsBwdifVulkanFilter)
                {
                    // We need to set to send_field to match what normal bwdif does by default.
                    steps.Add("bwdif_vulkan=mode=send_field");
                }
                else
                {
                    EnsureHWDownload();
                    steps.Add("bwdif");
                }

                doneDeinterlace = true;
            }

            if (ResizeTo is var (w2, h2) && !doneResize)
            {
                if (!doneHWDownload && hwaccel == "videotoolbox" && !hwaccelStrictMode && FFprobeUtils.Configuration.SupportsScaleVtFilter)
                {
                    // Note: videotoolbox cannot ensure we match the bicubic scaling that 'scale' uses by default, so we just use the default.
                    steps.Add(string.Create(CultureInfo.InvariantCulture, $"scale_vt=w={w2}:h={h2}"));
                    resizeHW = true;
                }
                else if (!doneHWDownload && hwaccel == "cuda" && FFprobeUtils.Configuration.SupportsScaleCudaFilter)
                {
                    // Note: we set interp_algo to bicubic to match what 'scale' uses by default.
                    steps.Add(string.Create(CultureInfo.InvariantCulture, $"scale_cuda=w={w2}:h={h2}:interp_algo=bicubic"));
                    resizeHW = true;
                }
                else if (!doneHWDownload && hwaccel == "qsv" && !hwaccelStrictMode && FFprobeUtils.Configuration.SupportsVppQsvFilter)
                {
                    // Note: qsv cannot ensure we match bicubic scaling that 'scale' uses by default, however 'hq' is most likely to either be it or be closest.
                    string filterPart = string.Create(CultureInfo.InvariantCulture, $"vpp_qsv=w={w2}:h={h2}:scale_mode=hq");

                    // If we're also doing tv->pc range conversion (and not HDR->SDR conversion), do that at the same time.
                    if (ForceConvertToFullRange && !HDRToSDR && !doneRangeConversion)
                    {
                        filterPart += ":out_range=full";
                        doneRangeConversion = true;
                    }

                    steps.Add(filterPart);
                    resizeHW = true;
                }
                else if (!doneHWDownload && hwaccel == "amf" && FFprobeUtils.Configuration.SupportsVppAmfFilter)
                {
                    // Note: we set interp_algo to bicubic to match what 'scale' uses by default.
                    steps.Add(string.Create(CultureInfo.InvariantCulture, $"vpp_amf=w={w2}:h={h2}:scale_type=bicubic"));
                    resizeHW = true;
                }
                else if (!doneHWDownload && hwaccel == "d3d12va" && !hwaccelStrictMode && FFprobeUtils.Configuration.SupportsScaleD3D12Filter)
                {
                    // Note: d3d12 cannot ensure we match bicubic scaling that 'scale' uses by default, so we just use the default.
                    steps.Add(string.Create(CultureInfo.InvariantCulture, $"scale_d3d12=w={w2}:h={h2}"));
                    resizeHW = true;
                }
                else if (!doneHWDownload && hwaccel == "d3d11va" && !hwaccelStrictMode && FFprobeUtils.Configuration.SupportsScaleD3D11Filter)
                {
                    // Note: d3d11 cannot ensure we match bicubic scaling that 'scale' uses by default, so we just use the default.
                    steps.Add(string.Create(CultureInfo.InvariantCulture, $"scale_d3d11=width={w2}:height={h2}"));
                    resizeHW = true;
                }
                else if (!doneHWDownload && hwaccel == "vulkan" && !hwaccelStrictMode && FFprobeUtils.Configuration.SupportsScaleVulkanFilter)
                {
                    // Note: vulkan does not allow us to specify using bicubic, so we use high quality bilinear instead.
                    string filterPart = string.Create(CultureInfo.InvariantCulture, $"scale_vulkan=w={w2}:h={h2}:scaler=bilinear:debayer=bilinear_hq");

                    // If we're also doing tv->pc range conversion (and not HDR->SDR conversion), do that at the same time.
                    if (ForceConvertToFullRange && !HDRToSDR && !doneRangeConversion)
                    {
                        filterPart += ":out_range=full";
                        doneRangeConversion = true;
                    }

                    steps.Add(filterPart);
                    resizeHW = true;
                }
                else
                {
                    EnsureHWDownload();
                    steps.Add(string.Create(CultureInfo.InvariantCulture, $"scale=w={w2}:h={h2}:force_original_aspect_ratio=disable"));
                }

                doneResize = true;
            }

            if (HDRToSDR && !doneHDRToSDR)
            {
                EnsureHWDownload();

                // Remap to HDR first for accurate results - however, this could have a performance penalty if we're also then scaling / sampling it after.
                // Note: for a massive resolution video, this could fail to allocate memory for the frames due to requiring 96/128 bits per pixel (which eats into
                // the 2^31 - 1 byte limit that ffmpeg imposes faster than usual).
                steps.Add(
                    $"zscale=t=linear:npl=500:r=full," +
                    $"format={(AssumePotentialAlphaChannelForHDRToSDR ? "gbrapf32le" : "gbrpf32le")}," +
                    $"zscale=p=bt709," +
                    $"tonemap=tonemap=mobius:param=0.3:desat=0," +
                    $"zscale=t=bt709:m=bt709:r=full");

                doneHDRToSDR = true;
                doneRangeConversion = true;
            }

            if (ForceConvertToFullRange && !doneRangeConversion)
            {
                // Note: colorspace_cuda exists, but it does not seem to actually work properly.
                if (!doneHWDownload && hwaccel == "qsv" && FFprobeUtils.Configuration.SupportsVppQsvFilter)
                {
                    steps.Add("vpp_qsv=out_range=full");
                }
                else if (!doneHWDownload && hwaccel == "vulkan" && FFprobeUtils.Configuration.SupportsScaleVulkanFilter)
                {
                    steps.Add("scale_vulkan=out_range=full");
                }
                else
                {
                    EnsureHWDownload();
                    steps.Add("scale=out_range=full");
                }

                doneRangeConversion = true;
            }

            EnsureHWDownload();

            if (FPS is { } fps && !doneFPS)
            {
                steps.Add(fps switch
                {
                    (long num, 1) => string.Create(CultureInfo.InvariantCulture, $"fps=fps={num}:eof_action=pass"),
                    var (num, den) => string.Create(CultureInfo.InvariantCulture, $"fps=fps={num}/{den}:eof_action=pass"),
                });

                doneFPS = true;
            }

            if (PixelFormat is not null && !donePixelFormat)
            {
                steps.Add($"format={PixelFormat}");

                donePixelFormat = true;
            }

            if ((MakePixelsSquareMode != 1 || resizeHW) && !doneMakePixelsSquare)
            {
                // Note: we use max int for the max, to match what scale does.
                if (!resizeHW || SarAfterHWResize is null)
                    steps.Add("setsar=sar=1/1");
                else
                    steps.Add($"setsar=sar={SarAfterHWResize}:max={int.MaxValue}");

                doneMakePixelsSquare = true;
            }
#pragma warning restore IDE0059 // Unnecessary assignment of a value

            return string.Join(',', steps);
        }
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
        // Note: the setter is public, but the CommandArgument is cached - correct usage requires setting all properties before first call to CommandArgument.
        public int SampleRate { get; set; } = sampleRate;
        protected override string CommandName => "ar";
        protected override string CommandArgument => field ??= SampleRate.ToString(CultureInfo.InvariantCulture);
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

    public sealed class PerStreamTagOverride(char streamKind, int streamIndexWithinKind, string tag)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string Tag { get; } = tag;
        protected override string CommandName => "tag";
        protected override string CommandArgument => Tag;
    }

    // For now we only support setting metadata by overall index to a stream, as we only need that currently.
    public sealed class PerStreamMetadataOverride(char streamKind, int streamIndexWithinKind)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public string? Language { get; set; }
        protected override string CommandName => string.Empty;
        protected override string CommandArgument => string.Empty;

        public override void PrepareArguments(List<string> args, string? hwaccel, bool hwaccelStrictMode)
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
        }
    }

    public sealed class PerStreamFramesOverride(char streamKind, int streamIndexWithinKind, int frames)
        : PerOutputStreamOverride(streamKind, streamIndexWithinKind)
    {
        public int Frames { get; } = frames;
        protected override string CommandName => "frames";
        protected override string CommandArgument { get; } = frames.ToString(CultureInfo.InvariantCulture);
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
            else
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
            else
            {
                args.Add(string.Create(CultureInfo.InvariantCulture, $"-map_metadata:s:{StreamKind}"));
                if (FileIndex != -1) args.Add(string.Create(CultureInfo.InvariantCulture, $"{FileIndex}:s:{StreamKind}"));
                else args.Add("-1");
            }
        }
    }

    private static IEnumerable<string> CreateArguments(FFmpegCommand command, string? progressFilePath, string? threadLimit)
    {
        List<string> args = [];

        if (threadLimit is not null)
        {
            args.Add("-filter_threads");
            args.Add(threadLimit);

            args.Add("-filter_complex_threads");
            args.Add(threadLimit);
        }

        // Input files:
        for (int i = 0; i < command.InputFiles.Length; i++)
        {
            if (command.InputFiles[i].Seek is (double offset, bool fromEnd))
            {
                args.Add(fromEnd ? "-sseof" : "-ss");
                args.Add(offset.ToString("F6", CultureInfo.InvariantCulture));
            }

            if (threadLimit is not null)
            {
                args.Add("-threads");
                args.Add(threadLimit);
            }

#if !CUSTOM_HWACCEL_MODE_NONE
            if (command.HWAccel is not null)
            {
                args.Add("-hwaccel");
                args.Add(command.HWAccel);

                if (command.HWAccel != "none" && command.UseHWAccelFiltersWhenPossible)
                {
                    args.Add("-hwaccel_output_format");
                    args.Add(MapHWAccelNameToFormatName(command.HWAccel));
                }
            }
            else
            {
                args.Add("-hwaccel");
                args.Add("auto");
            }
#else
            args.Add("-hwaccel");
            args.Add("none");
#endif

            args.Add("-i");
            args.Add(command.InputFiles[i].File.PathExport);
        }

        // Specifies how to map chapter metadata if it's .mov/.mp4 output:
        if (command.IsToMov)
        {
            args.Add("-map_chapters");
            args.Add(command.MapChaptersFrom.ToString(CultureInfo.InvariantCulture));
        }

        // Per-input-stream overrides:
        foreach (var perInputOverride in command.PerInputStreamOverrides)
        {
            perInputOverride.PrepareArguments(args);
        }

        // Per-output-stream overrides:
        foreach (var perOutputOverride in command.PerOutputStreamOverrides)
        {
            string? hwAccelMode = command.HWAccel switch
            {
                "none" or null => null,
                _ when !command.UseHWAccelFiltersWhenPossible => null,
                var x => x,
            };

            perOutputOverride.PrepareArguments(args, hwAccelMode, command.HWAccelStrictMode);
        }

        // Set mov/mp4 specific options if outputting to .mov/.mp4:
        if (command.IsToMov)
        {
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
        }

        // Progress reporting:
        if (progressFilePath != null)
        {
            args.Add("-progress");
            args.Add(progressFilePath);

            // Update stats every 100ms (or 16ms in debug mode, so we can test progress reading properly):
            args.Add("-stats_period");
#if !DEBUG
            args.Add("0.1");
#else
            args.Add("0.016");
#endif
        }

        // Unrecognized stream handling, error handling, and stdout handling:
        args.Add("-copy_unknown");
        args.Add("-xerror");
        args.Add("-hide_banner");
        args.Add("-nostdin");

        // Output file:
        args.Add("-y");

        if (threadLimit is not null)
        {
            args.Add("-threads");
            args.Add(threadLimit);
        }

        args.Add(command.OutputFile.PathExport);
        return args;
    }

    public static async Task RunFFmpegCommandAsync(
        FFmpegCommand command,
        Func<double, ValueTask>? progressCallback,
        IAbsoluteFilePath? progressFilePath,
        Func<bool, ValueTask>? queueingCallback = null,
        ProcessLifetime lifetime = ProcessLifetime.LongLived,
        CancellationToken cancellationToken = default)
    {
        // Validate progress callback and progress temp file path args:
        // Note: RunRawFFmpegCommandAsync also checks this, but we do it here also to fail fast before running CreateArguments.
        if (progressCallback is null != progressFilePath is null)
        {
            throw new ArgumentException("If a progress callback or progress file path is provided, both must be provided and non-null.");
        }

        // Ensure dest dirs exists:
        command.OutputFile.ParentDirectory.Create();

        // Run actual ffmpeg command:
        int? threadLimit = VideoProcessor.ThreadLimit;
        await RunRawFFmpegCommandAsync(
            CreateArguments(command, progressFilePath?.PathExport, threadLimit?.ToString(CultureInfo.InvariantCulture)),
            progressCallback,
            progressFilePath,
            ensureAllProgressRead: false,
            queueingCallback: queueingCallback,
            lifetime: lifetime,
            cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    }

    public static async Task RunRawFFmpegCommandAsync(
        IEnumerable<string> args,
        Func<double, ValueTask>? progressCallback,
        IAbsoluteFilePath? progressFilePath,
        bool ensureAllProgressRead, // Ensures that all progress is read if at least one progress callback is invoked.
        Func<bool, ValueTask>? queueingCallback = null,
        ProcessLifetime lifetime = ProcessLifetime.LongLived,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Validate progress callback and progress temp file path args:
        if (progressCallback is null != progressFilePath is null)
        {
            throw new ArgumentException("If a progress callback or progress file path is provided, both must be provided and non-null.");
        }

        // Set up progress callback reading if needed:
        using var progressCallbackCts = new CancellationTokenSource();
        var progressCallbackCt = progressCallbackCts.Token;
        progressFilePath?.ParentDirectory.Create();
        progressFilePath?.Delete();
        FileStream? fs = progressFilePath?.OpenAsyncStream(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            var progressCallbackTask = fs != null ? Task.Run(async () =>
            {
                List<byte> lineBuffer = [];
                byte[] buffer = new byte[32];
                int bytesRead;
                bool justRead = false;
#if !DEBUG
                const int WaitTimeMs = 90;
#else
                const int WaitTimeMs = 5;
#endif

                // This loop is for ensuring we read all progress (even post-exit) if requested.
                for (int i = 0; i < (ensureAllProgressRead ? 2 : 1); i++)
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
                            justRead = false;
                        }
                        else if (ensureAllProgressRead)
                        {
                            // If we're wanting to ensure all progress is read, we need to ensure we don't get a OperationCanceledException, as that would stop
                            // the outer loop from being able to run its second iteration.
                            try
                            {
                                await Task.Delay(WaitTimeMs, progressCallbackCt).ConfigureAwait(false);
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
                            await Task.Delay(WaitTimeMs, progressCallbackCt).ConfigureAwait(false);
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
                    lifetime: lifetime,
                    queueingCallback: queueingCallback,
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
