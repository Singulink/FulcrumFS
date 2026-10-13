# FFmpeg Scripts

Scripts for getting an FFmpeg build that `Singulink.FulcrumFS.Videos` can use. See the [Setting Up FFmpeg](https://www.singulink.com/Docs/FulcrumFS/articles/guides/ffmpeg-setup.html) guide ([source](../../Docs/articles/guides/ffmpeg-setup.md)) for what a build needs, how to use these scripts, and the licensing of the builds they make.

| Script | What it does |
| --- | --- |
| `check-ffmpeg.sh` | Checks a directory containing `ffmpeg` and `ffprobe` for everything the library uses, and reports which hardware acceleration modes work on the machine (bash: Linux, macOS). |
| `check-ffmpeg.ps1` | The same, in PowerShell (Windows PowerShell 5.1, or PowerShell 7 on any OS). |
| `build-ffmpeg.sh` | Builds a complete `ffmpeg` and `ffprobe` for Linux or macOS with [ffmpeg-build-script](https://github.com/markus-perl/ffmpeg-build-script), checks them and copies them into an output directory. |
| `build-ffmpeg.ps1` | Builds a complete, statically linked `ffmpeg.exe` and `ffprobe.exe` for Windows x64 with [media-autobuild_suite](https://github.com/m-ab-s/media-autobuild_suite), checks them and copies them into an output directory. |

The `windows` folder holds the answers and option files `build-ffmpeg.ps1` gives media-autobuild_suite. The builds include `libfdk_aac`, which makes them non-redistributable: see the guide.

```bash
./build-ffmpeg.sh --install-prerequisites ~/ffmpeg
./check-ffmpeg.sh ~/ffmpeg
```

```powershell
.\build-ffmpeg.ps1 C:\Tools\ffmpeg
.\check-ffmpeg.ps1 C:\Tools\ffmpeg
```

FulcrumFS's CI builds the FFmpeg it tests with these scripts (through `.github/scripts/build-ffmpeg`, which retries a build that fails, since the build downloads from many servers). What the check scripts look for mirrors `Source/FulcrumFS.Videos/FFprobeUtils.cs`: keep them in step with it.
