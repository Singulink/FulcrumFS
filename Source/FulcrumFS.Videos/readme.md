To use this library, you will need a build of FFmpeg (`ffmpeg` and `ffprobe`) with the libraries it uses, which not every build has: in particular `libx264`, `libx265`, `libzimg` (for the `zscale` filter), `libvpx` and `dav1d`, and optionally (but strongly preferred) `libfdk_aac`.

The [Setting Up FFmpeg](../../Docs/articles/guides/ffmpeg-setup.md) guide lists everything the library uses and what each is for. The scripts in [`Tools/FFmpeg`](../../Tools/FFmpeg) check an existing build for all of it (`check-ffmpeg.sh` / `check-ffmpeg.ps1`), and build a complete one for Windows, Linux or macOS (`build-ffmpeg.ps1` / `build-ffmpeg.sh`).

What the library requires is checked in `FFprobeUtils.cs`; the check scripts mirror it, so keep them in step when it changes.
