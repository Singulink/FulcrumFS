# Runs the Windows ffmpeg build (Tools/FFmpeg/build-ffmpeg.ps1, the script users build with) into $env:FFMPEG_OUTPUT_DIR, with retries. The build downloads
# sources from several third-party servers that are occasionally unavailable, so a transient failure (e.g. a download link being temporarily down) should not
# fail the whole CI run on the first try. build-ffmpeg.ps1 deletes the work directory an earlier attempt left behind, so each retry starts from a clean slate.
#
# CI builds FFmpeg's current master (users get the latest release by default), which is how the video tests keep running against current FFmpeg.

$ErrorActionPreference = 'Stop'

# We invoke the build in a child pwsh process and inspect its exit code ourselves, so a non-zero
# exit must not throw here.
$PSNativeCommandUseErrorActionPreference = $false

# Fail early if the workflow did not provide the output directory.
if (-not $env:FFMPEG_OUTPUT_DIR) {
    throw 'FFMPEG_OUTPUT_DIR must be set by the workflow'
}

# Total number of attempts (1 initial + 2 retries).
$maxAttempts = 3
$buildScript = Join-Path $PSScriptRoot '..\..\..\..\Tools\FFmpeg\build-ffmpeg.ps1'

for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    Write-Host "ffmpeg build attempt $attempt of $maxAttempts..."

    # Run the build in a child pwsh process so a thrown error or non-zero exit does not abort this
    # retry loop, and so each attempt starts from clean process state.
    & pwsh -NoProfile -File $buildScript -OutputDir $env:FFMPEG_OUTPUT_DIR -FFmpegVersion master
    $status = $LASTEXITCODE

    if ($status -eq 0) {
        Write-Host "ffmpeg build succeeded on attempt $attempt."
        exit 0
    }

    if ($attempt -ge $maxAttempts) {
        Write-Error "ffmpeg build failed after $maxAttempts attempts (exit code $status)."
        exit $status
    }

    Write-Warning "ffmpeg build failed on attempt $attempt (exit code $status); retrying..."
}
