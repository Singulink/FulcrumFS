# Checks that the ffmpeg and ffprobe executables in a directory have everything FulcrumFS.Videos uses, looking for each feature the way the library does
# when a processor is constructed, and reports which hardware acceleration modes they were built with and which of those work on this machine.
#
# Usage: check-ffmpeg.ps1 [-Strict] [-SkipHWAccelTest] <directory>
#
# The exit code is 0 when nothing required is missing, 1 when something is, and 2 when the executables cannot be found or run. Runs under Windows PowerShell
# 5.1 and PowerShell 7 (on any operating system).
#
# What is checked mirrors FFprobeUtils.cs in FulcrumFS.Videos. Keep the two in step, together with check-ffmpeg.sh, the bash version of this script.

param(
    # The directory containing ffmpeg and ffprobe.
    [Parameter(Mandatory = $true, Position = 0)] [string] $Directory,

    # Also fail when an optional feature is missing. The build scripts use this: a build they make should have everything.
    [switch] $Strict,

    # Do not try to decode with the hardware acceleration modes the build has; only report that it has them.
    [switch] $SkipHWAccelTest
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell only runs on Windows, and does not define $IsWindows.
$onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows

if (-not (Test-Path -PathType Container $Directory)) {
    Write-Host "Directory not found: $Directory"
    exit 2
}

$Directory = (Resolve-Path $Directory).Path

function Find-Executable([string] $Name) {
    $path = Join-Path $Directory $(if ($onWindows) { "$Name.exe" } else { $Name })
    if (-not (Test-Path -PathType Leaf $path)) {
        Write-Host "$(Split-Path -Leaf $path) was not found in $Directory."
        exit 2
    }

    return $path
}

$ffmpeg = Find-Executable 'ffmpeg'
$ffprobe = Find-Executable 'ffprobe'

# Runs an executable and returns its exit code and output. The process is run directly rather than with PowerShell's call operator so that what it writes
# to stderr is captured as text in both PowerShell editions (Windows PowerShell turns it into error records).
function Invoke-Tool([string] $Path, [string[]] $Arguments) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Path
    $startInfo.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    try {
        $process = [System.Diagnostics.Process]::Start($startInfo)
    }
    catch {
        return [pscustomobject]@{ ExitCode = -1; Output = ''; Error = $_.Exception.Message }
    }

    $process.StandardInput.Close()
    $errorTask = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()

    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Error = $errorTask.Result }
}

function Get-VersionLine([string] $Path) {
    $result = Invoke-Tool $Path @('-hide_banner', '-version')
    if ($result.ExitCode -ne 0) {
        Write-Host "$Path could not be run:"
        Write-Host ($result.Error + $result.Output)
        exit 2
    }

    return ($result.Output -split "`r?`n")[0]
}

$ffmpegVersion = Get-VersionLine $ffmpeg
$ffprobeVersion = Get-VersionLine $ffprobe

Write-Host "Checking $Directory"
Write-Host "  $ffmpegVersion"
Write-Host "  $ffprobeVersion"

# The library reads what the build supports from ffprobe but transcodes with ffmpeg, so the two must come from the same build. The version is the third word
# of the line ("ffmpeg version 9.0.2 Copyright ...").
if (($ffmpegVersion -split ' ')[2] -ne ($ffprobeVersion -split ' ')[2]) {
    Write-Host '  Warning: ffmpeg and ffprobe report different versions. Use the two executables of one build:'
    Write-Host "  the library checks ffprobe's features and runs ffmpeg."
}

# Returns the flags and name of each entry of a listing, i.e. the first two columns of each line after the line of dashes that ends the listing's legend
# (or of every line, for -filters, whose legend does not end with a line of dashes in every FFmpeg version; the legend's lines cannot match a filter name).
function Get-Entries([string] $Path, [string] $Listing, [switch] $AllLines) {
    $found = $AllLines.IsPresent
    foreach ($line in ((Invoke-Tool $Path @('-hide_banner', '-v', 'error', $Listing)).Output -split "`r?`n")) {
        $columns = @($line.Trim() -split '\s+')
        if ($found) {
            if ($columns.Count -ge 2) { [pscustomobject]@{ Flags = $columns[0]; Name = $columns[1] } }
        }
        elseif ($line -match '^ *-+ *$') {
            $found = $true
        }
    }
}

$listings = @{
    encoders = @(Get-Entries $ffprobe '-encoders')
    decoders = @(Get-Entries $ffprobe '-decoders')
    codecs = @(Get-Entries $ffprobe '-codecs')
    muxers = @(Get-Entries $ffprobe '-muxers')
    demuxers = @(Get-Entries $ffprobe '-demuxers')
    filters = @(Get-Entries $ffprobe '-filters' -AllLines)
    pix_fmts = @(Get-Entries $ffprobe '-pix_fmts')
}

# -hwaccels prints a heading and then one method per line.
$hwaccels = @(((Invoke-Tool $ffmpeg @('-hide_banner', '-v', 'error', '-nostdin', '-hwaccels')).Output -split "`r?`n") | Select-Object -Skip 1 |
    ForEach-Object { $_.Trim() } | Where-Object { $_ })

# Whether an entry of a listing is named $Name (or lists it among comma-separated names, as -demuxers does) and has flags that match the regex $Flags
# (matched case-sensitively: in -demuxers, for example, "D" is a demuxer and "d" a device).
function Test-Entry([string] $Listing, [string] $Flags, [string] $Name) {
    foreach ($entry in $listings[$Listing]) {
        if ($entry.Flags -cmatch $Flags -and ($entry.Name -split ',') -ccontains $Name) { return $true }
    }

    return $false
}

# Each line: group|listing|flags regex|name|kind|what the library uses it for
#
# Groups: "required" is needed by every VideoProcessor or VideoFrameExtractionProcessor; "source" is needed to read a source codec or container format, all
# of which are accepted by default (the processing options' SourceVideoCodecs, SourceAudioCodecs and SourceFormats); "optional" is used when present.
$features = @'
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
'@

$headings = @{
    required = 'Required:'
    source = 'Required for the source codecs and formats accepted by default:'
    optional = 'Optional:'
}

$missingRequired = 0
$missingOptional = 0
$group = ''

foreach ($line in ($features -split "`r?`n" | Where-Object { $_ })) {
    $lineGroup, $listing, $flags, $name, $kind, $purpose = $line -split '\|'

    if ($lineGroup -ne $group) {
        $group = $lineGroup
        Write-Host ''
        Write-Host $headings[$group]
    }

    if (Test-Entry $listing $flags $name) {
        $status = 'ok'
    }
    else {
        $status = 'MISSING'
        if ($group -eq 'optional') { $missingOptional++ } else { $missingRequired++ }
    }

    Write-Host ('  {0,-8} {1,-24} {2}' -f $status, "$name $kind", $purpose)
}

# Hardware acceleration. The library uses a mode when ffmpeg lists it, ffprobe lists the hardware pixel format it decodes to, and a small H.264 stream can be
# decoded with it and downloaded back out of the hardware (see CheckHWAccelActuallySupported in FFprobeUtils.cs), which is what is tried here.
Write-Host ''
Write-Host 'Hardware acceleration (optional; used only when the processing options ask for it):'

$sample = $null
try {
    if (-not $SkipHWAccelTest -and (Test-Entry 'encoders' '^V' 'libx264')) {
        $sample = [System.IO.Path]::GetTempFileName()
        $result = Invoke-Tool $ffmpeg @('-hide_banner', '-v', 'error', '-nostdin', '-f', 'lavfi', '-r', '10', '-i', 'testsrc2=s=128x128', '-frames:v', '10',
            '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-f', 'h264', '-y', $sample)

        if ($result.ExitCode -ne 0) {
            Write-Host '  (could not encode the test stream, so the modes are not tried)'
            Remove-Item -Force $sample
            $sample = $null
        }
    }

    # Each mode with the hardware pixel format it decodes to (see FFmpegUtils.MapHWAccelNameToFormatName).
    $modes = [ordered]@{ videotoolbox = 'videotoolbox_vld'; cuda = 'cuda'; qsv = 'qsv'; amf = 'amf'; d3d12va = 'd3d12' }

    foreach ($mode in $modes.Keys) {
        $pixFmt = $modes[$mode]

        if (-not ($hwaccels -ccontains $mode) -or -not (Test-Entry 'pix_fmts' '^..H' $pixFmt)) {
            $status = 'not built in'
        }
        elseif (-not $sample) {
            $status = 'built in'
        }
        else {
            $result = Invoke-Tool $ffmpeg @('-hide_banner', '-v', 'error', '-nostdin', '-hwaccel', $mode, '-hwaccel_output_format', $pixFmt, '-f', 'h264',
                '-i', $sample, '-filter:v', 'hwdownload,format=nv12', '-f', 'framemd5', '-')

            $status = if ($result.ExitCode -eq 0) {
                'built in, works on this machine'
            }
            else {
                'built in, not usable on this machine (no supported device or driver)'
            }
        }

        Write-Host ('  {0,-13} {1}' -f $mode, $status)
    }
}
finally {
    if ($sample) { Remove-Item -Force -ErrorAction SilentlyContinue $sample }
}

Write-Host ''

if ($missingRequired -gt 0) {
    Write-Host "$missingRequired required feature(s) missing: the library's processors will refuse this build (with their default options)."
    exit 1
}

if ($missingOptional -gt 0) {
    if ($Strict) {
        Write-Host "$missingOptional optional feature(s) missing, which -Strict does not allow."
        exit 1
    }

    Write-Host "Everything required is present; $missingOptional optional feature(s) missing."
    exit 0
}

Write-Host 'Everything FulcrumFS.Videos uses is present.'
exit 0
