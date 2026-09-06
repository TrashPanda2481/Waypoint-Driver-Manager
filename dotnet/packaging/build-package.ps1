# Builds both distribution shapes from one publish:
#   waypoint-<version>-win-x64-portable.zip   unzip and run, nothing installed
#   waypoint-<version>-win-x64.msi            per-machine install, on PATH
#
# Native AOT needs vswhere.exe on PATH; this adds it if the standard Visual
# Studio Installer location exists.

[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipMsi
)

$ErrorActionPreference = 'Stop'

$packagingDir = $PSScriptRoot
$dotnetDir = Split-Path $packagingDir -Parent
$artifacts = Join-Path $packagingDir 'artifacts'
$publishDir = Join-Path $dotnetDir "Waypoint.Cli\bin\$Configuration\net8.0\$Runtime\publish"

$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) {
    $env:PATH = "$env:PATH;$vsInstaller"
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
Get-ChildItem $artifacts -File | Remove-Item -Force

Write-Host "== publishing $Runtime ($Configuration) =="
dotnet publish (Join-Path $dotnetDir 'Waypoint.Cli') -c $Configuration -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$cliExe = Join-Path $publishDir 'waypoint.exe'
if (-not (Test-Path $cliExe)) { throw "Expected published binary at $cliExe." }

$signature = Get-AuthenticodeSignature -LiteralPath $cliExe
if ($signature.Status -eq 'NotSigned') {
    Write-Warning "waypoint.exe is NOT signed. Run dotnet/setup-dev-signing.ps1 first."
}
else {
    Write-Host "signed by: $($signature.SignerCertificate.Subject)"
}

Write-Host ""
Write-Host "== portable zip =="
$zipPath = Join-Path $artifacts "waypoint-$Version-$Runtime-portable.zip"
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-portable-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    Copy-Item $cliExe (Join-Path $staging 'waypoint.exe')
    @"
Waypoint Driver Manager $Version (portable)

Run waypoint.exe from anywhere. Nothing is installed and nothing is written
outside this folder except the cache and audit log under
C:\ProgramData\Waypoint. Delete this folder to remove it.

For an installed copy that is on PATH for every shell, use the .msi instead.

https://github.com/TrashPanda2481/Waypoint-Driver-Manager
"@ | Set-Content (Join-Path $staging 'README.txt') -Encoding utf8

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "wrote $zipPath"

if ($SkipMsi) {
    Write-Host ""
    Write-Host "MSI skipped (-SkipMsi)."
    return
}

Write-Host ""
Write-Host "== msi =="
$dotnetTools = Join-Path $env:USERPROFILE '.dotnet\tools'
if ((Test-Path $dotnetTools) -and ($env:PATH -notlike "*$dotnetTools*")) {
    $env:PATH = "$env:PATH;$dotnetTools"
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "wix not found. Install it with: dotnet tool install --global wix --version 5.*"
}

$msiPath = Join-Path $artifacts "waypoint-$Version-$Runtime.msi"
wix build (Join-Path $packagingDir 'Waypoint.wxs') `
    -arch x64 `
    -d "ProductVersion=$Version" `
    -d "CliExe=$cliExe" `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE." }

# The MSI is a separate artifact from the exe inside it, so it needs its own signature.
$thumbprint = $env:WAYPOINT_SIGN_THUMBPRINT
if (-not $thumbprint) {
    $localProps = Join-Path $dotnetDir 'Directory.Build.local.props'
    if (Test-Path $localProps) {
        $match = Select-String -Path $localProps -Pattern '<WaypointSignThumbprint[^>]*>([0-9A-Fa-f]+)<' | Select-Object -First 1
        if ($match) { $thumbprint = $match.Matches[0].Groups[1].Value }
    }
}

if ($thumbprint) {
    & (Join-Path $dotnetDir 'sign.ps1') -Path $msiPath -Thumbprint $thumbprint
}
else {
    Write-Warning "MSI not signed: no signing identity found. Run dotnet/setup-dev-signing.ps1."
}

Write-Host ""
Write-Host "wrote $msiPath"
Write-Host ""
Get-ChildItem $artifacts -File | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
