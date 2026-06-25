# Runs the Windows ffmpeg build (build-windows.ps1) with retries. The build downloads sources from
# several third-party servers that are occasionally unavailable, so a transient failure (e.g. a
# download link being temporarily down) should not fail the whole CI run on the first try.
# build-windows.ps1 cleans up any leftover state from a previous attempt before each run, so each
# retry starts from a clean slate.

$ErrorActionPreference = 'Stop'

# We invoke the build in a child pwsh process and inspect its exit code ourselves, so a non-zero
# exit must not throw here.
$PSNativeCommandUseErrorActionPreference = $false

# Total number of attempts (1 initial + 2 retries).
$maxAttempts = 3
$buildScript = Join-Path $PSScriptRoot 'build-windows.ps1'

for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    Write-Host "ffmpeg build attempt $attempt of $maxAttempts..."

    # Run the build in a child pwsh process so a thrown error or non-zero exit does not abort this
    # retry loop, and so each attempt starts from clean process state.
    & pwsh -NoProfile -File $buildScript
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
