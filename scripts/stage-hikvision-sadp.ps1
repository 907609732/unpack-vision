param(
    [string]$Destination = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repositoryRoot 'tools\hikvision-tooling\1.0.43'
}
$Destination = [System.IO.Path]::GetFullPath($Destination)
$allowedRoots = @(
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'tools')),
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
)
$destinationAllowed = $allowedRoots | Where-Object {
    $Destination.Equals($_, [System.StringComparison]::OrdinalIgnoreCase) -or
    $Destination.StartsWith($_ + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}
if ($null -eq $destinationAllowed) {
    throw 'Destination must stay inside this repository tools or artifacts directory.'
}

$releaseTag = 'v1.0.43-afa00e3'
$archiveName = 'sadp-windows-amd64.exe.tar.gz'
$archiveUrl = "https://github.com/cameronnewman/hikvision-tooling/releases/download/$releaseTag/$archiveName"
$archiveSha256 = 'edaf3e99ca155e640c94608bbb132ef386e11b0716fc22fb959666733424fda5'
$executableSha256 = '64d162d55671d5267a2a435c69018866090b874dde9b5d35784eca828b8c6413'
$licenseDownloads = @{
    'LICENSE-hikvision-tooling.txt' = @{
        Url = 'https://raw.githubusercontent.com/cameronnewman/hikvision-tooling/afa00e3/LICENSE'
        Sha256 = 'e0fefd96c0c47be566ec00efe755dba1966709f798036ccb96b80c7127cf9fe6'
    }
    'LICENSE-caarlos0-env.txt' = @{
        Url = 'https://raw.githubusercontent.com/caarlos0/env/v11.4.1/LICENSE.md'
        Sha256 = '657225b6c683571763995299113fe46677f110c70df88e95e75fa67a8772f5fa'
    }
    'LICENSE-google-uuid.txt' = @{
        Url = 'https://raw.githubusercontent.com/google/uuid/v1.6.0/LICENSE'
        Sha256 = '0a8d61ed3cbfd5312326e8126c31ce9c627a283adc99131b56896d29ada04b2d'
    }
    'LICENSE-uber-zap.txt' = @{
        Url = 'https://raw.githubusercontent.com/uber-go/zap/v1.28.0/LICENSE'
        Sha256 = 'c2b97b3281be272711909076c8402499b4b5a3216196af47a25b1d3674d86152'
    }
    'LICENSE-uber-multierr.txt' = @{
        Url = 'https://raw.githubusercontent.com/uber-go/multierr/v1.11.0/LICENSE.txt'
        Sha256 = 'dcdabe03bef2382a130640d1c3a4cd5ec42aba1035095c38272fde694eb72405'
    }
    'LICENSE-go.txt' = @{
        Url = 'https://raw.githubusercontent.com/golang/go/go1.26.5/LICENSE'
        Sha256 = '911f8f5782931320f5b8d1160a76365b83aea6447ee6c04fa6d5591467db9dad'
    }
}
$noticeSource = Join-Path $repositoryRoot 'licenses\hikvision-tooling\1.0.43\NOTICE-hikvision-tooling.txt'
$noticeSha256 = 'bf18893646f6f74c429a733dd0f93315bc2eefb85df5779b0272200e4358e36d'

$destinationExecutable = Join-Path $Destination 'sadp.exe'
$expectedFiles = @{
    'sadp.exe' = $executableSha256
    'NOTICE-hikvision-tooling.txt' = $noticeSha256
}
foreach ($license in $licenseDownloads.GetEnumerator()) {
    $expectedFiles[$license.Key] = $license.Value.Sha256
}
$expectedNames = @($expectedFiles.Keys)

function Test-FileHash {
    param([string]$Path, [string]$Expected)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Expected
}

if (Test-Path -LiteralPath $Destination -PathType Container) {
    $existingEntries = @(Get-ChildItem -LiteralPath $Destination -Force)
    $unexpectedEntries = @($existingEntries | Where-Object {
        $_.PSIsContainer -or $expectedNames -notcontains $_.Name
    })
    if ($unexpectedEntries.Count -ne 0) {
        throw 'Hikvision SADP stage contains unexpected files or directories; inspect it before rebuilding.'
    }
    $allExpectedFilesValid = $existingEntries.Count -eq $expectedNames.Count
    foreach ($expected in $expectedFiles.GetEnumerator()) {
        if (-not (Test-FileHash (Join-Path $Destination $expected.Key) $expected.Value)) {
            $allExpectedFilesValid = $false
        }
    }
    if ($allExpectedFilesValid) {
        Write-Output "Hikvision SADP helper $releaseTag is already staged and verified."
        return
    }
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('unpackvision-sadp-stage-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $temporaryRoot $archiveName
$extractedExecutable = Join-Path $temporaryRoot 'sadp-windows-amd64.exe'

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archive
    if (-not (Test-FileHash $archive $archiveSha256)) {
        throw "hikvision-tooling archive SHA256 mismatch. Expected $archiveSha256."
    }

    & tar -xzf $archive -C $temporaryRoot
    if ($LASTEXITCODE -ne 0 -or -not (Test-FileHash $extractedExecutable $executableSha256)) {
        throw "hikvision-tooling executable SHA256 mismatch. Expected $executableSha256."
    }

    if (-not (Test-FileHash $noticeSource $noticeSha256)) {
        throw 'Reviewed hikvision-tooling NOTICE is missing or has changed without updating its audit hash.'
    }

    foreach ($license in $licenseDownloads.GetEnumerator()) {
        $downloadedLicense = Join-Path $temporaryRoot $license.Key
        Invoke-WebRequest -Uri $license.Value.Url -OutFile $downloadedLicense
        if (-not (Test-FileHash $downloadedLicense $license.Value.Sha256)) {
            throw "Dependency license SHA256 mismatch: $($license.Key)."
        }
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath $extractedExecutable -Destination $destinationExecutable -Force
    Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $Destination 'NOTICE-hikvision-tooling.txt') -Force
    foreach ($license in $licenseDownloads.GetEnumerator()) {
        Copy-Item `
            -LiteralPath (Join-Path $temporaryRoot $license.Key) `
            -Destination (Join-Path $Destination $license.Key) `
            -Force
    }

    foreach ($expected in $expectedFiles.GetEnumerator()) {
        if (-not (Test-FileHash (Join-Path $Destination $expected.Key) $expected.Value)) {
            throw "Staged Hikvision SADP helper failed final integrity verification: $($expected.Key)."
        }
    }
    Write-Output "Staged and verified Hikvision SADP helper $releaseTag."
}
finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath($temporaryRoot)
    $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemp.StartsWith($systemTemp, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemp)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
