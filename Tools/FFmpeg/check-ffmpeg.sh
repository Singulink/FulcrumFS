#!/usr/bin/env bash

# Checks that the ffmpeg and ffprobe executables in a directory have everything FulcrumFS.Videos uses, looking for each feature the way the library does
# when a processor is constructed, and reports which hardware acceleration modes they were built with and which of those work on this machine.
#
# Usage: check-ffmpeg.sh [--strict] [--skip-hwaccel-test] <directory>
#
#   --strict             Also fail when an optional feature is missing. The build scripts use this: a build they make should have everything.
#   --skip-hwaccel-test  Do not try to decode with the hardware acceleration modes the build has; only report that it has them.
#
# The exit code is 0 when nothing required is missing, 1 when something is, and 2 when the arguments are wrong or the executables cannot be run.
#
# What is checked mirrors FFprobeUtils.cs in FulcrumFS.Videos. Keep the two in step, together with check-ffmpeg.ps1, the PowerShell version of this script.

set -uo pipefail

usage() {
  echo "Usage: $0 [--strict] [--skip-hwaccel-test] <directory containing ffmpeg and ffprobe>" >&2
  exit 2
}

strict=0
test_hwaccel=1
dir=''

while (( $# > 0 )); do
  case "$1" in
    --strict) strict=1 ;;
    --skip-hwaccel-test) test_hwaccel=0 ;;
    -*) usage ;;
    *)
      [[ -z "$dir" ]] || usage
      dir="$1"
      ;;
  esac
  shift
done

[[ -n "$dir" ]] || usage

if [[ ! -d "$dir" ]]; then
  echo "Directory not found: $dir" >&2
  exit 2
fi

# The library looks for ffmpeg and ffprobe on Unix and for ffmpeg.exe and ffprobe.exe on Windows. Both are accepted here, so that Windows binaries can be
# checked from Git Bash or WSL.
find_executable() {
  if [[ -f "$dir/$1" ]]; then
    echo "$dir/$1"
  elif [[ -f "$dir/$1.exe" ]]; then
    echo "$dir/$1.exe"
  else
    echo "$1 was not found in $dir." >&2
    exit 2
  fi
}

ffmpeg=$(find_executable ffmpeg) || exit 2
ffprobe=$(find_executable ffprobe) || exit 2

for exe in "$ffmpeg" "$ffprobe"; do
  if [[ ! -x "$exe" ]]; then
    echo "$exe is not executable (chmod +x it)." >&2
    exit 2
  fi
done

# Prints the first line of an executable's -version output, or fails if it cannot be run (for example because a shared library it needs is missing).
version_line() {
  local output
  if ! output=$("$1" -hide_banner -version 2>&1); then
    echo "$1 could not be run:" >&2
    echo "$output" >&2
    return 1
  fi
  echo "$output" | tr -d '\r' | sed -n 1p
}

ffmpeg_version=$(version_line "$ffmpeg") || exit 2
ffprobe_version=$(version_line "$ffprobe") || exit 2

echo "Checking $dir"
echo "  $ffmpeg_version"
echo "  $ffprobe_version"

# The library reads what the build supports from ffprobe but transcodes with ffmpeg, so the two must come from the same build. The version is the third word
# of the line ("ffmpeg version 9.0.2 Copyright ...").
if [[ "$(awk '{ print $3 }' <<< "$ffmpeg_version")" != "$(awk '{ print $3 }' <<< "$ffprobe_version")" ]]; then
  echo "  Warning: ffmpeg and ffprobe report different versions. Use the two executables of one build:"
  echo "  the library checks ffprobe's features and runs ffmpeg."
fi

# Prints the flags and name of each entry of a listing, i.e. the first two columns of each line after the line of dashes that ends the listing's legend.
entries() {
  "$@" | tr -d '\r' | awk 'found { print $1, $2; next } /^ *-+ *$/ { found = 1 }'
}

encoders=$(entries "$ffprobe" -hide_banner -v error -encoders)
decoders=$(entries "$ffprobe" -hide_banner -v error -decoders)
codecs=$(entries "$ffprobe" -hide_banner -v error -codecs)
muxers=$(entries "$ffprobe" -hide_banner -v error -muxers)
demuxers=$(entries "$ffprobe" -hide_banner -v error -demuxers)
pix_fmts=$(entries "$ffprobe" -hide_banner -v error -pix_fmts)

# Not every FFmpeg version ends the legend of -filters with a line of dashes, so every line is taken (the legend's lines cannot match a filter name).
filters=$("$ffprobe" -hide_banner -v error -filters | tr -d '\r' | awk '{ print $1, $2 }')

# -hwaccels prints a heading and then one method per line.
hwaccels=$("$ffmpeg" -hide_banner -v error -nostdin -hwaccels | tr -d '\r' | awk 'NR > 1 && NF { print $1 }')

# has <entries> <flags regex> <name>: whether an entry is named <name> (or lists it among comma-separated names, as -demuxers does) and has flags that match
# <flags regex>.
has() {
  awk -v flags="$2" -v name="$3" '
    $1 ~ flags {
      count = split($2, names, ",")
      for (i = 1; i <= count; i++) if (names[i] == name) found = 1
    }
    END { exit !found }' <<< "$1"
}

missing_required=0
missing_optional=0
group=''

# Each line: group|listing|flags regex|name|kind|what the library uses it for
#
# Groups: "required" is needed by every VideoProcessor or VideoFrameExtractionProcessor; "source" is needed to read a source codec or container format, all
# of which are accepted by default (the processing options' SourceVideoCodecs, SourceAudioCodecs and SourceFormats); "optional" is used when present.
while IFS='|' read -r line_group listing flags name kind purpose; do
  case "$line_group" in
    ''|'#'*) continue ;;
  esac

  if [[ "$line_group" != "$group" ]]; then
    group=$line_group
    case "$group" in
      required) echo; echo "Required:" ;;
      source) echo; echo "Required for the source codecs and formats accepted by default:" ;;
      optional) echo; echo "Optional:" ;;
    esac
  fi

  case "$listing" in
    encoders) list=$encoders ;;
    decoders) list=$decoders ;;
    codecs) list=$codecs ;;
    muxers) list=$muxers ;;
    demuxers) list=$demuxers ;;
    filters) list=$filters ;;
  esac

  if has "$list" "$flags" "$name"; then
    status='ok'
  else
    status='MISSING'
    if [[ "$group" == optional ]]; then
      missing_optional=$((missing_optional + 1))
    else
      missing_required=$((missing_required + 1))
    fi
  fi

  printf '  %-8s %-24s %s\n' "$status" "$name $kind" "$purpose"
done <<'EOF'
required|encoders|^V|libx264|encoder|H.264 output
required|encoders|^V|libx265|encoder|HEVC output
required|encoders|^A|aac|encoder|AAC output (always needed, even when libfdk_aac is present)
required|encoders|^V|png|encoder|frame extraction
required|muxers|E|mp4|muxer|MP4 output
required|filters||zscale|filter|colour conversion and HDR tone mapping (needs libzimg)
required|filters||scale|filter|resizing
required|filters||fps|filter|frame rate limits
required|filters||tonemap|filter|HDR tone mapping
required|filters||format|filter|pixel format conversion
required|filters||bwdif|filter|deinterlacing
required|filters||setsar|filter|pixel aspect ratio
required|filters||transpose|filter|rotation
required|filters||hflip|filter|mirroring
required|filters||vflip|filter|mirroring
required|filters||sidedata|filter|removing rotation and HDR side data
source|codecs|^D.V|h264|decoder|H.264 sources
source|codecs|^D.V|hevc|decoder|HEVC sources
source|codecs|^D.V|mpeg2video|decoder|MPEG-2 (H.262) sources
source|codecs|^D.V|h263|decoder|H.263 sources
source|codecs|^D.V|vvc|decoder|VVC (H.266) sources
source|codecs|^D.V|mpeg1video|decoder|MPEG-1 sources
source|codecs|^D.V|mpeg4|decoder|MPEG-4 Part 2 sources
source|codecs|^D.V|vp8|decoder|VP8 sources
source|decoders|^V|libvpx|decoder|VP8 sources
source|codecs|^D.V|vp9|decoder|VP9 sources
source|decoders|^V|libvpx-vp9|decoder|VP9 sources
source|codecs|^D.V|av1|decoder|AV1 sources
source|decoders|^V|libdav1d|decoder|AV1 sources
source|codecs|^D.A|aac|decoder|AAC and HE-AAC sources
source|codecs|^D.A|mp2|decoder|MP2 sources
source|codecs|^D.A|mp3|decoder|MP3 sources
source|codecs|^D.A|vorbis|decoder|Vorbis sources
source|codecs|^D.A|opus|decoder|Opus sources
source|codecs|^D.A|amr_nb|decoder|AMR-NB sources
source|codecs|^D.A|amr_wb|decoder|AMR-WB sources
source|demuxers|D|mov|demuxer|MP4, MOV, M4A, 3GP, 3G2 and MJ2 sources
source|demuxers|D|matroska|demuxer|MKV and WebM sources
source|demuxers|D|avi|demuxer|AVI sources
source|demuxers|D|mpegts|demuxer|TS and M2TS sources
source|demuxers|D|mpeg|demuxer|MPEG program stream sources
optional|encoders|^A|libfdk_aac|encoder|better AAC output (without it, FFmpeg's own aac encoder is used)
optional|encoders|^S|mov_text|encoder|TryPreserveUnrecognizedStreams
optional|encoders|^S|dvdsub|encoder|TryPreserveUnrecognizedStreams
EOF

# Hardware acceleration. The library uses a mode when ffmpeg lists it, ffprobe lists the hardware pixel format it decodes to, and a small H.264 stream can be
# decoded with it and downloaded back out of the hardware (see CheckHWAccelActuallySupported in FFprobeUtils.cs), which is what is tried here.
echo
echo "Hardware acceleration (optional; used only when the processing options ask for it):"

sample=''
if (( test_hwaccel )) && has "$encoders" '^V' libx264; then
  sample=$(mktemp "${TMPDIR:-/tmp}/check-ffmpeg.XXXXXX")
  trap 'rm -f "$sample"' EXIT
  if ! "$ffmpeg" -hide_banner -v error -nostdin -f lavfi -r 10 -i testsrc2=s=128x128 -frames:v 10 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
      -f h264 -y "$sample" > /dev/null 2>&1; then
    echo "  (could not encode the test stream, so the modes are not tried)"
    sample=''
  fi
fi

# Each mode with the hardware pixel format it decodes to (see FFmpegUtils.MapHWAccelNameToFormatName).
for pair in videotoolbox:videotoolbox_vld cuda:cuda qsv:qsv amf:amf d3d12va:d3d12; do
  mode=${pair%%:*}
  pix_fmt=${pair#*:}

  if ! grep -qx "$mode" <<< "$hwaccels" || ! has "$pix_fmts" '^..H' "$pix_fmt"; then
    status='not built in'
  elif [[ -z "$sample" ]]; then
    status='built in'
  elif "$ffmpeg" -hide_banner -v error -nostdin -hwaccel "$mode" -hwaccel_output_format "$pix_fmt" -f h264 -i "$sample" \
      -filter:v hwdownload,format=nv12 -f framemd5 - > /dev/null 2>&1; then
    status='built in, works on this machine'
  else
    status='built in, not usable on this machine (no supported device or driver)'
  fi

  printf '  %-13s %s\n' "$mode" "$status"
done

echo

if (( missing_required > 0 )); then
  echo "$missing_required required feature(s) missing: the library's processors will refuse this build (with their default options)."
  exit 1
fi

if (( missing_optional > 0 )); then
  if (( strict )); then
    echo "$missing_optional optional feature(s) missing, which --strict does not allow."
    exit 1
  fi

  echo "Everything required is present; $missing_optional optional feature(s) missing."
  exit 0
fi

echo "Everything FulcrumFS.Videos uses is present."
