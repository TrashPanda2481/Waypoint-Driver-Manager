# Authenticode-signs a built binary using a cert from CurrentUser\My.
# Cert is referenced by thumbprint, so no key material lives in the repo.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Nothing to sign at '$Path'."
}

$signtool =
    Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Parent.Name -as [version] } |
    Sort-Object { [version]$_.Directory.Parent.Name } |
    Select-Object -Last 1

if (-not $signtool) {
    throw 'signtool.exe not found. Install the Windows SDK (Signing Tools component).'
}

# /tr + /td timestamps the signature so it stays valid past cert expiry.
& $signtool.FullName sign /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE."
}

$sig = Get-AuthenticodeSignature -LiteralPath $Path
Write-Host "Signed $Path"
Write-Host "  status : $($sig.Status)"
Write-Host "  signer : $($sig.SignerCertificate.Subject)"
