using System.Collections.Immutable;

namespace FulcrumFS;

#pragma warning disable SA1124 // Do not use regions

/// <content>
/// Contains the built-in singleton <see cref="FileFormat"/> instances and the registry of all built-in types.
/// </content>
public abstract partial class FileFormat
{
    #region Images

    /// <summary>Gets the JPEG image file format. Extensions: <c>.jpg</c> (primary), <c>.jpeg</c>, <c>.jfif</c>.</summary>
    public static FileFormat Jpeg { get; } = new JpegFileFormat();

    /// <summary>Gets the PNG image file format. Extensions: <c>.png</c> (primary), <c>.apng</c>.</summary>
    public static FileFormat Png { get; } = new PngFileFormat();

    /// <summary>Gets the GIF image file format. Extension: <c>.gif</c>.</summary>
    public static FileFormat Gif { get; } = new GifFileFormat();

    /// <summary>Gets the WebP image file format. Extension: <c>.webp</c>.</summary>
    public static FileFormat WebP { get; } = new WebPFileFormat();

    /// <summary>Gets the BMP image file format. Extensions: <c>.bmp</c> (primary), <c>.bm</c>, <c>.dip</c>.</summary>
    public static FileFormat Bmp { get; } = new BmpFileFormat();

    /// <summary>Gets the TIFF image file format. Extensions: <c>.tif</c> (primary), <c>.tiff</c>.</summary>
    public static FileFormat Tiff { get; } = new TiffFileFormat();

    /// <summary>Gets the HEIC image file format. Extension: <c>.heic</c>.</summary>
    public static FileFormat Heic { get; } = new IsoBmffFileFormat("HEIC", [".heic"], ["heic", "heix", "heim", "heis"]);

    /// <summary>Gets the HEIF image file format. Extensions: <c>.heif</c> (primary), <c>.heifs</c>, <c>.hif</c>.</summary>
    public static FileFormat Heif { get; } = new IsoBmffFileFormat("HEIF", [".heif", ".heifs", ".hif"], ["mif1", "msf1", "heif", "hevm", "hevs"]);

    /// <summary>Gets the AVIF image file format. Extension: <c>.avif</c>.</summary>
    public static FileFormat Avif { get; } = new IsoBmffFileFormat("AVIF", [".avif"], ["avif", "avis"]);

    #endregion

    #region ISOBMFF Media

    /// <summary>Gets the loose MP4 media file format. Extensions: <c>.mp4</c> (primary), <c>.mov</c>, <c>.m4a</c>, <c>.3gp</c>, <c>.3g2</c>, <c>.mj2</c>.</summary>
    /// <remarks>
    /// This format is less strict and does not check the brand, it leaves checking up to ffmpeg which checks for the <c>mov,mp4,m4a,3gp,3g2,mj2</c> group.
    /// </remarks>
    public static FileFormat Mp4Loose { get; } = new IsoBmffFileFormat("MP4 Loose", [".mp4", ".mov", ".m4a", ".3gp", ".3g2", ".mj2"], []);

    /// <summary>Gets the MP4 media file format. Extension: <c>.mp4</c>.</summary>
    public static FileFormat Mp4 { get; } = new IsoBmffFileFormat("MP4", [".mp4"],
    [
        "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "iso7", "iso8", "iso9",
        "mp41", "mp42", "mp4v", "mp4a",
        "avc1", "hvc1", "hev1", "dby1", "dash", "msdh", "msix", "MSNV",
        "f4v ", "f4p ", "f4a ", "f4b ",
    ]);

    /// <summary>Gets the QuickTime (MOV) media file format. Extension: <c>.mov</c>.</summary>
    public static FileFormat Mov { get; } = new IsoBmffFileFormat("QuickTime (MOV)", [".mov"], ["qt  "]);

    /// <summary>Gets the MPEG-4 audio (M4A) media file format. Extension: <c>.m4a</c>.</summary>
    public static FileFormat M4a { get; } = new IsoBmffFileFormat("M4A", [".m4a"], ["M4A ", "M4B ", "M4P "]);

    /// <summary>Gets the 3GPP (3GP) media file format. Extension: <c>.3gp</c>.</summary>
    public static FileFormat Tgp { get; } = new IsoBmffFileFormat("3GP", [".3gp"],
    [
        "3gp4", "3gp5", "3gp6", "3gp7", "3gp8", "3gp9",
        "3gs7", "3gr6", "3gh9", "3gm9", "3gr9",
        "3gpp",
    ]);

    /// <summary>Gets the 3GPP2 (3G2) media file format. Extension: <c>.3g2</c>.</summary>
    public static FileFormat Tg2 { get; } = new IsoBmffFileFormat("3G2", [".3g2"], ["3g2a", "3g2b", "3g2c"]);

    /// <summary>Gets the Motion JPEG 2000 (MJ2) media file format. Extension: <c>.mj2</c>.</summary>
    public static FileFormat Mj2 { get; } = new IsoBmffFileFormat("Motion JPEG 2000", [".mj2"], ["mjp2", "mj2s"]);

    #endregion

    #region Matroska Media

    /// <summary>Gets the Matroska (MKV) media file format. Extension: <c>.mkv</c>.</summary>
    public static FileFormat Mkv { get; } = new EbmlFileFormat("Matroska (MKV)", ".mkv", "matroska");

    /// <summary>Gets the WebM media file format. Extension: <c>.webm</c>.</summary>
    public static FileFormat WebM { get; } = new EbmlFileFormat("WebM", ".webm", "webm");

    #endregion

    #region MPEG-TS Media

    /// <summary>Gets the MPEG-TS (188-byte packet) media file format. Extension: <c>.ts</c>.</summary>
    public static FileFormat Ts { get; } = new MpegTsFileFormat("MPEG-TS", [".ts"], packetSize: 188, syncOffset: 0);

    /// <summary>Gets the MPEG-TS (192-byte packet, Blu-ray style) media file format. Extensions: <c>.m2ts</c> (primary), <c>.mts</c>.</summary>
    public static FileFormat M2ts { get; } = new MpegTsFileFormat("MPEG-TS (Blu-ray)", [".m2ts", ".mts"], packetSize: 192, syncOffset: 4);

    #endregion

    #region Other Media

    /// <summary>Gets the AVI media file format. Extension: <c>.avi</c>.</summary>
    public static FileFormat Avi { get; } = new AviFileFormat();

    /// <summary>Gets the MPEG-PS media file format. Extensions: <c>.mpg</c> (primary), <c>.mpeg</c>.</summary>
    public static FileFormat Mpeg { get; } = new MpegFileFormat();

    /// <summary>Gets the WAV audio file format. Extension: <c>.wav</c>.</summary>
    public static FileFormat Wav { get; } = new WavFileFormat();

    /// <summary>Gets the MP3 audio file format. Extension: <c>.mp3</c>.</summary>
    public static FileFormat Mp3 { get; } = new Mp3FileFormat();

    /// <summary>Gets the FLAC audio file format. Extension: <c>.flac</c>.</summary>
    public static FileFormat Flac { get; } = new FlacFileFormat();

    /// <summary>Gets the Ogg media file format. Extensions: <c>.ogg</c> (primary), <c>.oga</c>, <c>.ogv</c>, <c>.opus</c>.</summary>
    public static FileFormat Ogg { get; } = new OggFileFormat();

    #endregion

    #region Documents

    /// <summary>Gets the PDF document file format. Extension: <c>.pdf</c>.</summary>
    public static FileFormat Pdf { get; } = new PdfFileFormat();

    /// <summary>Gets the RTF document file format. Extension: <c>.rtf</c>.</summary>
    public static FileFormat Rtf { get; } = new RtfFileFormat();

    /// <summary>Gets the legacy Microsoft Word (DOC) file format. Extension: <c>.doc</c>. Validates the OLE Compound Document signature and verifies
    /// the contained document is a Word document by inspecting the directory entries for the 'WordDocument' stream.</summary>
    public static FileFormat Doc { get; } = new OleCompoundFileFormat("DOC", ".doc", OleCompoundReader.OleDocumentType.Doc);

    /// <summary>Gets the legacy Microsoft Excel (XLS) file format. Extension: <c>.xls</c>. Validates the OLE Compound Document signature and verifies
    /// the contained document is an Excel workbook by inspecting the directory entries for the 'Workbook' or 'Book' stream.</summary>
    public static FileFormat Xls { get; } = new OleCompoundFileFormat("XLS", ".xls", OleCompoundReader.OleDocumentType.Xls);

    /// <summary>Gets the legacy Microsoft PowerPoint (PPT) file format. Extension: <c>.ppt</c>. Validates the OLE Compound Document signature and
    /// verifies the contained document is a PowerPoint presentation by inspecting the directory entries for the 'PowerPoint Document' stream.</summary>
    public static FileFormat Ppt { get; } = new OleCompoundFileFormat("PPT", ".ppt", OleCompoundReader.OleDocumentType.Ppt);

    #endregion

    #region ZIP-family

    /// <summary>Gets the ZIP archive file format. Extension: <c>.zip</c>.</summary>
    public static FileFormat Zip { get; } = new ZipFileFormat();

    /// <summary>Gets the DOCX (Office Open XML Word document) file format. Extension: <c>.docx</c>.</summary>
    public static FileFormat Docx { get; } = new OpenXmlFileFormat("DOCX", ".docx", "word/document.xml");

    /// <summary>Gets the XLSX (Office Open XML spreadsheet) file format. Extension: <c>.xlsx</c>.</summary>
    public static FileFormat Xlsx { get; } = new OpenXmlFileFormat("XLSX", ".xlsx", "xl/workbook.xml");

    /// <summary>Gets the PPTX (Office Open XML presentation) file format. Extension: <c>.pptx</c>.</summary>
    public static FileFormat Pptx { get; } = new OpenXmlFileFormat("PPTX", ".pptx", "ppt/presentation.xml");

    /// <summary>Gets the ODT (OpenDocument text) file format. Extension: <c>.odt</c>.</summary>
    public static FileFormat Odt { get; } = new MimetypeZipFileFormat("ODT", ".odt", "application/vnd.oasis.opendocument.text");

    /// <summary>Gets the ODS (OpenDocument spreadsheet) file format. Extension: <c>.ods</c>.</summary>
    public static FileFormat Ods { get; } = new MimetypeZipFileFormat("ODS", ".ods", "application/vnd.oasis.opendocument.spreadsheet");

    /// <summary>Gets the ODP (OpenDocument presentation) file format. Extension: <c>.odp</c>.</summary>
    public static FileFormat Odp { get; } = new MimetypeZipFileFormat("ODP", ".odp", "application/vnd.oasis.opendocument.presentation");

    /// <summary>Gets the EPUB e-book file format. Extension: <c>.epub</c>.</summary>
    public static FileFormat Epub { get; } = new MimetypeZipFileFormat("EPUB", ".epub", "application/epub+zip");

    #endregion
}
