using System.Collections.Frozen;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for media container file formats used in video processing.
/// </summary>
public abstract class MediaContainerFormat
{
    /// <summary>
    /// <para>
    /// Gets the MP4 media container format.</para>
    /// <para>
    /// Note: since the file structure of mp4, mov, m4a, 3gp, 3g2, and mj2 are all the same for ffmpeg demuxing, they can all be matched by this format.</para>
    /// <para>
    /// Note: this format supports muxing to .mp4.</para>
    /// </summary>
    public static MediaContainerFormat MP4 { get; } = new MP4Impl();

    /// <inheritdoc cref="MP4" />
    public static MediaContainerFormat Mov => MP4;

    /// <summary>
    /// <para>
    /// Gets the MKV media container format.</para>
    /// <para>
    /// Note: since the file structure of mkv, and webm are all the same for ffmpeg demuxing, they can both be matched by this format.</para>
    /// <para>
    /// Note: this format does not support muxing, only demuxing.</para>
    /// </summary>
    public static MediaContainerFormat Mkv { get; } = new MKVImpl();

    /// <inheritdoc cref="Mkv" />
    public static MediaContainerFormat WebM => Mkv;

    /// <summary>
    /// <para>
    /// Gets the AVI media container format.</para>
    /// <para>
    /// Note: this format does not support muxing, only demuxing.</para>
    /// </summary>
    public static MediaContainerFormat Avi { get; } = new AviImpl();

    /// <summary>
    /// <para>
    /// Gets the TS media container format.</para>
    /// <para>
    /// Note: since the file structure of ts, mts, and m2ts are all the same for ffmpeg demuxing, they can all be matched by this format.</para>
    /// <para>
    /// Note: this format does not support muxing, only demuxing.</para>
    /// </summary>
    public static MediaContainerFormat TS { get; } = new TSImpl();

    /// <summary>
    /// <para>
    /// Gets the MPEG media container format.</para>
    /// <para>
    /// Note: this format does not support muxing, only demuxing.</para>
    /// </summary>
    public static MediaContainerFormat Mpeg { get; } = new MpegImpl();

    /// <summary>
    /// Gets a list of all supported media container formats (with writable ones first).
    /// </summary>
    public static IReadOnlyList<MediaContainerFormat> AllSourceFormats { get; } =
    [
        MP4,
        Mkv,
        Avi,
        TS,
        Mpeg,
    ];

    /// <summary>
    /// Gets a list of all supported media container formats, that have writing support (<see cref="SupportsWriting" />).
    /// </summary>
    public static IReadOnlyList<MediaContainerFormat> AllResultFormats { get; } =
    [
        MP4,
    ];

    /// <summary>
    /// Gets a value indicating whether this container format supports being written to, as we only implement writing for some formats - i.e., whether we
    /// support muxing for it.
    /// </summary>
    public virtual bool SupportsWriting => false;

    /// <summary>
    /// Gets the name of the container format as used in ffprobe output (format_name).
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the common file extensions associated with this media container format (including the leading '.').
    /// </summary>
    public abstract IEnumerable<string> CommonExtensions { get; }

    /// <summary>
    /// Gets the primary file extension associated with this media container format (including the leading '.').
    /// </summary>
    public abstract string PrimaryExtension { get; }

    private FrozenDictionary<string, int> NameParts
        => field ??= Name
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((x, i) => (x, i))
            .ToFrozenDictionary((x) => x.x, (x) => x.i, StringComparer.Ordinal);

    /// <summary>
    /// Determines whether the specified format name (from ffprobe) matches this media container format.
    /// </summary>
    public bool NameMatches(string formatName)
    {
        // Simple cases: our listed name is up to date, neither have ','s meaning they differ.
        if (Name == formatName)
        {
            return true;
        }
        else if (!Name.Contains(',') && !formatName.Contains(','))
        {
            return false;
        }

        // Otherwise, check if our listed name is a weak subset of the specified format name (comma separated).
        var nameParts = NameParts;
        var namePartsAltLookup = nameParts.GetAlternateLookup<ReadOnlySpan<char>>();
#pragma warning disable SA1119 // Statement should not use unnecessary parenthesis
        Span<bool> matchedParts = nameParts.Count < 16 ? (stackalloc bool[16])[..nameParts.Count] : new bool[nameParts.Count];
#pragma warning restore SA1119 // Statement should not use unnecessary parenthesis
        foreach (var part in formatName.AsSpan().Split(','))
        {
            var partStr = formatName.AsSpan()[part];
            if (namePartsAltLookup.TryGetValue(partStr, out int idx))
            {
                matchedParts[idx] = true;
            }
        }

        return !matchedParts.Contains(false);
    }

    // Internal helper to get whether this codec has a supported demuxer in the current ffmpeg configuration:
    internal abstract bool HasSupportedDemuxer { get; }

    private sealed class MP4Impl : MediaContainerFormat
    {
        public override bool SupportsWriting => true;
        public override string Name => "mov,mp4,m4a,3gp,3g2,mj2";
        public override IEnumerable<string> CommonExtensions { get; } = [".mp4", ".mov", ".m4a", ".3gp", ".3g2", ".mj2"];
        public override string PrimaryExtension => ".mp4";
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMovGroupDemuxing;
    }

    private sealed class MKVImpl : MediaContainerFormat
    {
        public override string Name => "matroska,webm";
        public override IEnumerable<string> CommonExtensions { get; } = [".mkv", ".webm"];
        public override string PrimaryExtension => ".mkv";
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMatroskaGroupDemuxing;
    }

    private sealed class AviImpl : MediaContainerFormat
    {
        public override string Name => "avi";
        public override IEnumerable<string> CommonExtensions { get; } = [".avi"];
        public override string PrimaryExtension => ".avi";
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsAviDemuxing;
    }

    private sealed class TSImpl : MediaContainerFormat
    {
        public override string Name => "mpegts";
        public override IEnumerable<string> CommonExtensions { get; } = [".ts", ".mts", ".m2ts"];
        public override string PrimaryExtension => ".ts";
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMpegTSGroupDemuxing;
    }

    private sealed class MpegImpl : MediaContainerFormat
    {
        public override string Name => "mpeg";
        public override IEnumerable<string> CommonExtensions { get; } = [".mpeg", ".mpg"];
        public override string PrimaryExtension => ".mpeg";
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMpegDemuxing;
    }
}
