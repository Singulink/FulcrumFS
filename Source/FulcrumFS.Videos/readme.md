To use this library, you will need a build of ffmpeg. It must support the following features:
- libx264 / libx265 encoders (based on your primary result video codec).
- Native (built-in to ffmpeg) aac encoder.
- (Optional, but strongly preferred) LibFDK-AAC encoder.
- Native (built-in to ffmpeg) MP4 muxer.
- The following filters (usually included by default): zscale, scale, fps, tonemap, format, bwdif, setsar.
- Decoders depending on your enabled source codecs: mpeg1video, mpeg2video, mpeg4, h263, h264, hevc, vvc, vp8, vp9, avi, aac, mp2, mp3, vorbis, opus.
- Demuxers depending on your enabled source formats: mov (and related, like mp4), matroska (and related, like webm), avi, mpegts, mpeg.
- If `TryPreserveUnrecognizedStreams = true`, then the mov_text and dvdsub encoders are also needed.
