#!/usr/bin/env bash

# Writes ffmpeg-linux-x64.zip (ffmpeg + ffprobe at the zip root) into $FFMPEG_OUTPUT_DIR.

# Exit on any error, unset variable, or failed pipe.
set -euo pipefail

# Fail early if the workflow did not provide the output directory.
: "${FFMPEG_OUTPUT_DIR:?FFMPEG_OUTPUT_DIR must be set by the workflow}"

# Ensure the output directory exists.
mkdir -p "$FFMPEG_OUTPUT_DIR"

# Build ffmpeg for Linux (output goes to ~/Clones/ffmpeg-build/packages/FFmpeg-release-X.Y)
sudo apt -y install build-essential curl zip
mkdir -p ~/Clones
cd ~/Clones
git clone https://github.com/markus-perl/ffmpeg-build-script.git
cd ffmpeg-build-script
git apply "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/build-ffmpeg.patch" # temorary workaround to ensure we're using a new enough version of x265
cd ~/Clones
mkdir -p ffmpeg-build
cd ffmpeg-build
bash ../ffmpeg-build-script/build-ffmpeg --build --enable-gpl-and-non-free

# Locate the built package dir (FFmpeg-release-X.Y, where X.Y is the version).
ffmpeg_package_dir=$(find ~/Clones/ffmpeg-build/packages -maxdepth 1 -type d -name 'FFmpeg-release-*' | head -n 1)
if [[ -z "$ffmpeg_package_dir" ]]; then
  echo "Could not find FFmpeg-release-* package directory" >&2
  exit 1
fi

# Zip ffmpeg and ffprobe at the zip root for Linux.
zip -j "$FFMPEG_OUTPUT_DIR/ffmpeg-linux-x64.zip" "$ffmpeg_package_dir/ffmpeg" "$ffmpeg_package_dir/ffprobe"
