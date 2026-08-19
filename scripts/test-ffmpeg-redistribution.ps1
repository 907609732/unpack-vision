[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FfmpegPath,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
$expectedVersion = '8.1.2'
$path = [IO.Path]::GetFullPath($FfmpegPath)
if (-not (Test-Path -LiteralPath $path)) { throw "FFmpeg was not found: $path" }
$output = & $path -hide_banner -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg version probe failed.' }
$versionMatch = [regex]::Match($output, '(?im)^ffmpeg version\s+(?:n-)?(?<version>\d+\.\d+(?:\.\d+)?)')
if (-not $versionMatch.Success -or $versionMatch.Groups['version'].Value -ne $expectedVersion) {
    throw "Only FFmpeg $expectedVersion may be redistributed."
}
$forbiddenFlags = @('--enable-gpl', '--enable-nonfree') | Where-Object {
    $output.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
}
if ($forbiddenFlags.Count -gt 0) {
    throw "FFmpeg redistribution refused because forbidden build flags were detected: $($forbiddenFlags -join ', ')"
}
$result = [pscustomobject]@{
    Version = $expectedVersion
    Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Policy = 'LGPL-only metadata probe passed; --enable-gpl and --enable-nonfree absent'
}
if ($PassThru) { return $result }
Write-Host "FFmpeg $expectedVersion redistribution metadata gate passed: $path"
