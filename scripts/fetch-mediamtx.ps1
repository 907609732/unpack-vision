[CmdletBinding()]
param(
    [string]$Destination
)

$ErrorActionPreference = "Stop"
$version = "1.18.2"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $scriptDirectory "..\tools\mediamtx\$version"
}
$fileName = "mediamtx_v${version}_windows_amd64.zip"
$expectedSha256 = "945ab46c5fc6d2802ad18e2f1d7e49245ca5609657d85e310aa6eda4cdd72eec"
$url = "https://github.com/bluenviron/mediamtx/releases/download/v${version}/$fileName"
$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)

if (Test-Path -LiteralPath $resolvedDestination) {
    $existing = Join-Path $resolvedDestination "mediamtx.exe"
    if (Test-Path -LiteralPath $existing) {
        Write-Host "MediaMTX $version already exists: $existing"
        exit 0
    }
    throw "Destination exists but is incomplete: $resolvedDestination"
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "UnpackVision-MediaMTX-$([Guid]::NewGuid().ToString('N'))"
$archive = Join-Path $temporaryRoot $fileName
$expanded = Join-Path $temporaryRoot "expanded"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
    Invoke-WebRequest -Uri $url -OutFile $archive
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    if ($actual -ne $expectedSha256) {
        throw "MediaMTX SHA256 mismatch. Expected $expectedSha256, got $actual"
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedDestination) -Force | Out-Null
    Move-Item -LiteralPath $expanded -Destination $resolvedDestination
    Write-Host "MediaMTX $version downloaded and verified: $resolvedDestination"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
