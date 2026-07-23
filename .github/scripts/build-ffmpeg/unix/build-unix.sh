#!/usr/bin/env bash

# Copies the ffmpeg and ffprobe binaries into $FFMPEG_OUTPUT_DIR.

# Exit on any error, unset variable, or failed pipe.
set -euo pipefail

# Get directory of this script.
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Fail early if the workflow did not provide the output directory.
: "${FFMPEG_OUTPUT_DIR:?FFMPEG_OUTPUT_DIR must be set by the workflow}"

# Ensure the output directory exists.
mkdir -p "$FFMPEG_OUTPUT_DIR"

# Remove any leftover state from a previous (failed) attempt so a retry starts from a clean slate
# (e.g. a partial git clone, an already-applied patch, or a partial build tree).
rm -rf ~/Clones

# Install pre-requisites
if [[ "$(uname)" == "Darwin" ]]; then
  brew install curl zip
else
  sudo apt -y install build-essential curl zip
fi

# Build ffmpeg for Unix (output goes to ~/Clones/ffmpeg-build/packages/FFmpeg-release-X.Y)
mkdir -p ~/Clones
cd ~/Clones
git clone https://github.com/markus-perl/ffmpeg-build-script.git
cd ffmpeg-build-script
git apply "$script_dir/build-ffmpeg.patch" # temporary workaround to ensure we're using a new enough version of x265
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

# Copy ffmpeg and ffprobe into the output directory.
cp "$ffmpeg_package_dir/ffmpeg" "$ffmpeg_package_dir/ffprobe" "$FFMPEG_OUTPUT_DIR/"

# Ad-hoc sign the copied binaries on macOS so Gatekeeper/syspolicyd doesn't re-assess them on
# every launch, which adds significant per-invocation overhead when tests spawn them repeatedly.
if [[ "$(uname)" == "Darwin" ]]; then
  codesign --force --sign - "$FFMPEG_OUTPUT_DIR/ffmpeg" "$FFMPEG_OUTPUT_DIR/ffprobe"
fi
