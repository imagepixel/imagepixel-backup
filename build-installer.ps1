<#
.SYNOPSIS
    Builds the setup program of Imagepixel Backup.

.DESCRIPTION
    Builds restic if needed, fetches the WinFsp installer, publishes the application
    together with the .NET runtime and packs everything into
    artifacts\installer\ImagepixelBackup-Setup-<version>.exe.

    Requirements: Git, Go, the .NET 10 SDK and Inno Setup 6.

.PARAMETER PortalSettings
    A portal.json with the address and the public key of the customer portal, put next
    to the program so that the setup offers pairing with the portal (docs\device-pairing.md).
    Without it the setup offers the setup code only.
#>
[CmdletBinding()]
param(
    [string]$PortalSettings,
    [string]$WinFspVersion = 'v2.1',
    [string]$WinFspMsi = 'winfsp-2.1.25156.msi',
    [string]$WinFspSha256 = '073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Find-InnoSetup {
    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php'
}

$iscc = Find-InnoSetup

# restic with the mount command for Windows
if (-not (Test-Path (Join-Path $root 'bin\restic.exe'))) {
    & (Join-Path $root 'build-restic.ps1')
}

# rclone as released by the rclone project, for the encrypted view of the storage
if (-not (Test-Path (Join-Path $root 'bin\rclone.exe'))) {
    & (Join-Path $root 'get-rclone.ps1')
}

# the unmodified installer of the WinFsp project, checked against its known hash
$winFspDir = Join-Path $root '.build\winfsp'
$winFspPath = Join-Path $winFspDir $WinFspMsi
if (-not (Test-Path $winFspPath)) {
    Write-Host "Fetching $WinFspMsi ..."
    New-Item -ItemType Directory -Force $winFspDir | Out-Null
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -OutFile $winFspPath `
        -Uri "https://github.com/winfsp/winfsp/releases/download/$WinFspVersion/$WinFspMsi"
}
$hash = (Get-FileHash $winFspPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $WinFspSha256) {
    throw "$WinFspMsi has the SHA-256 hash $hash, expected $WinFspSha256"
}

# the application together with the .NET runtime
$project = Join-Path $root 'src\ImagepixelBackup.App\ImagepixelBackup.App.csproj'
$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}
Write-Host 'Publishing the application ...'
Invoke-Checked dotnet @('publish', $project, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '-p:DebugType=none', '-p:DebugSymbols=false',
    '--output', $publish, '--nologo', '--verbosity', 'quiet')

if ($PortalSettings) {
    $portal = Get-Content $PortalSettings -Raw | ConvertFrom-Json
    if (-not ($portal.url -like 'https://*') -or -not $portal.apiKey) {
        throw "$PortalSettings must name an address starting with https:// and a key"
    }
    Copy-Item $PortalSettings (Join-Path $publish 'portal.json')
    Write-Host 'The setup offers pairing with the customer portal.'
}

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    throw "No <Version> found in $project"
}

Write-Host "Building the setup program for version $version ..."
Invoke-Checked $iscc @('/Qp', "/DAppVersion=$version", "/DWinFspMsi=$WinFspMsi",
    (Join-Path $root 'installer\ImagepixelBackup.iss'))

$setup = Join-Path $root "artifacts\installer\ImagepixelBackup-Setup-$version.exe"
Write-Host ("Done: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
