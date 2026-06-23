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
}
