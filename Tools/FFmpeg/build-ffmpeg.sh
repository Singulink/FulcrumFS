#!/usr/bin/env bash

# Builds ffmpeg and ffprobe for Linux or macOS with everything FulcrumFS.Videos uses (including libfdk_aac), checks them with check-ffmpeg.sh and copies them
# into an output directory.
#
# Usage: build-ffmpeg.sh [options] <output directory>
#
#   --ffmpeg-version VERSION  The FFmpeg release to build, e.g. 9.0.2, or "snapshot" for FFmpeg's current master. The default is the latest release of the
#                             FFmpeg major version the build script supports, looked up when the build starts.
#   --script-ref REF          The tag or branch of ffmpeg-build-script to build with. The default is the head of its default branch.
#   --work-dir DIR            The directory to build in (default: ~/fulcrumfs-ffmpeg-build). It must not exist, unless an earlier run of this script made it,
#                             in which case it is deleted and made again. It is left in place afterwards (except on CI), and can then be deleted.
#   --install-prerequisites   Install the packages the build needs first, with apt-get (Debian, Ubuntu), dnf (Fedora, RHEL) or Homebrew (macOS). Uses sudo
#                             when not run as root.
#
# The build is done by ffmpeg-build-script (https://github.com/markus-perl/ffmpeg-build-script), which downloads and compiles FFmpeg and the libraries it
# links statically (all but the C and C++ runtime libraries on Linux, and the system frameworks on macOS). Building takes from half an hour to a couple of
# hours, and over 10 GB of disk space. On Linux the binaries need C and C++ runtime libraries at least as new as the build machine's, so build on the
# distribution you deploy to (or an older one). Hardware acceleration is compiled in when what it needs is found at build time: see the FFmpeg setup guide of
# the FulcrumFS documentation.
#
# The result uses libfdk_aac, which makes it non-free: it may be used, but not redistributed. See the guide for what that means.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  echo "Usage: $0 [--ffmpeg-version VERSION] [--script-ref REF] [--work-dir DIR] [--install-prerequisites] <output directory>" >&2
  exit 2
}

ffmpeg_version=''
script_ref=''
work_dir="$HOME/fulcrumfs-ffmpeg-build"
install_prerequisites=0
output_dir=''

while (( $# > 0 )); do
  case "$1" in
    --ffmpeg-version) (( $# > 1 )) || usage; ffmpeg_version="$2"; shift ;;
    --script-ref) (( $# > 1 )) || usage; script_ref="$2"; shift ;;
    --work-dir) (( $# > 1 )) || usage; work_dir="$2"; shift ;;
    --install-prerequisites) install_prerequisites=1 ;;
    -*) usage ;;
    *)
      [[ -z "$output_dir" ]] || usage
      output_dir="$1"
      ;;
  esac
  shift
done

[[ -n "$output_dir" ]] || usage

# Run privileged commands with sudo unless already root (as in a container build, where sudo is usually not installed).
sudo=''
if [[ "$(id -u)" != 0 ]]; then
  sudo='sudo'
fi

# Install the prerequisites. ffmpeg-build-script compiles its own pkg-config, nasm, yasm, autotools and cmake; what it needs from the host beyond a compiler is
# python3, meson and ninja, without which it silently leaves dav1d (the AV1 decoder the video processor requires) out of the build.
if (( install_prerequisites )); then
  if [[ "$(uname)" == "Darwin" ]]; then
    # A formula that is installed already can still make brew exit non-zero (for example when it cannot link over a file of the runner image), so the
    # tools are checked for below instead of trusting brew's exit code.
    brew install curl zip meson ninja || echo "brew install reported a problem; checking for the tools themselves." >&2
  elif command -v apt-get > /dev/null; then
    $sudo apt-get update
    $sudo env DEBIAN_FRONTEND=noninteractive apt-get -y install build-essential ca-certificates curl git zip tar xz-utils file python3 python3-pip \
      meson ninja-build
  elif command -v dnf > /dev/null; then
    $sudo dnf -y install gcc gcc-c++ make curl git tar gzip xz diffutils findutils file python3 python3-pip meson ninja-build
  else
    echo "--install-prerequisites knows apt-get, dnf and Homebrew, and found none of them: install the tools listed below yourself." >&2
  fi
fi

missing=()
for tool in git curl make tar python3 meson ninja; do
  command -v "$tool" > /dev/null || missing+=("$tool")
done

# macOS' tar unpacks .xz archives itself; GNU tar runs xz.
if [[ "$(uname)" != "Darwin" ]] && ! command -v xz > /dev/null; then
  missing+=(xz)
fi

if ! command -v c++ > /dev/null && ! command -v g++ > /dev/null; then
  missing+=("a C++ compiler")
fi

if (( ${#missing[@]} > 0 )); then
  echo "The build needs tools that are not installed: ${missing[*]}." >&2
  if [[ "$(uname)" == "Darwin" ]]; then
    echo "Install the Xcode command line tools (xcode-select --install) and then 'brew install meson ninja'," >&2
    echo "or run this script with --install-prerequisites." >&2
  else
    echo "Run this script with --install-prerequisites, or install them yourself (on Debian and Ubuntu:" >&2
    echo "sudo apt-get install build-essential curl git tar xz-utils python3 meson ninja-build)." >&2
  fi
  exit 1
fi

# The build starts from a clean slate. A work directory that an earlier run of this script made (it has the marker file) is deleted; any other directory is
# someone's own, so it is never deleted: the build refuses to start instead.
marker_name='.fulcrumfs-ffmpeg-build'
if [[ -e "$work_dir" ]]; then
  if [[ ! -f "$work_dir/$marker_name" ]]; then
    echo "$work_dir exists already and was not made by this script. Remove it yourself, or pass --work-dir with a directory that does not exist." >&2
    exit 1
  fi

  echo "Removing $work_dir, left by an earlier run."
  rm -rf "$work_dir"
fi

mkdir -p "$work_dir"
work_dir="$(cd "$work_dir" && pwd)"
touch "$work_dir/$marker_name"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

# Unless --script-ref says otherwise, the build script is cloned at the head of its default branch and used as it is: it builds the latest release of the
# FFmpeg major version it supports (looked up at ffmpeg.org at the start of every build), with the versions of the libraries its maintainers currently pin.
# Nothing here patches it, since a patch stops applying the day upstream restructures.
cd "$work_dir"
git clone --depth 1 ${script_ref:+--branch "$script_ref"} https://github.com/markus-perl/ffmpeg-build-script.git
echo "ffmpeg-build-script is at commit $(git -C ffmpeg-build-script rev-parse --short HEAD) ($(git -C ffmpeg-build-script log -1 --format=%cd --date=short))."

# The build tree goes next to the script's clone, so it can be deleted with the work directory. The binaries end up in ffmpeg-build/workspace/bin.
mkdir -p ffmpeg-build
cd ffmpeg-build
bash ../ffmpeg-build-script/build-ffmpeg --build --enable-gpl-and-non-free --skip-install ${ffmpeg_version:+"--ffmpeg-version=$ffmpeg_version"}

workspace_bin="$work_dir/ffmpeg-build/workspace/bin"
for exe in ffmpeg ffprobe; do
  if [[ ! -x "$workspace_bin/$exe" ]]; then
    echo "Could not find $exe in $workspace_bin" >&2
    exit 1
  fi
done

cp "$workspace_bin/ffmpeg" "$workspace_bin/ffprobe" "$output_dir/"
chmod +x "$output_dir/ffmpeg" "$output_dir/ffprobe"
echo "Copied ffmpeg and ffprobe to $output_dir."

# Check the result as the library will, requiring the optional features too: a build made here should have everything.
echo
bash "$script_dir/check-ffmpeg.sh" --strict "$output_dir"

# On CI, delete the build tree now that the binaries have been copied out of it. CI runners have very limited disk space (generally ~14GB), and the sources
# plus build artifacts take up a large chunk of it. Anywhere else it is kept, so that it can be looked into.
echo
if [[ -n "${CI:-}" ]]; then
  echo "Removing the build tree to free up disk space (CI only)."
  rm -rf "$work_dir"
else
  echo "The build tree is in $work_dir; delete it once you no longer need it."
fi
