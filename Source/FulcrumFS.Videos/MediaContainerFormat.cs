namespace FulcrumFS.Videos;

/// <summary>
/// Represents a base class for media container file formats used in video processing.
/// </summary>
public abstract class MediaContainerFormat
{
    /// <summary>
    /// Gets the 3GP media container format.
    /// </summary>
#pragma warning disable SA1300 // Element should begin with upper-case letter
    public static MediaContainerFormat _3GP { get; } = new _3GPImpl();
#pragma warning restore SA1300 // Element should begin with upper-case letter

    /// <summary>
    /// Gets the MP4 media container format.
    /// </summary>
    public static MediaContainerFormat MP4 { get; } = new MP4Impl();

    /// <summary>
    /// Gets the MOV media container format.
    /// </summary>
    public static MediaContainerFormat Mov { get; } = new MovImpl();

    /// <summary>
    /// Gets the MKV media container format.
    /// </summary>
    public static MediaContainerFormat Mkv { get; } = new MKVImpl();

    /// <summary>
    /// Gets the WebM media container format.
    /// </summary>
    public static MediaContainerFormat WebM { get; } = new WebMImpl();

    /// <summary>
    /// Gets the AVI media container format.
    /// </summary>
    public static MediaContainerFormat Avi { get; } = new AviImpl();

    /// <summary>
    /// Gets the WMV media container format.
    /// </summary>
    public static MediaContainerFormat Wmv { get; } = new WmvImpl();

    /// <summary>
    /// Gets the TS media container format.
    /// </summary>
    public static MediaContainerFormat TS { get; } = new TSImpl();

    /// <summary>
    /// Gets the MTS media container format.
    /// </summary>
    public static MediaContainerFormat MTS { get; } = new MTSImpl();

    /// <summary>
    /// Gets the M2TS media container format.
    /// </summary>
    public static MediaContainerFormat M2TS { get; } = new M2TSImpl();
}
