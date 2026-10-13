# Checks that the answers file (media-autobuild_suite.ini) answers every option media-autobuild_suite currently asks for.
#
# The suite asks, on the console, for every option of its list that it does not find in the .ini, and when it gets no input it asks again, forever. In CI
# that held a runner until the job's time limit (six hours by default) each time the suite gained an option. This check fails in seconds instead, and names
# the options to add.

param(
    # The suite's media-autobuild_suite.bat.
    [Parameter(Mandatory = $true)] [string] $BatPath,

    # The answers file the suite will be run with.
    [Parameter(Mandatory = $true)] [string] $IniPath
)

$ErrorActionPreference = 'Stop'

# The options are listed in one "set iniOptions=..." statement, continued over several lines with a trailing caret.
$batLines = [System.IO.File]::ReadAllLines($BatPath)
$start = -1
for ($i = 0; $i -lt $batLines.Length; $i++) {
    if ($batLines[$i].StartsWith('set iniOptions=')) {
        $start = $i
        break
    }
}

if ($start -lt 0) {
    throw "Could not find the option list ('set iniOptions=') in $BatPath. The suite has changed how it declares its options: update check-suite-options.ps1."
}

$optionText = ''
for ($i = $start; $i -lt $batLines.Length; $i++) {
    $line = $batLines[$i].TrimEnd()
    $continued = $line.EndsWith('^')
    $optionText += ' ' + $line.TrimEnd('^')
    if (-not $continued) { break }
}

$options = @($optionText.Replace('set iniOptions=', '') -split '\s+' | Where-Object { $_ })
if ($options.Count -eq 0) {
    throw "The option list ('set iniOptions=') in $BatPath is empty. The suite has changed how it declares its options: update check-suite-options.ps1."
}

# The suite looks an option up as a case-sensitive substring of a line of the .ini, so that is what is checked here.
$iniLines = [System.IO.File]::ReadAllLines($IniPath)
$missing = @()
foreach ($option in $options) {
    $found = $false
    foreach ($iniLine in $iniLines) {
        if ($iniLine.Contains($option)) {
            $found = $true
            break
        }
    }

    if (-not $found) { $missing += $option }
}

if ($missing.Count -gt 0) {
    throw ("media-autobuild_suite asks for $($missing.Count) option(s) that $(Split-Path -Leaf $IniPath) does not answer: $($missing -join ', '). " +
        "Add them to Tools/FFmpeg/windows/media-autobuild_suite.ini (the suite's .bat shows the choices of each); " +
        "without them the suite waits for console input forever.")
}

Write-Host "The answers file covers all $($options.Count) options of media-autobuild_suite."
