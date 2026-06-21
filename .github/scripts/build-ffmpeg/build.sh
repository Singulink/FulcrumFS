#!/usr/bin/env bash

# Writes ffmpeg-linux-x64.zip and ffmpeg-windows-x64.zip (ffmpeg + ffprobe at the zip root) into $FFMPEG_OUTPUT_DIR.

# Exit on any error, unset variable, or failed pipe.
set -euo pipefail

# Fail early if the workflow did not provide the output directory.
: "${FFMPEG_OUTPUT_DIR:?FFMPEG_OUTPUT_DIR must be set by the workflow}"

# Ensure the output directory exists.
mkdir -p "$FFMPEG_OUTPUT_DIR"

# Build ffmpeg for Linux (output goes to ~/Clones/ffmpeg-build/packages/FFmpeg-release-X.Y)
mkdir -p ~/Clones
cd ~/Clones
sudo apt -y install build-essential curl zip
SKIPINSTALL=yes bash <(curl -s "https://raw.githubusercontent.com/markus-perl/ffmpeg-build-script/master/web-install-gpl-and-non-free.sh?v1")

# Locate the built package dir (FFmpeg-release-X.Y, where X.Y is the version).
ffmpeg_package_dir=$(find ~/Clones/ffmpeg-build/packages -maxdepth 1 -type d -name 'FFmpeg-release-*' | head -n 1)
if [[ -z "$ffmpeg_package_dir" ]]; then
  echo "Could not find FFmpeg-release-* package directory" >&2
  exit 1
fi

# Zip all package files (ffmpeg, ffprobe, and any shared libs) at the zip root for Linux.
( cd "$ffmpeg_package_dir" && zip -r "$FFMPEG_OUTPUT_DIR/ffmpeg-linux-x64.zip" . )

# Build for Windows x64 from Linux x64 (run after the first one) - result goes into '~/Clones/ffmpeg-windows-build-helpers/sandbox/win64/ffmpeg_git_with_fdk_aac' (ffmpeg.exe & ffprobe.exe are self contained)
cd ~/Clones
git clone https://github.com/rdp/ffmpeg-windows-build-helpers.git
cd ffmpeg-windows-build-helpers
sudo apt-get -y install subversion ragel curl texinfo g++ ed bison flex cvs yasm automake libtool autoconf gcc cmake git make pkg-config zlib1g-dev unzip pax nasm gperf autogen bzip2 autoconf-archive p7zip-full meson clang python3 python3-distutils-extra python3-setuptools
sudo ln -s /usr/bin/python3 /usr/bin/python
cp --update=none cross_compile_ffmpeg.sh cross_compile_ffmpeg.sh.bak # make a copy of the original file before we add our workarounds
sed -i -e '/^[[:space:]]*config_options+=" --enable-libharfbuzz"[[:space:]]*$/d' -e '/^[[:space:]]*build_harfbuzz[[:space:]]*$/d' cross_compile_ffmpeg.sh # libharfbuzz doesn't seem to build successfully, just disable it for now
sed -i -e '/^[[:space:]]*config_options+=" --enable-libbluray"[[:space:]]*$/d' -e '/^[[:space:]]*build_libbluray # Needs libxml >= 2.6, freetype, fontconfig. Uses dlfcn.[[:space:]]*$/d' cross_compile_ffmpeg.sh # libbluray doesn't seem to build successfully, just disable it for now
sed -i -e '/^[[:space:]]*config_options+=" --enable-libflite"[[:space:]]*$/d' -e '/^[[:space:]]*build_libflite[[:space:]]*$/d' cross_compile_ffmpeg.sh # libflite doesn't seem to download successfully, just disable it for now
sed -i -e "/^[[:space:]]*build_vamp_plugin # Needs libsndfile for 'vamp-simple-host.exe' \[disabled\].[[:space:]]*$/d" cross_compile_ffmpeg.sh # vamp doesn't seem to download successfully, just disable it for now
sed -i 's/--enable-librubberband//g' cross_compile_ffmpeg.sh
sed -i '/^[[:space:]]*build_librubberband #/d' cross_compile_ffmpeg.sh # this depends on vamp
sed -i 's/--enable-libass//g' cross_compile_ffmpeg.sh
sed -i '/^[[:space:]]*build_libass #/d' cross_compile_ffmpeg.sh # this depends on harfbuzz
sed -i 's/^[[:space:]]*config_options+=" --enable-decklink".*/true/' cross_compile_ffmpeg.sh # the decklink stuff never seems to work
./cross_compile_ffmpeg.sh --enable-gpl=y --disable-nonfree=n --compiler-flavors=win64

# Locate the Windows build output dir (self-contained ffmpeg.exe & ffprobe.exe).
ffmpeg_windows_dir=~/Clones/ffmpeg-windows-build-helpers/sandbox/win64/ffmpeg_git_with_fdk_aac
if [[ ! -f "$ffmpeg_windows_dir/ffmpeg.exe" ]]; then
  echo "Could not find Windows ffmpeg.exe in $ffmpeg_windows_dir" >&2
  exit 1
fi

# Zip the self-contained Windows binaries at the zip root.
zip -j "$FFMPEG_OUTPUT_DIR/ffmpeg-windows-x64.zip" "$ffmpeg_windows_dir/ffmpeg.exe" "$ffmpeg_windows_dir/ffprobe.exe"
