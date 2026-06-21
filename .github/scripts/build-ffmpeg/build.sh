#!/usr/bin/env bash

# Writes ffmpeg-linux-x64.zip and ffmpeg-windows-x64.zip (ffmpeg + ffprobe at the zip root) into $FFMPEG_OUTPUT_DIR.

# Exit on any error, unset variable, or failed pipe.
set -euo pipefail

# Fail early if the workflow did not provide the output directory.
: "${FFMPEG_OUTPUT_DIR:?FFMPEG_OUTPUT_DIR must be set by the workflow}"

# Ensure the output directory exists.
mkdir -p "$FFMPEG_OUTPUT_DIR"

# TODO: replace these placeholder blank zips with the real FFmpeg build output.
touch "$FFMPEG_OUTPUT_DIR/ffmpeg-linux-x64.zip"
touch "$FFMPEG_OUTPUT_DIR/ffmpeg-windows-x64.zip"
