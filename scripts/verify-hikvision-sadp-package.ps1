param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$expectedFiles = @{
    'sadp.exe' = '64d162d55671d5267a2a435c69018866090b874dde9b5d35784eca828b8c6413'
    'LICENSE-hikvision-tooling.txt' = 'e0fefd96c0c47be566ec00efe755dba1966709f798036ccb96b80c7127cf9fe6'
    'LICENSE-caarlos0-env.txt' = '657225b6c683571763995299113fe46677f110c70df88e95e75fa67a8772f5fa'
    'LICENSE-google-uuid.txt' = '0a8d61ed3cbfd5312326e8126c31ce9c627a283adc99131b56896d29ada04b2d'
    'LICENSE-uber-zap.txt' = 'c2b97b3281be272711909076c8402499b4b5a3216196af47a25b1d3674d86152'
    'LICENSE-uber-multierr.txt' = 'dcdabe03bef2382a130640d1c3a4cd5ec42aba1035095c38272fde694eb72405'
    'LICENSE-go.txt' = '911f8f5782931320f5b8d1160a76365b83aea6447ee6c04fa6d5591467db9dad'
    'NOTICE-hikvision-tooling.txt' = 'bf18893646f6f74c429a733dd0f93315bc2eefb85df5779b0272200e4358e36d'
}

if (-not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) {
    throw "Pinned Hikvision SADP runtime directory was not found: $RuntimeRoot"
}

$entries = @(Get-ChildItem -LiteralPath $RuntimeRoot -Force)
$unexpectedEntries = @($entries | Where-Object {
    $_.PSIsContainer -or -not ($expectedFiles.ContainsKey($_.Name))
})
if ($unexpectedEntries.Count -ne 0 -or $entries.Count -ne $expectedFiles.Count) {
    throw 'Pinned Hikvision SADP runtime must contain exactly the audited executable, NOTICE and dependency license set.'
}

foreach ($entry in $expectedFiles.GetEnumerator()) {
    $path = Join-Path $RuntimeRoot $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Pinned Hikvision SADP release file is missing: $($entry.Key)"
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value) {
        throw "Pinned Hikvision SADP release file failed SHA256 verification: $($entry.Key)"
    }
}

Write-Host "Verified pinned Hikvision SADP helper package: $RuntimeRoot"
