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
# (e.g. a partial git clone or a partial build tree).
rm -rf ~/Clones

# Install pre-requisites. The build script compiles its own pkg-config, nasm, yasm, autotools and cmake; what it needs from the host beyond a compiler is
# python3, meson and ninja, without which it silently leaves dav1d (the AV1 decoder the video processor requires) out of the build.
if [[ "$(uname)" == "Darwin" ]]; then
  # A formula that is installed already can still make brew exit non-zero (for example when it cannot link over a file of the runner image), so the
  # tools are checked for below instead of trusting brew's exit code.
  brew install curl zip meson ninja || echo "brew install reported a problem; checking for the tools themselves." >&2
else
  sudo apt-get update
  sudo apt -y install build-essential curl zip tar xz-utils file python3 python3-pip meson ninja-build
fi

for tool in curl python3 meson ninja; do
  if ! command -v "$tool" > /dev/null; then
    echo "Required build tool '$tool' is not installed." >&2
    exit 1
  fi
done

# Build ffmpeg for Unix (the binaries end up in ~/Clones/ffmpeg-build/workspace/bin).
#
# The build script is cloned at the head of its default branch on purpose, and used as it is: it builds the latest release of the FFmpeg major version it
# supports (looked up at ffmpeg.org at the start of every build), with the versions of the libraries its maintainers currently pin. That is what keeps the
# video tests running against current FFmpeg, with its current fixes, without anyone bumping a version here. The price is that an upstream change can break
# this step, which then needs a look at what changed upstream; nothing here patches the script, since a patch stops applying the day upstream restructures
# (as the x265 version patch this script used to carry did).
mkdir -p ~/Clones
cd ~/Clones
git clone https://github.com/markus-perl/ffmpeg-build-script.git
echo "ffmpeg-build-script is at commit $(git -C ffmpeg-build-script rev-parse --short HEAD) ($(git -C ffmpeg-build-script log -1 --format=%cd --date=short))."
mkdir -p ffmpeg-build
cd ffmpeg-build
bash ../ffmpeg-build-script/build-ffmpeg --build --enable-gpl-and-non-free --skip-install

# Copy ffmpeg and ffprobe into the output directory.
workspace_bin=~/Clones/ffmpeg-build/workspace/bin
for exe in ffmpeg ffprobe; do
  if [[ ! -x "$workspace_bin/$exe" ]]; then
    echo "Could not find $exe in $workspace_bin" >&2
    exit 1
  fi
done

cp "$workspace_bin/ffmpeg" "$workspace_bin/ffprobe" "$FFMPEG_OUTPUT_DIR/"

# Say which FFmpeg was built, so a test run's log names the version its results are for. (sed reads to the end of the output, unlike head, which would end
# the pipe early and fail the script under pipefail.)
"$FFMPEG_OUTPUT_DIR/ffmpeg" -hide_banner -version | sed -n 1p

# On CI, delete the build tree now that the (statically linked) binaries have been extracted from it. CI runners have very limited disk space (generally ~14GB),
# and the sources plus build artifacts take up a large chunk of it. Only do this on CI so local runs can inspect the tree if desired.
if [[ "${CI:-}" != "" ]]; then
  echo "Removing the ffmpeg build tree to free up disk space (CI only)."
  rm -rf ~/Clones
fi
