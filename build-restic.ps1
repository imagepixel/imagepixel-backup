<#
.SYNOPSIS
    Builds restic for Windows with support for the "mount" command.

.DESCRIPTION
    Upstream restic cannot mount repositories on Windows. This script fetches
    the pinned restic release, copies the files from restic-overlay on top of
    it and builds bin\restic.exe. The resulting binary mounts repositories
    read-only through WinFsp.

    Requirements: Git and Go on the PATH. WinFsp is needed to run the mount
    command, not to build it.
#>
[CmdletBinding()]
param(
    [string]$ResticVersion = 'v0.19.1',
    [string]$CgofuseVersion = 'v1.6.0',
    [string]$Output = (Join-Path $PSScriptRoot 'bin\restic.exe')
)

$ErrorActionPreference = 'Stop'

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$source = Join-Path $PSScriptRoot ".build\restic-$ResticVersion"
$overlay = Join-Path $PSScriptRoot 'restic-overlay'

if (-not (Test-Path (Join-Path $source 'go.mod'))) {
    Write-Host "Fetching restic $ResticVersion ..."
    Invoke-Checked git @('clone', '--quiet', '--depth', '1', '--branch', $ResticVersion,
        '-c', 'advice.detachedHead=false', 'https://github.com/restic/restic.git', $source)
}

Write-Host 'Applying overlay ...'
Copy-Item -Path (Join-Path $overlay '*') -Destination $source -Recurse -Force

$outputDir = Split-Path -Parent $Output
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

Push-Location $source
try {
    # cgofuse talks to the WinFsp DLL directly, so no C compiler is needed
    $env:CGO_ENABLED = '0'
    Invoke-Checked go @('get', "github.com/winfsp/cgofuse@$CgofuseVersion")

    Write-Host "Building $Output ..."
    Invoke-Checked go @('build', '-trimpath', '-ldflags', '-s -w', '-o', $Output, './cmd/restic')
}
finally {
    Pop-Location
}

Write-Host "Done: $Output"
