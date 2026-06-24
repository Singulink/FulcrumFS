# Builds self-contained (statically linked) ffmpeg.exe/ffprobe.exe for Windows x64 natively using
# media-autobuild_suite, copying them into $env:FFMPEG_OUTPUT_DIR.

# Fail on any error.
$ErrorActionPreference = 'Stop'

# Fail early if the workflow did not provide the output directory.
if (-not $env:FFMPEG_OUTPUT_DIR) {
    throw 'FFMPEG_OUTPUT_DIR must be set by the workflow'
}

# Ensure the output directory exists.
New-Item -ItemType Directory -Force -Path $env:FFMPEG_OUTPUT_DIR | Out-Null

# media-autobuild_suite requires a very short root path.
$suiteRoot = 'C:\ffmpeg'
$suiteDir = Join-Path $suiteRoot 'media-autobuild_suite'

# Remove any leftover state from a previous (failed) attempt so a retry starts from a clean slate
# (e.g. a partial git clone or a stale build\compilation_failed marker).
if (Test-Path $suiteRoot) {
    Remove-Item -Recurse -Force -Path $suiteRoot
}

# Create the root folder.
New-Item -ItemType Directory -Force -Path $suiteRoot | Out-Null

# Clone the suite.
git clone https://github.com/m-ab-s/media-autobuild_suite $suiteDir
if ($LASTEXITCODE -ne 0) { throw 'Failed to clone media-autobuild_suite' }

# Pre-seed the answers (.ini) and option files so the suite runs unattended. It only writes (and
# pauses on) the option files when they are missing, so providing them keeps the run silent.
$buildDir = Join-Path $suiteDir 'build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
foreach ($f in 'media-autobuild_suite.ini', 'ffmpeg_options.txt', 'mpv_options.txt') {
    Copy-Item -Force -Path (Join-Path $PSScriptRoot $f) -Destination (Join-Path $buildDir $f)
}

# Run the suite.
$PSNativeCommandUseErrorActionPreference = $false
Push-Location $suiteDir
try {
    & cmd.exe /c 'media-autobuild_suite.bat'
}
finally {
    Pop-Location
}
Write-Host "media-autobuild_suite.bat exited with code $LASTEXITCODE; verifying build output (exit code is ignored - success is determined by the checks below)."

# Check if compilation failed.
if (Test-Path (Join-Path $buildDir 'compilation_failed')) {
    throw 'media-autobuild_suite reported a build failure (build\compilation_failed was created).'
}

# The static build produces standalone binaries, so copy just the two we need.
$binVideoDir = Join-Path $suiteDir 'local64\bin-video'
foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
    $source = Join-Path $binVideoDir $exe
    if (-not (Test-Path $source)) { throw "Could not find Windows $exe in $binVideoDir" }
    Copy-Item -Force -Path $source -Destination $env:FFMPEG_OUTPUT_DIR
    Write-Host "Copied $exe to $env:FFMPEG_OUTPUT_DIR."
}

# media-autobuild_suite.bat exits non-zero even on success, leaving $LASTEXITCODE set. The GitHub
# Actions pwsh wrapper exits with $LASTEXITCODE, which would fail the step despite a good build.
# Success is already verified above (compilation_failed marker + binary existence), so exit cleanly.
exit 0
