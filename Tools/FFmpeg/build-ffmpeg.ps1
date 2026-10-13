# Builds ffmpeg.exe and ffprobe.exe for Windows x64 with everything FulcrumFS.Videos uses (including libfdk_aac and the hardware acceleration modes the library
# supports on Windows), checks them with check-ffmpeg.ps1 and copies them into an output directory.
#
# Usage: build-ffmpeg.ps1 [-OutputDir] <directory> [-FFmpegVersion <version>] [-WorkDir <directory>] [-Cores <count>]
#
# The build is done by media-autobuild_suite (https://github.com/m-ab-s/media-autobuild_suite), which installs its own MSYS2 and compilers into the work
# directory, then downloads and compiles FFmpeg and the libraries it links statically, so the two executables need nothing else installed. Only Git has to be
# installed beforehand. A build takes from one to a few hours, and around 20 GB of disk space. Runs under Windows PowerShell 5.1 and PowerShell 7.
#
# The result uses libfdk_aac, which makes it non-free: it may be used, but not redistributed. See the FFmpeg setup guide of the FulcrumFS documentation.

param(
    # The directory to copy ffmpeg.exe and ffprobe.exe into.
    [Parameter(Mandatory = $true, Position = 0)] [string] $OutputDir,

    # The FFmpeg release to build (for example 9.0.2), "latest" for the latest release (looked up when the build starts), or "master" for FFmpeg's current
    # master. The libraries are always built at the heads of their repositories.
    [string] $FFmpegVersion = 'latest',

    # The directory to build in, which media-autobuild_suite needs to be a short path without spaces. It must not exist, unless an earlier run of this script
    # made it, in which case it is deleted and made again. It is left in place afterwards (except on CI), and can then be deleted.
    [string] $WorkDir = 'C:\ffbuild',

    # The number of processors to compile with.
    [int] $Cores = [Environment]::ProcessorCount
)

# Fail on any error.
$ErrorActionPreference = 'Stop'

# The suite is run as a native command and its exit code inspected below, so a non-zero exit must not throw under PowerShell 7.3+.
$PSNativeCommandUseErrorActionPreference = $false

if ($env:OS -ne 'Windows_NT') {
    throw 'This script builds on Windows. On Linux and macOS, use build-ffmpeg.sh.'
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Git is required: install it (https://git-scm.com/) and run this script again.'
}

if ($WorkDir.Contains(' ')) {
    throw "The work directory must not contain spaces (media-autobuild_suite cannot build in it): $WorkDir"
}

$ffmpegRepo = 'https://git.ffmpeg.org/ffmpeg.git'

# The tag of the requested release (FFmpeg tags release X.Y.Z as nX.Y.Z), or nothing for master, which the suite builds by default.
$ffmpegTag = $null
if ($FFmpegVersion -ne 'master') {
    $releaseTags = @(git ls-remote --tags --refs $ffmpegRepo | ForEach-Object { ($_ -split '\s+')[1] -replace '^refs/tags/', '' } |
        Where-Object { $_ -match '^n\d+\.\d+(\.\d+)?$' })
    if ($LASTEXITCODE -ne 0 -or $releaseTags.Count -eq 0) { throw "Could not list the FFmpeg releases at $ffmpegRepo." }

    if ($FFmpegVersion -eq 'latest') {
        $ffmpegTag = $releaseTags | Sort-Object { [version]$_.Substring(1) } | Select-Object -Last 1
    }
    elseif ($releaseTags -contains "n$FFmpegVersion") {
        $ffmpegTag = "n$FFmpegVersion"
    }
    else {
        throw "FFmpeg $FFmpegVersion is not a release (there is no tag n$FFmpegVersion at $ffmpegRepo). Use a release version, 'latest' or 'master'."
    }
}

Write-Host "Building FFmpeg $(if ($ffmpegTag) { $ffmpegTag.Substring(1) } else { 'master' })."

# The build starts from a clean slate. A work directory that an earlier run of this script made (it has the marker file) is deleted, which on CI removes what a
# failed attempt left behind (e.g. a partial git clone or a stale build\compilation_failed marker). Any other folder is someone's own (a developer's suite,
# with hours of compiled work in it), so it is never deleted: the build refuses to start instead.
$markerName = '.fulcrumfs-ffmpeg-build'
if (Test-Path $WorkDir) {
    if (-not (Test-Path (Join-Path $WorkDir $markerName))) {
        throw "$WorkDir exists already and was not made by this script. Remove it yourself, or pass -WorkDir with a short path that does not exist."
    }

    Write-Host "Removing $WorkDir, left by an earlier run."
    Remove-Item -Recurse -Force -Path $WorkDir
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
New-Item -ItemType File -Path (Join-Path $WorkDir $markerName) | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$suiteDir = Join-Path $WorkDir 'media-autobuild_suite'

# Clone the suite. It is cloned at the head of its default branch, so it can gain an option between two runs (see check-suite-options.ps1 below).
git clone https://github.com/m-ab-s/media-autobuild_suite $suiteDir
if ($LASTEXITCODE -ne 0) { throw 'Failed to clone media-autobuild_suite' }

# Pre-seed the answers (.ini) and option files so the suite runs unattended. It only writes (and pauses on) the option files when they are missing, so
# providing them keeps the run silent. The answers file is given the FFmpeg revision and the number of processors to use.
$buildDir = Join-Path $suiteDir 'build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
foreach ($f in 'ffmpeg_options.txt', 'mpv_options.txt') {
    Copy-Item -Force -Path (Join-Path $PSScriptRoot "windows\$f") -Destination (Join-Path $buildDir $f)
}

$ffmpegPath = if ($ffmpegTag) { "$ffmpegRepo#tag=$ffmpegTag" } else { $ffmpegRepo }
$ini = [System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'windows\media-autobuild_suite.ini')) | ForEach-Object {
    if ($_.StartsWith('ffmpegPath=')) { "ffmpegPath=$ffmpegPath" }
    elseif ($_.StartsWith('cores=')) { "cores=$Cores" }
    else { $_ }
}

$iniPath = Join-Path $buildDir 'media-autobuild_suite.ini'
[System.IO.File]::WriteAllLines($iniPath, [string[]]$ini)

$bat = Join-Path $suiteDir 'media-autobuild_suite.bat'

# The suite asks on the console for every option the answers file lacks, and without input it asks forever, so check first that the answers file is
# complete: a missing answer then fails here, in seconds, naming the option.
& (Join-Path $PSScriptRoot 'windows\check-suite-options.ps1') -BatPath $bat -IniPath $iniPath

# HACK: The suite is run in CI mode so that it never waits for input (where it would otherwise stop to ask what to do after a failed download, for example).
# The CI variable does not get passed into the suite's shell, so the batch file is changed to set it there. Otherwise a CI job can get stuck for 6 hours and
# not retry.
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

# Run the suite, by its full path: cmd.exe does not look for commands in the current directory where NoDefaultCurrentDirectoryInExePath is set.
Push-Location $suiteDir
try {
    & cmd.exe /c $bat
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
    if (-not (Test-Path $source)) { throw "Could not find $exe in $binVideoDir" }
    Copy-Item -Force -Path $source -Destination $OutputDir
}

Write-Host "Copied ffmpeg.exe and ffprobe.exe to $OutputDir."

# Check the result as the library will, requiring the optional features too: a build made here should have everything.
Write-Host ''
& (Join-Path $PSScriptRoot 'check-ffmpeg.ps1') -Directory $OutputDir -Strict
if ($LASTEXITCODE -ne 0) { throw 'The build is missing features FulcrumFS.Videos uses (see above).' }

# On CI, delete the build tree now that the (statically linked) binaries have been copied out of it. CI runners have very limited disk space (generally
# ~14GB), and the sources plus build artifacts take up a large chunk of it. Anywhere else it is kept, so that it can be looked into.
Write-Host ''
if ($env:CI) {
    Write-Host 'Removing the build tree to free up disk space (CI only).'
    Remove-Item -Recurse -Force -Path $WorkDir -ErrorAction SilentlyContinue
}
else {
    Write-Host "The build tree is in $WorkDir; delete it once you no longer need it."
}

# media-autobuild_suite.bat exits non-zero even on success, leaving $LASTEXITCODE set. The GitHub Actions pwsh wrapper exits with $LASTEXITCODE, which would
# fail the step despite a good build. Success is already verified above, so exit cleanly.
exit 0
