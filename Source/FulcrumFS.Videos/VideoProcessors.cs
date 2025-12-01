namespace FulcrumFS.Videos;

/// <summary>
/// Provides pre-existing video processor configurations.
/// </summary>
public static class VideoProcessors
{
    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessor"/> that always re-encodes to a standardized H.264 video and AAC audio streams in an MP4
    /// container, while preserving all metadata other than thumbnails by default.
    /// </summary>
    public static VideoProcessor StandardizedH264AACMP4 { get; } = new VideoProcessor()
    {
        SourceVideoCodecs = VideoCodec.AllSourceCodecs,
        SourceAudioCodecs = AudioCodec.AllSourceCodecs,
        SourceFormats = MediaContainerFormat.AllSourceFormats,
        VideoStreamOptions = VideoStreamProcessingOptions.StandardizedH264,
        AudioStreamOptions = AudioStreamProcessingOptions.StandardizedAAC,
        ResultFormats = [MediaContainerFormat.MP4],
        ForceProgressiveDownload = true,
        PreserveUnrecognizedStreams = null,
        StripMetadata = StripVideoMetadataMode.ThumbnailOnly,
        AudioSourceValidation = AudioStreamValidationOptions.None,
        VideoSourceValidation = VideoStreamValidationOptions.None,
        ProgressCallback = null,
    };

    /// <summary>
    /// Gets a predefined instance of <see cref="VideoProcessor"/> that always preserves the original streams when possible.
    /// </summary>
    public static VideoProcessor Preserve { get; } = new VideoProcessor()
    {
        SourceVideoCodecs = VideoCodec.AllSourceCodecs,
        SourceAudioCodecs = AudioCodec.AllSourceCodecs,
        SourceFormats = MediaContainerFormat.AllSourceFormats,
        VideoStreamOptions = VideoStreamProcessingOptions.Preserve,
        AudioStreamOptions = AudioStreamProcessingOptions.Preserve,
        ResultFormats = MediaContainerFormat.AllSourceFormats,
        ForceProgressiveDownload = false,
        PreserveUnrecognizedStreams = true,
        StripMetadata = StripVideoMetadataMode.ThumbnailOnly,
        AudioSourceValidation = AudioStreamValidationOptions.None,
        VideoSourceValidation = VideoStreamValidationOptions.None,
        ProgressCallback = null,
    };
}
