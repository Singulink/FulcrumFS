# Builds ffmpeg/ffprobe for Windows x64 natively (on Windows) using media-autobuild_suite.
# Copies the resulting binaries (ffmpeg.exe, ffprobe.exe, and their required DLLs - they are
# NOT self-contained) into $env:FFMPEG_OUTPUT_DIR.
#
# See ..\linux\build-linux.sh for the (recommended) cross-compile-from-Linux approach, which
# produces a smaller, fully self-contained executable.

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

# Copy our pre-seeded answers file into the suite's build folder so it runs unattended.
$buildDir = Join-Path $suiteDir 'build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
Copy-Item -Force -Path (Join-Path $PSScriptRoot 'media-autobuild_suite.ini') -Destination (Join-Path $buildDir 'media-autobuild_suite.ini')

# Run the suite. It bootstraps MSYS2 and builds everything selected in the .ini; this can take a while.
Push-Location $suiteDir
try {
    & cmd.exe /c 'media-autobuild_suite.bat'
    if ($LASTEXITCODE -ne 0) { throw "media-autobuild_suite.bat exited with code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

# The built binaries land here. They are NOT self-contained, so we keep the whole directory's contents.
$binVideoDir = Join-Path $suiteDir 'local64\bin-video'
if (-not (Test-Path (Join-Path $binVideoDir 'ffmpeg.exe'))) {
    throw "Could not find Windows ffmpeg.exe in $binVideoDir"
}

# Copy ffmpeg.exe, ffprobe.exe, and their accompanying runtime files into the output directory.
Copy-Item -Force -Path (Join-Path $binVideoDir '*') -Destination $env:FFMPEG_OUTPUT_DIR -Recurse
