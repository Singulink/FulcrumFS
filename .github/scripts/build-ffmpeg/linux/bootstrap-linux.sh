#!/usr/bin/env bash

# Runs the Linux ffmpeg build (build-linux.sh) with retries. The build downloads sources from
# several third-party servers that are occasionally unavailable, so a transient failure (e.g. a
# download link being temporarily down) should not fail the whole CI run on the first try.
# build-linux.sh cleans up any leftover state from a previous attempt before each run, so each
# retry starts from a clean slate.

# Exit on unset variable or failed pipe (but not on command failure, which is handled per attempt).
set -uo pipefail

# Get directory of this script.
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Total number of attempts (1 initial + 2 retries).
max_attempts=3

for (( attempt = 1; attempt <= max_attempts; attempt++ )); do
  echo "ffmpeg build attempt $attempt of $max_attempts..."

  # Capture the exit code of the build without aborting this loop on failure.
  status=0
  bash "$script_dir/build-linux.sh" || status=$?

  if (( status == 0 )); then
    echo "ffmpeg build succeeded on attempt $attempt."
    exit 0
  fi

  if (( attempt >= max_attempts )); then
    echo "ffmpeg build failed after $max_attempts attempts (exit code $status)." >&2
    exit "$status"
  fi

  echo "ffmpeg build failed on attempt $attempt (exit code $status); retrying..." >&2
done
