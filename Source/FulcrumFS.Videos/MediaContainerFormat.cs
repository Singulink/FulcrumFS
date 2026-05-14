using System.Collections.Frozen;
using Singulink.IO;

namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for media container file formats used in video processing.
/// </summary>
/// <remarks>
/// Each <see cref="MediaContainerFormat" /> represents one specific container file format with a unique extension. While ffmpeg groups some formats together
/// under the same demuxer (e.g., mov/mp4/m4a/3gp/3g2/mj2 share a single demuxer, and matroska/webm share another), this type distinguishes them strictly:
/// content is verified against the expected file extension and a mismatch is treated as an error rather than silently reconciled.
/// </remarks>
public abstract class MediaContainerFormat
{
    /// <summary>
    /// Gets the MP4 media container format. Primary extension is ".mp4". This is currently the only format that supports muxing (writing) output.
    /// </summary>
    public static MediaContainerFormat MP4 { get; } = new MP4Impl();

    /// <summary>
    /// Gets the QuickTime/MOV media container format. Primary extension is ".mov".
    /// </summary>
    public static MediaContainerFormat Mov { get; } = new MovImpl();

    /// <summary>
    /// Gets the MPEG-4 audio (M4A) media container format. Primary extension is ".m4a".
    /// </summary>
    public static MediaContainerFormat M4A { get; } = new M4AImpl();

    /// <summary>
    /// Gets the 3GPP (3GP) media container format. Primary extension is ".3gp".
    /// </summary>
    public static MediaContainerFormat Tgp { get; } = new TgpImpl();

    /// <summary>
    /// Gets the 3GPP2 (3G2) media container format. Primary extension is ".3g2".
    /// </summary>
    public static MediaContainerFormat Tg2 { get; } = new Tg2Impl();

    /// <summary>
    /// Gets the Motion JPEG 2000 (MJ2) media container format. Primary extension is ".mj2".
    /// </summary>
    public static MediaContainerFormat Mj2 { get; } = new Mj2Impl();

    /// <summary>
    /// Gets the Matroska (MKV) media container format. Primary extension is ".mkv".
    /// </summary>
    public static MediaContainerFormat Mkv { get; } = new MkvImpl();

    /// <summary>
    /// Gets the WebM media container format. Primary extension is ".webm".
    /// </summary>
    public static MediaContainerFormat WebM { get; } = new WebMImpl();

    /// <summary>
    /// Gets the AVI media container format. Primary extension is ".avi".
    /// </summary>
    public static MediaContainerFormat Avi { get; } = new AviImpl();

    /// <summary>
    /// Gets the MPEG-TS (188 byte packet) media container format. Primary extension is ".ts".
    /// </summary>
    public static MediaContainerFormat TS { get; } = new TSImpl();

    /// <summary>
    /// Gets the MPEG-TS (192 byte packet, Blu-ray style) media container format. Primary extension is ".m2ts".
    /// </summary>
    public static MediaContainerFormat M2ts { get; } = new M2tsImpl();

    /// <summary>
    /// Gets the MPEG-PS media container format. Primary extension is ".mpeg".
    /// </summary>
    public static MediaContainerFormat Mpeg { get; } = new MpegImpl();

    /// <summary>
    /// Gets a list of all supported media container formats for source files (with writable ones first).
    /// </summary>
    public static IReadOnlyList<MediaContainerFormat> AllSourceFormats { get; } =
    [
        MP4,
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
    public abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Gets the primary file extension associated with this media container format (including the leading '.').
    /// </summary>
    /// <remarks>
    /// The primary extension is the first extension in <see cref="Extensions"/> and all files of this format will be written with this extension.
    /// </remarks>
    public string PrimaryExtension => Extensions[0];

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
#pragma warning restore SA1119
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

    /// <summary>
    /// Validates that the file's actual content matches this container format. The caller has already verified that the file extension is one this format
    /// accepts and that the ffprobe-reported format name belongs to this format's demuxer group.
    /// </summary>
    internal abstract ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken);

    private const string IsoBmffName = "mov,mp4,m4a,3gp,3g2,mj2";
    private const string MatroskaName = "matroska,webm";

    // ISO BMFF (mov, mp4, m4a, 3gp, 3g2, mj2) brand sets. ffprobe exposes the file's major_brand and compatible_brands from the ftyp box.
    // We accept a file as being of a particular format if the major_brand or any compatible_brand is in that format's brand set.

    private static readonly FrozenSet<string> _mp4Brands = new[]
    {
        // ISO base media file format brands, MP4 brands, DASH/fragmented MP4 brands and common derivatives.
        "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "iso7", "iso8", "iso9",
        "mp41", "mp42", "mp4v", "mp4a",
        "avc1", "hvc1", "hev1", "dby1", "dash", "msdh", "msix", "MSNV",
        "f4v ", "f4p ", "f4a ", "f4b ",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _movBrands = new[] { "qt  " }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _m4aBrands = new[] { "M4A ", "M4B ", "M4P " }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _tgpBrands = new[]
    {
        "3gp4", "3gp5", "3gp6", "3gp7", "3gp8", "3gp9",
        "3gs7", "3gr6", "3gh9", "3gm9", "3gr9",
        "3gpp",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _tg2Brands = new[] { "3g2a", "3g2b", "3g2c" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _mj2Brands = new[] { "mjp2", "mj2s" }.ToFrozenSet(StringComparer.Ordinal);

    private static bool BrandsMatch(FFprobeUtils.VideoFileInfo info, FrozenSet<string> acceptedBrands)
    {
        // The major_brand alone identifies the primary format. compatible_brands often overlap (e.g., 3gp files commonly include
        // "isom"), so they cannot be used to determine the actual format. If no major_brand is present we accept the file as a
        // best-effort fallback.
        if (info.MajorBrand is null)
            return true;

        return acceptedBrands.Contains(info.MajorBrand);
    }

    private sealed class MP4Impl : MediaContainerFormat
    {
        public override bool SupportsWriting => true;
        public override string Name => IsoBmffName;
        public override IReadOnlyList<string> Extensions { get; } = [".mp4"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMovGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(BrandsMatch(info, _mp4Brands));
    }

    private sealed class MovImpl : MediaContainerFormat
    {
        public override string Name => IsoBmffName;
        public override IReadOnlyList<string> Extensions { get; } = [".mov"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMovGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(BrandsMatch(info, _movBrands));
    }

    private sealed class M4AImpl : MediaContainerFormat
    {
        public override string Name => IsoBmffName;
        public override IReadOnlyList<string> Extensions { get; } = [".m4a"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMovGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(BrandsMatch(info, _m4aBrands));
    }

    private sealed class TgpImpl : MediaContainerFormat
    {
        public override string Name => IsoBmffName;
        public override IReadOnlyList<string> Extensions { get; } = [".3gp"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMovGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(BrandsMatch(info, _tgpBrands));
    }

    private sealed class Tg2Impl : MediaContainerFormat
    {
        public override string Name => IsoBmffName;
        public override IReadOnlyList<string> Extensions { get; } = [".3g2"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMovGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(BrandsMatch(info, _tg2Brands));
    }

    private sealed class Mj2Impl : MediaContainerFormat
    {
        public override string Name => IsoBmffName;
        public override IReadOnlyList<string> Extensions { get; } = [".mj2"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMovGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(BrandsMatch(info, _mj2Brands));
    }

    private sealed class MkvImpl : MediaContainerFormat
    {
        public override string Name => MatroskaName;
        public override IReadOnlyList<string> Extensions { get; } = [".mkv"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMatroskaGroupDemuxing;

        internal override async ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
        {
            string? docType = await EbmlHeaderReader.ReadDocTypeAsync(filePath, cancellationToken).ConfigureAwait(false);
            return docType == "matroska";
        }
    }

    private sealed class WebMImpl : MediaContainerFormat
    {
        public override string Name => MatroskaName;
        public override IReadOnlyList<string> Extensions { get; } = [".webm"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMatroskaGroupDemuxing;

        internal override async ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
        {
            string? docType = await EbmlHeaderReader.ReadDocTypeAsync(filePath, cancellationToken).ConfigureAwait(false);
            return docType == "webm";
        }
    }

    private sealed class AviImpl : MediaContainerFormat
    {
        public override string Name => "avi";
        public override IReadOnlyList<string> Extensions { get; } = [".avi"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsAviDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(true);
    }

    private sealed class TSImpl : MediaContainerFormat
    {
        public override string Name => "mpegts";
        public override IReadOnlyList<string> Extensions { get; } = [".ts"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMpegTSGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
        {
            // Standard MPEG-TS uses 188 byte packets. If ffprobe did not report a packet size, accept (cannot disprove).
            return new(info.PacketSize is null or 188);
        }
    }

    private sealed class M2tsImpl : MediaContainerFormat
    {
        public override string Name => "mpegts";
        public override IReadOnlyList<string> Extensions { get; } = [".m2ts", ".mts"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMpegTSGroupDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
        {
            // Blu-ray style MPEG-TS uses 192 byte packets (188 byte packet + 4 byte timing prefix).
            return new(info.PacketSize == 192);
        }
    }

    private sealed class MpegImpl : MediaContainerFormat
    {
        public override string Name => "mpeg";
        public override IReadOnlyList<string> Extensions { get; } = [".mpeg", ".mpg"];
        internal override bool HasSupportedDemuxer => FFprobeUtils.Configuration.SupportsMpegDemuxing;

        internal override ValueTask<bool> MatchesContentAsync(FFprobeUtils.VideoFileInfo info, IAbsoluteFilePath filePath, CancellationToken cancellationToken)
            => new(true);
    }
}
