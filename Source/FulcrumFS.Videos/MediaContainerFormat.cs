using System.Collections.Frozen;
using Singulink.IO;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for media container file formats used in video processing.
/// </summary>
/// <remarks>
/// Each <see cref="MediaContainerFormat" /> represents one specific container file format with a unique primary extension. While ffmpeg groups some formats
/// together under the same demuxer (e.g., mov/mp4/m4a/3gp/3g2/mj2 share a single demuxer, and matroska/webm share another), this type distinguishes them
/// strictly: content is verified against the expected file extension and a mismatch is treated as an error rather than silently reconciled.
/// </remarks>
public abstract class MediaContainerFormat
{
    private const string IsoBmffName = "mov,mp4,m4a,3gp,3g2,mj2";
    private const string MatroskaName = "matroska,webm";

    /// <summary>
    /// Gets the loose MP4 media container format. Extensions: <c>.mp4</c> (primary), <c>.mov</c>, <c>.m4a</c>, <c>.3gp</c>, <c>.3g2</c>, <c>.mj2</c>.
    /// </summary>
    public static MediaContainerFormat MP4Loose { get; } = new Impl(FileFormat.Mp4Loose, IsoBmffName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMovGroupDemuxing);

    /// <summary>
    /// Gets the MP4 media container format. Extension: <c>.mp4</c>. This is currently the only format that supports muxing (writing) output.
    /// </summary>
    public static MediaContainerFormat MP4 { get; } = new Impl(FileFormat.Mp4, IsoBmffName, supportsWriting: true, static () => FFprobeUtils.Configuration.SupportsMovGroupDemuxing);

    /// <summary>
    /// Gets the QuickTime/MOV media container format. Extension: <c>.mov</c>.
    /// </summary>
    public static MediaContainerFormat Mov { get; } = new Impl(FileFormat.Mov, IsoBmffName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMovGroupDemuxing);

    /// <summary>
    /// Gets the MPEG-4 audio (M4A) media container format. Extension: <c>.m4a</c>.
    /// </summary>
    public static MediaContainerFormat M4A { get; } = new Impl(FileFormat.M4a, IsoBmffName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMovGroupDemuxing);

    /// <summary>
    /// Gets the 3GPP (3GP) media container format. Extension: <c>.3gp</c>.
    /// </summary>
    public static MediaContainerFormat Tgp { get; } = new Impl(FileFormat.Tgp, IsoBmffName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMovGroupDemuxing);

    /// <summary>
    /// Gets the 3GPP2 (3G2) media container format. Extension: <c>.3g2</c>.
    /// </summary>
    public static MediaContainerFormat Tg2 { get; } = new Impl(FileFormat.Tg2, IsoBmffName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMovGroupDemuxing);

    /// <summary>
    /// Gets the Motion JPEG 2000 (MJ2) media container format. Extension: <c>.mj2</c>.
    /// </summary>
    public static MediaContainerFormat Mj2 { get; } = new Impl(FileFormat.Mj2, IsoBmffName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMovGroupDemuxing);

    /// <summary>
    /// Gets the Matroska (MKV) media container format. Extension: <c>.mkv</c>.
    /// </summary>
    public static MediaContainerFormat Mkv { get; } = new Impl(FileFormat.Mkv, MatroskaName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMatroskaGroupDemuxing);

    /// <summary>
    /// Gets the WebM media container format. Extension: <c>.webm</c>.
    /// </summary>
    public static MediaContainerFormat WebM { get; } = new Impl(FileFormat.WebM, MatroskaName, supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMatroskaGroupDemuxing);

    /// <summary>
    /// Gets the AVI media container format. Extension: <c>.avi</c>.
    /// </summary>
    public static MediaContainerFormat Avi { get; } = new Impl(FileFormat.Avi, "avi", supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsAviDemuxing);

    /// <summary>
    /// Gets the MPEG-TS (188-byte packet) media container format. Extension: <c>.ts</c>.
    /// </summary>
    public static MediaContainerFormat TS { get; } = new Impl(FileFormat.Ts, "mpegts", supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMpegTSGroupDemuxing);

    /// <summary>
    /// Gets the MPEG-TS (192-byte packet, Blu-ray style) media container format. Extensions: <c>.m2ts</c> (primary), <c>.mts</c>.
    /// </summary>
    public static MediaContainerFormat M2ts { get; } = new Impl(FileFormat.M2ts, "mpegts", supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMpegTSGroupDemuxing);

    /// <summary>
    /// Gets the MPEG-PS media container format. Extensions: <c>.mpg</c> (primary), <c>.mpeg</c>.
    /// </summary>
    public static MediaContainerFormat Mpeg { get; } = new Impl(FileFormat.Mpeg, "mpeg", supportsWriting: false, static () => FFprobeUtils.Configuration.SupportsMpegDemuxing);

    /// <summary>
    /// Gets a list of all supported media container formats for source files (with writable ones first).
    /// </summary>
    public static IReadOnlyList<MediaContainerFormat> AllSourceFormats { get; } =
    [
        MP4,
        MP4Loose,
        Mov,
        M4A,
        Tgp,
        Tg2,
        Mj2,
        Mkv,
        WebM,
        Avi,
        TS,
        M2ts,
        Mpeg,
    ];

    /// <summary>
    /// Gets a list of all supported media container formats that have writing support (<see cref="SupportsWriting" />).
    /// </summary>
    public static IReadOnlyList<MediaContainerFormat> AllResultFormats { get; } =
    [
        MP4,
    ];

    /// <summary>
    /// Gets the corresponding <see cref="FulcrumFS.FileFormat"/> used for content validation.
    /// </summary>
    public abstract FileFormat FileFormat { get; }

    /// <summary>
    /// Gets a value indicating whether this container format supports being written to, i.e., whether we support muxing for it.
    /// </summary>
    public virtual bool SupportsWriting => false;

    /// <summary>
    /// Gets the name of the container format as used in ffprobe output (format_name). Multiple <see cref="MediaContainerFormat" /> instances may share the
    /// same demuxer name when ffmpeg groups them under a single demuxer (e.g., all ISO BMFF formats share "mov,mp4,m4a,3gp,3g2,mj2").
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the file extensions associated with this media container format (including the leading '.'). Most formats have a single extension; some have
    /// aliases (e.g., .mpeg and .mpg) that all refer to the same underlying format.
    /// </summary>
    public IReadOnlyList<string> Extensions => FileFormat.Extensions;

    /// <summary>
    /// Gets the primary file extension associated with this media container format (including the leading '.').
    /// </summary>
    /// <remarks>
    /// The primary extension is the first extension in <see cref="Extensions"/> and all files of this format will be written with this extension.
    /// </remarks>
    public string PrimaryExtension => FileFormat.PrimaryExtension;

    /// <summary>
    /// Determines whether the specified format name (from ffprobe) matches this media container format.
    /// </summary>
    /// <remarks>
    /// ffmpeg groups some demuxers together and reports the combined name as a comma-separated list (e.g., "mov,mp4,m4a,3gp,3g2,mj2"). This method returns
    /// true when every demuxer name in <paramref name="formatName"/> belongs to this format's demuxer group.
    /// </remarks>
    public abstract bool NameMatches(string formatName);

    // Internal helper to get whether this codec has a supported demuxer in the current ffmpeg configuration:
    internal abstract bool HasSupportedDemuxer { get; }

    /// <summary>
    /// Validates that the file's actual content matches this container format. The caller has already verified that the file extension is one this format
    /// accepts and that the ffprobe-reported format name belongs to this format's demuxer group.
    /// </summary>
    internal async ValueTask<bool> MatchesContentAsync(IAbsoluteFilePath filePath, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(filePath.PathExport);
        await using (stream.ConfigureAwait(false))
        {
            var result = await FileFormat.ValidateAsync(stream, cancellationToken).ConfigureAwait(false);
            return result.IsValid;
        }
    }

    private sealed class Impl : MediaContainerFormat
    {
        private readonly FrozenSet<string> _demuxerNames;
        private readonly Func<bool> _hasSupportedDemuxer;

        public Impl(FileFormat fileFormat, string name, bool supportsWriting, Func<bool> hasSupportedDemuxer)
        {
            FileFormat = fileFormat;
            Name = name;
            SupportsWriting = supportsWriting;
            _hasSupportedDemuxer = hasSupportedDemuxer;
            _demuxerNames = name.Split(',').ToFrozenSet(StringComparer.Ordinal);
        }

        public override FileFormat FileFormat { get; }

        public override string Name { get; }

        public override bool SupportsWriting { get; }

        internal override bool HasSupportedDemuxer => _hasSupportedDemuxer();

        public override bool NameMatches(string formatName)
        {
            if (!formatName.Contains(','))
                return _demuxerNames.Contains(formatName);

            var lookup = _demuxerNames.GetAlternateLookup<ReadOnlySpan<char>>();
            foreach (var range in formatName.AsSpan().Split(','))
            {
                if (!lookup.Contains(formatName.AsSpan()[range]))
                    return false;
            }

            return true;
        }
    }
}
