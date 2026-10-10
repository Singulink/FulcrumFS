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

# media-autobuild_suite requires a very short root path. FFMPEG_SUITE_ROOT overrides the default, for a machine where C:\ffmpeg is taken.
$suiteRoot = if ($env:FFMPEG_SUITE_ROOT) { $env:FFMPEG_SUITE_ROOT } else { 'C:\ffmpeg' }
$suiteDir = Join-Path $suiteRoot 'media-autobuild_suite'

# The build starts from a clean slate. On CI that means removing what a previous (failed) attempt left behind (e.g. a partial git clone or a stale
# build\compilation_failed marker). Anywhere else, a folder that exists is someone's own (a developer's suite, with hours of compiled work in it), so it is
# never deleted: the build refuses to start instead.
if (Test-Path $suiteRoot) {
    if (-not $env:CI) {
        throw "$suiteRoot exists already. This script builds from a clean slate and does not delete a folder outside CI: remove it yourself, or set FFMPEG_SUITE_ROOT to another short path that does not exist."
    }

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

$bat = Join-Path $suiteDir 'media-autobuild_suite.bat'

# The suite is cloned at the head of its default branch on purpose (it builds FFmpeg's current master with current libraries, which is how the video tests
# keep running against current FFmpeg), so it can gain an option between two runs. It asks on the console for every option the answers file lacks, and
# without input it asks forever, so check first that the answers file is complete: a missing answer then fails here, in seconds, naming the option.
& (Join-Path $PSScriptRoot 'check-suite-options.ps1') -BatPath $bat -IniPath (Join-Path $buildDir 'media-autobuild_suite.ini')

# HACK: We want to run the suite in CI mode so it doesn't ask for input; however, the CI env var doesn't get passed into mintty, so we need to manually adjust
# the batch file so it sets it. Otherwise CI can get stuck for 6 hours & not retry.
$content = [System.IO.File]::ReadAllText($bat)
$old = @'
"%command% %arg%"
'@
$new = @'
"export CI=true ; %command% %arg%"
'@
if ([regex]::Matches($content, [regex]::Escape($old)).Count -ne 1) {
    throw "Expected exactly one match in $bat"
}
$content = $content.Replace($old, $new)

# Written without a byte order mark, which cmd.exe would read as part of the first command of the batch file (Windows PowerShell's Set-Content -Encoding UTF8
# writes one; PowerShell 7's does not).
[System.IO.File]::WriteAllText($bat, $content, (New-Object System.Text.UTF8Encoding($false)))

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

# Say which FFmpeg was built (the suite builds FFmpeg's current master), so a test run's log names the revision its results are for.
$versionLine = & (Join-Path $env:FFMPEG_OUTPUT_DIR 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
Write-Host "Built $versionLine"

# On CI, delete the build tree now that the (statically linked) binaries have been extracted from it. CI runners have very limited disk space (generally ~14GB),
# and the sources plus build artifacts take up a large chunk of it. Only do this on CI so local runs can inspect the tree if desired.
if ($env:CI) {
    Write-Host 'Removing the ffmpeg build tree to free up disk space (CI only).'
    Remove-Item -Recurse -Force -Path $suiteRoot -ErrorAction SilentlyContinue
}

# media-autobuild_suite.bat exits non-zero even on success, leaving $LASTEXITCODE set. The GitHub
# Actions pwsh wrapper exits with $LASTEXITCODE, which would fail the step despite a good build.
# Success is already verified above (compilation_failed marker + binary existence), so exit cleanly.
exit 0
