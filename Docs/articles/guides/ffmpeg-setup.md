<div class="article">

# Setting Up FFmpeg

`Singulink.FulcrumFS.Videos` does its work by running the `ffmpeg` and `ffprobe` executables of an FFmpeg build you provide, and that build needs a few libraries that not every build has. This guide covers what a build needs, how to check one, and how to make one with the scripts in the repository's [`Tools/FFmpeg`](https://github.com/Singulink/FulcrumFS/tree/main/Tools/FFmpeg) folder, which build a complete one on Windows, Linux or macOS without any configuration. Once you have a build, see [Video Processing](video-processing.md) for configuring the library with it.

## What a Build Needs

When you construct a <xref:FulcrumFS.Videos.VideoProcessor> or <xref:FulcrumFS.Videos.VideoFrameExtractionProcessor>, the library asks `ffprobe` what the build supports and throws <xref:System.NotSupportedException> if something its options need is missing. What it uses falls into three groups.

**Required** by every processor:

- The `libx264` encoder for H.264 output and the `libx265` encoder for HEVC output (a build needs only the one for the codec you produce, but the predefined options use both).
- FFmpeg's own `aac` encoder, `png` encoder (for frame extraction) and `mp4` muxer.
- The `zscale` filter, which needs FFmpeg to be built with `libzimg`, and the `scale`, `fps`, `tonemap`, `format`, `bwdif`, `setsar`, `transpose`, `hflip`, `vflip` and `sidedata` filters, which FFmpeg includes by default.

**Required for the source codecs and formats accepted by default.** A processor accepts every source codec and container format the library knows unless its options narrow them, and it needs a decoder or demuxer for each one it accepts:

- Video decoders: `h264`, `hevc`, `mpeg1video`, `mpeg2video`, `mpeg4`, `h263` and `vvc`; `vp8` and `vp9` together with the `libvpx` and `libvpx-vp9` decoders (from `libvpx`); `av1` together with the `libdav1d` decoder (from `dav1d`).
- Audio decoders: `aac`, `mp2`, `mp3`, `vorbis`, `opus`, `amr_nb` and `amr_wb`.
- Demuxers: `mov` (MP4, MOV, M4A, 3GP, 3G2, MJ2), `matroska` (MKV, WebM), `avi`, `mpegts` (TS, M2TS) and `mpeg`.

If a build lacks one of these, you can still use it by leaving the codec or format out of <xref:FulcrumFS.Videos.VideoProcessingOptions.SourceVideoCodecs>, <xref:FulcrumFS.Videos.VideoProcessingOptions.SourceAudioCodecs> or <xref:FulcrumFS.Videos.VideoProcessingOptions.SourceFormats>; uploads in that codec or format are then rejected. An older FFmpeg without a VVC decoder, for example, works once <xref:FulcrumFS.Videos.VideoCodec.H266> is left out.

**Optional**, used when present:

- The `libfdk_aac` encoder (from Fraunhofer's FDK AAC library), which gives noticeably better AAC audio than FFmpeg's own encoder, especially at low bitrates. Without it, the library uses FFmpeg's `aac` encoder. See [Licensing](#licensing) for the catch.
- The `mov_text` and `dvdsub` encoders, which are needed when <xref:FulcrumFS.Videos.VideoProcessingOptions.TryPreserveUnrecognizedStreams> is set. FFmpeg includes them by default.
- Hardware acceleration, when the processing options ask for it. See [Hardware Acceleration](#hardware-acceleration).

`ffmpeg` and `ffprobe` must come from the same build: the library checks what `ffprobe` supports and transcodes with `ffmpeg`.

## Checking a Build

`check-ffmpeg.sh` (bash, for Linux and macOS) and `check-ffmpeg.ps1` (PowerShell, for Windows or PowerShell 7 anywhere) check a directory containing `ffmpeg` and `ffprobe` for all of the above, the same way the library does, and report what is missing:

```bash
./check-ffmpeg.sh /opt/ffmpeg
```

```powershell
.\check-ffmpeg.ps1 C:\Tools\ffmpeg
```

```
Checking /opt/ffmpeg
  ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers
  ffprobe version 9.0.2 Copyright (c) 2007-2026 the FFmpeg developers

Required:
  ok       libx264 encoder          H.264 output
  ok       libx265 encoder          HEVC output
  ...
  MISSING  zscale filter            colour conversion and HDR tone mapping (needs libzimg)
  ...

Hardware acceleration (optional; used only when the processing options ask for it):
  videotoolbox  not built in
  cuda          built in, works on this machine
  ...

1 required feature(s) missing: the library's processors will refuse this build (with their default options).
```

The exit code is 0 when nothing required is missing and 1 when something is. `--strict` (`-Strict`) also fails on a missing optional feature. For each hardware acceleration mode the build has, the script decodes a short test stream with it, as the library does before using a mode, so running it on the server tells you which modes will actually work there; `--skip-hwaccel-test` (`-SkipHWAccelTest`) only lists them.

## Building with the Scripts

The build scripts make a build with everything the library uses, including `libfdk_aac`, check it with `--strict` and copy `ffmpeg` and `ffprobe` into the directory you give them. Both executables link the libraries they use statically (on Linux, all but the system's C and C++ runtime libraries), so the output directory is all you need to deploy. The scripts drive two well-established build systems: [ffmpeg-build-script](https://github.com/markus-perl/ffmpeg-build-script) on Linux and macOS, and [media-autobuild_suite](https://github.com/m-ab-s/media-autobuild_suite) on Windows. Each downloads and compiles FFmpeg and its libraries from source, which takes a while: allow from half an hour to a few hours, depending on the machine.

To get the scripts, clone the [repository](https://github.com/Singulink/FulcrumFS) or copy the [`Tools/FFmpeg`](https://github.com/Singulink/FulcrumFS/tree/main/Tools/FFmpeg) folder; the scripts use the other files in it.

Both scripts build in a work directory of their own (by default `~/fulcrumfs-ffmpeg-build` on Linux and macOS, and `C:\ffbuild` on Windows). A script refuses to start if that directory exists and it did not make it; one left by an earlier run is deleted and made again, so a failed build can simply be run again. The work directory is left in place after a build, for looking into, and can be deleted then.

> [!NOTE]
> FulcrumFS's CI builds FFmpeg with these same scripts on every change and every week, and runs the library's tests against the result, so a change upstream that breaks them is noticed quickly.

### Linux and macOS

On Linux, the build needs a C/C++ toolchain, `git`, `curl`, `tar`, `xz`, `python3`, `meson` and `ninja`. `--install-prerequisites` installs them with `apt-get` (Debian, Ubuntu), `dnf` (Fedora, RHEL) or Homebrew (macOS), using `sudo` unless run as root. On macOS, install the Xcode command line tools first (`xcode-select --install`).

```bash
./build-ffmpeg.sh --install-prerequisites ~/ffmpeg
```

| Option | Meaning |
| --- | --- |
| `--ffmpeg-version VERSION` | The FFmpeg release to build, such as `9.0.2`, or `snapshot` for FFmpeg's current master. By default, the latest release of the major version ffmpeg-build-script supports. |
| `--script-ref REF` | The tag or branch of ffmpeg-build-script to build with. By default, the head of its default branch. |
| `--work-dir DIR` | The directory to build in. |
| `--install-prerequisites` | Install the packages the build needs first. |

On Linux, the executables link the system's C and C++ runtime libraries dynamically, and need versions of them at least as new as the ones they were built with. Build on the distribution you deploy to, or an older one; for containers, see [Building in Docker](#building-in-docker).

### Windows

The build needs only [Git](https://git-scm.com/) and around 20 GB of disk space: media-autobuild_suite installs its own MSYS2 environment and compilers into the work directory. Run the script from PowerShell (Windows PowerShell 5.1 or PowerShell 7):

```powershell
.\build-ffmpeg.ps1 C:\Tools\ffmpeg
```

| Parameter | Meaning |
| --- | --- |
| `-FFmpegVersion` | The FFmpeg release to build, such as `9.0.2`, `latest` (the default) for the latest release, or `master` for FFmpeg's current master. The libraries are always built at the heads of their repositories. |
| `-WorkDir` | The directory to build in, which must be a short path without spaces. |
| `-Cores` | The number of processors to compile with. By default, all of them. |

> [!TIP]
> An antivirus that scans files as they are written slows the build down considerably, since it writes a very large number of small files. Consider excluding the work directory from on-access scanning for the duration of the build.

### Building in Docker

To run the application in a Linux container, build FFmpeg in a stage of the image's Dockerfile, based on the .NET SDK image (which is built on the same distribution as the runtime images of the same tag), and copy the executables into the final image. Copy the `Tools/FFmpeg` folder into the build context first:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS ffmpeg
COPY Tools/FFmpeg /tools/ffmpeg
RUN bash /tools/ffmpeg/build-ffmpeg.sh --install-prerequisites --work-dir /tmp/ffmpeg-build /opt/ffmpeg && rm -rf /tmp/ffmpeg-build

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=ffmpeg /opt/ffmpeg /opt/ffmpeg
# ... the rest of your image; configure the library with /opt/ffmpeg.
```

Docker caches the stage's result, so FFmpeg is only built again when the stage, the scripts or the base image change.

## Using a Prebuilt Build

Any build that passes `check-ffmpeg` works. None of the widely available prebuilt builds include `libfdk_aac`, because a build with it cannot be redistributed (see [Licensing](#licensing)), so with one of them the library uses FFmpeg's own AAC encoder. Some are also missing required libraries. At the time of writing:

- On Windows, the "full" builds from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) and the GPL builds from [BtbN](https://github.com/BtbN/FFmpeg-Builds) include the required libraries. gyan.dev's "essentials" builds do not include `dav1d`, so they cannot read AV1 sources.
- On Linux, BtbN's GPL builds include the required libraries. Distribution packages vary in what they include, and can be too old to have a VVC decoder.
- On macOS, Homebrew's `ffmpeg` is built without `libzimg`, so it lacks the required `zscale` filter. The [homebrew-ffmpeg](https://github.com/homebrew-ffmpeg/homebrew-ffmpeg) tap builds one with it: `brew install homebrew-ffmpeg/ffmpeg/ffmpeg --with-zimg`.

BtbN's build scripts can also make a "nonfree" variant with `libfdk_aac`, for Windows or Linux, with Docker on a Linux machine.

## Hardware Acceleration

A hardware acceleration mode can only be used if FFmpeg was built with support for it, and the machine has a device and driver it works with. What the build scripts include:

- **Windows:** CUDA (NVIDIA), AMF (AMD), QSV (Intel) and D3D12 are always built in. At run time, each needs the GPU's driver.
- **macOS:** VideoToolbox is always built in.
- **Linux:** AMF and QSV are built in on x86-64. AMF needs AMD's AMF runtime on the server. QSV on Linux also needs VA-API, which is only built in when the libva development files (`libva-dev` on Debian and Ubuntu) are installed at build time, and which then makes the executables depend on `libva` (and Intel's media driver) on the server. CUDA is built in only when the CUDA toolkit (`nvcc`) is installed at build time; ffmpeg-build-script then builds for the compute capability of the machine's NVIDIA GPU, or for the one in the `CUDA_COMPUTE_CAPABILITY` environment variable when building for a different GPU.

Run `check-ffmpeg` on the server to see which modes work there. The library falls back to software when a requested mode is unavailable, so a build without acceleration still processes every file; see [Hardware Acceleration](video-processing.md#hardware-acceleration) in the video processing guide.

## Licensing

FFmpeg itself is LGPL, but a build with `libx264` and `libx265`, which the library requires, is GPL. Adding `libfdk_aac`, whose license is not compatible with the GPL, makes it what FFmpeg's configure calls "nonfree and unredistributable": you may build it and use it, but not give it to others, which includes shipping it in an installer or publishing a container image with it. The builds the scripts make are of this kind.

If you need to distribute FFmpeg with your application, use a build without `libfdk_aac` (any of the prebuilt GPL builds above, for example) and meet the GPL's terms for it. The library works the same with such a build, using FFmpeg's own AAC encoder. See [FFmpeg's license page](https://ffmpeg.org/legal.html) for the details, and take legal advice where it matters.

## Next Steps

- [Video Processing](video-processing.md) - Configuring the library with your build, and the processors and their options.

</div>
