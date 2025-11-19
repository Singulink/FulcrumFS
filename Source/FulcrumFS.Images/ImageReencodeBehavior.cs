namespace FulcrumFS.Images;

/// <summary>
/// Specifies image re-encoding behavior, ie. whether and when an image should be re-encoded and when the original image bytes should be preserved.
/// </summary>
/// <remarks>
/// <para>Unconditional rules:</para>
/// <list type="bullet">
///   <item>
///     <description>
///       Re-encoding is skipped and the original bytes are preserved if the image processor can prove that the output would not meaningfully change (in content
///       or byte size).
///     </description>
///   </item>
///   <item>
///     <description>
///       If no changes are made to the image format, pixel data, or metadata during processing, any re-encoded result that is larger in byte size than the
///       source is always discarded and the original source bytes are preserved.
///     </description>
///   </item>
///   <item>
///     <description>
///       If changes are made to the image format or pixel data, re-encoding always occurs and the re-encoded output is used.
///     </description>
///   </item>
/// </list>
/// <para>
/// These rules apply regardless of the configured behavior. The enum values control when re-encoding is performed and how to prefer source v.s. re-encoded
/// bytes in other scenarios (for example, when only metadata is changed).</para>
/// </remarks>
public enum ImageReencodeBehavior
{
    /// <summary>
    /// Re-encode by default, and discard the re-encoded output if it is larger (by byte size) and no metadata changes were made during processing. If metadata
    /// changes were made, use the re-encoded output to preserve those changes. Subject to the unconditional rules specified in the <see
    /// cref="ImageReencodeBehavior"/> remarks.
    /// </summary>
    DiscardLargerUnlessMetadataChanged,

    /// <summary>
    /// Re-encode by default, and discard the re-encoded output if it is larger (by byte size) even if metadata changes were made during processing. Use only
    /// when preserving processed metadata changes is not required (e.g. when metadata is being stripped solely to reduce size, not for privacy reasons).
    /// Subject to the unconditional rules specified in the <see cref="ImageReencodeBehavior"/> remarks.
    /// </summary>
    DiscardLargerEvenIfMetadataChanged,

    /// <summary>
    /// Skip re-encoding when no metadata changes were made during processing, and in the case of lossy formats, the requested result quality is greater than or
    /// equal to the source quality. Differences in lossless compression level do not trigger re-encoding; the source’s compression level is preserved when
    /// encoding is skipped. Subject to the unconditional rules specified in the <see cref="ImageReencodeBehavior"/> remarks.
    /// </summary>
    SkipUnlessMetadataOrQualityChanged,
}
