# Builds every distribution shape. Two variants, differing only in how the GUI
# carries .NET, because WPF has no Native AOT story and waypoint.exe does:
#
#   waypoint-*-<version>-<rid>.{zip,msi}                  ~170MB, runs anywhere
#   waypoint-*-<version>-<rid>-requires-dotnet8.{zip,msi} ~8MB, needs the
#                                                         .NET 8 Desktop Runtime
#
# waypoint.exe is identical in both: Native AOT, no runtime dependency ever.
# Only waypoint-desktop.exe differs, so on a machine with no runtime and no
# network the CLI still works from either package.
#
# Native AOT needs vswhere.exe on PATH; this adds it if the standard Visual
# Studio Installer location exists.

[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipMsi,
    [ValidateSet('both', 'bundled', 'requires-dotnet8')]
    [string]$Variant = 'both'
)

$ErrorActionPreference = 'Stop'

$packagingDir = $PSScriptRoot
$dotnetDir = Split-Path $packagingDir -Parent
$artifacts = Join-Path $packagingDir 'artifacts'
$cliPublishDir = Join-Path $dotnetDir "Waypoint.Cli\bin\$Configuration\net8.0\$Runtime\publish"

$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) {
    $env:PATH = "$env:PATH;$vsInstaller"
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
Get-ChildItem $artifacts -File | Remove-Item -Force

function Get-SigningThumbprint {
    if ($env:WAYPOINT_SIGN_THUMBPRINT) { return $env:WAYPOINT_SIGN_THUMBPRINT }
    $localProps = Join-Path $dotnetDir 'Directory.Build.local.props'
    if (Test-Path $localProps) {
        $match = Select-String -Path $localProps -Pattern '<WaypointSignThumbprint[^>]*>([0-9A-Fa-f]+)<' | Select-Object -First 1
        if ($match) { return $match.Matches[0].Groups[1].Value }
    }
    return $null
}

function Assert-Signed([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -eq 'NotSigned') {
        Write-Warning "$(Split-Path $Path -Leaf) is NOT signed. Run dotnet/setup-dev-signing.ps1 first."
    }
    else {
        Write-Host "  signed: $(Split-Path $Path -Leaf)"
    }
}

Write-Host "== publishing waypoint.exe ($Runtime, $Configuration, Native AOT) =="
dotnet publish (Join-Path $dotnetDir 'Waypoint.Cli') -c $Configuration -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (CLI) failed with exit code $LASTEXITCODE." }

$cliExe = Join-Path $cliPublishDir 'waypoint.exe'
if (-not (Test-Path $cliExe)) { throw "Expected published binary at $cliExe." }
Assert-Signed $cliExe

$thumbprint = Get-SigningThumbprint
if (-not $thumbprint) {
    Write-Warning "No signing identity found; MSIs will not be signed. Run dotnet/setup-dev-signing.ps1."
}

$variants = switch ($Variant) {
    'both' { @('bundled', 'requires-dotnet8') }
    default { @($Variant) }
}

foreach ($v in $variants) {
    $selfContained = $v -eq 'bundled'
    $suffix = if ($selfContained) { '' } else { '-requires-dotnet8' }
    $guiDir = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-gui-$v-$([guid]::NewGuid().ToString('N'))"

    Write-Host ""
    Write-Host "== $v =="
    dotnet publish (Join-Path $dotnetDir 'Waypoint.Gui') `
        -c $Configuration -r $Runtime `
        --self-contained $(if ($selfContained) { 'true' } else { 'false' }) `
        -o $guiDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (GUI, $v) failed with exit code $LASTEXITCODE." }

    $guiExe = Join-Path $guiDir 'waypoint-desktop.exe'
    if (-not (Test-Path $guiExe)) { throw "Expected published binary at $guiExe." }
    Assert-Signed $guiExe

    # PDBs are debug output; they belong in neither shape.
    Get-ChildItem $guiDir -Recurse -Filter *.pdb | Remove-Item -Force

    try {
        $note = if ($selfContained) {
            @"
This build carries its own copy of .NET. Nothing else to install.
"@
        }
        else {
            @"
This build needs the .NET 8 Desktop Runtime for waypoint-desktop.exe:
https://dotnet.microsoft.com/download/dotnet/8.0 (x64 Desktop Runtime)

waypoint.exe does NOT need it. The CLI is self-contained in both builds,
so it still works on a machine with no runtime and no network.
"@
        }

        # --- portable zip ---
        $zipPath = Join-Path $artifacts "waypoint-portable-$Version-$Runtime$suffix.zip"
        $staging = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-portable-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        try {
            Copy-Item $cliExe (Join-Path $staging 'waypoint.exe')
            Copy-Item (Join-Path $guiDir '*') $staging -Recurse
            @"
Waypoint Driver Manager $Version (portable)

Run waypoint.exe for the command line, or waypoint-desktop.exe for the
window. Nothing is installed and nothing is written outside this folder
except the cache and audit log under C:\ProgramData\Waypoint. Delete this
folder to remove it.

$note
For an installed copy that is on PATH for every shell, use the .msi instead.

https://github.com/TrashPanda2481/Waypoint-Driver-Manager
"@ | Set-Content (Join-Path $staging 'README.txt') -Encoding utf8

            Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force
        }
        finally {
            Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host "  wrote $(Split-Path $zipPath -Leaf)"

        if ($SkipMsi) { continue }

        # --- msi ---
        $dotnetTools = Join-Path $env:USERPROFILE '.dotnet\tools'
        if ((Test-Path $dotnetTools) -and ($env:PATH -notlike "*$dotnetTools*")) {
            $env:PATH = "$env:PATH;$dotnetTools"
        }
        if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
            throw "wix not found. Install it with: dotnet tool install --global wix --version 5.*"
        }

        $msiPath = Join-Path $artifacts "waypoint-installer-$Version-$Runtime$suffix.msi"
        wix build (Join-Path $packagingDir 'Waypoint.wxs') `
            -arch x64 `
            -ext WixToolset.UI.wixext `
            -d "ProductVersion=$Version" `
            -d "CliExe=$cliExe" `
            -d "GuiDir=$guiDir" `
            -o $msiPath
        if ($LASTEXITCODE -ne 0) { throw "wix build ($v) failed with exit code $LASTEXITCODE." }

        # The MSI is a separate artifact from the exes inside it, so it needs
        # its own signature.
        if ($thumbprint) {
            & (Join-Path $dotnetDir 'sign.ps1') -Path $msiPath -Thumbprint $thumbprint
        }

        # wix drops a .wixpdb next to the msi; it is build symbols, not something
        # to publish, and artifacts/ is the folder that gets uploaded.
        Remove-Item ([System.IO.Path]::ChangeExtension($msiPath, 'wixpdb')) -Force -ErrorAction SilentlyContinue

        Write-Host "  wrote $(Split-Path $msiPath -Leaf)"
    }
    finally {
        Remove-Item $guiDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Get-ChildItem $artifacts -File |
    Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Sort-Object Name | Format-Table -AutoSize
