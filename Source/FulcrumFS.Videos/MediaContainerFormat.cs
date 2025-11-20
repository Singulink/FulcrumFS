namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for media container file formats used in video processing.
/// </summary>
public abstract class MediaContainerFormat
{
    /// <summary>
    /// Gets the MP4 media container format.
    /// Note: since the file structure of mp4, mov, m4a, 3gp, 3g2, and mj2 are all the same, they can all be matched by this format.
    /// </summary>
    public static MediaContainerFormat MP4 { get; } = new MP4Impl();

    /// <inheritdoc cref="MP4" />
    public static MediaContainerFormat Mov => MP4;

    /// <summary>
    /// Gets the MKV media container format.
    /// Note: since the file structure of mkv, and webm are the same, they can both be matched by this format.
    /// </summary>
    public static MediaContainerFormat Mkv { get; } = new MKVImpl();

    /// <inheritdoc cref="Mkv" />
    public static MediaContainerFormat WebM => Mkv;

    /// <summary>
    /// Gets the AVI media container format.
    /// </summary>
    public static MediaContainerFormat Avi { get; } = new AviImpl();

    /// <summary>
    /// Gets the TS media container format.
    /// Note: since the file structure of ts, mts, and m2ts are all the same, they can all be matched by this format.
    /// </summary>
    public static MediaContainerFormat TS { get; } = new TSImpl();

    /// <summary>
    /// Gets the MPEG media container format.
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
    /// Gets a value indicating whether this container format supports being written to, as we only implement writing for some formats.
    /// </summary>
    public virtual bool SupportsWriting => false;

    /// <summary>
    /// Gets the name of the codec as used in ffprobe output (format_name).
    /// </summary>
    public abstract string Name { get; }

    private sealed class MP4Impl : MediaContainerFormat
    {
        public override bool SupportsWriting => true;
        public override string Name => "mov,mp4,m4a,3gp,3g2,mj2";
    }

    private sealed class MKVImpl : MediaContainerFormat
    {
        public override string Name => "matroska,webm";
    }

    private sealed class AviImpl : MediaContainerFormat
    {
        public override string Name => "avi";
    }

    private sealed class TSImpl : MediaContainerFormat
    {
        public override string Name => "mpegts";
    }

    private sealed class MpegImpl : MediaContainerFormat
    {
        public override string Name => "mpeg";
    }
}
