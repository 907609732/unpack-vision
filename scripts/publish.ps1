param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$AppFolder = 'App'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$publishRoot = Join-Path $projectRoot 'artifacts\publish'

if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw '.NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    $dotnet = $dotnetCommand.Source
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'

$resolvedPublishRoot = [System.IO.Path]::GetFullPath($publishRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$appOutput = [System.IO.Path]::GetFullPath((Join-Path $resolvedPublishRoot $AppFolder))
$allowedPublishPrefix = $resolvedPublishRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not $appOutput.StartsWith($allowedPublishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must stay below $resolvedPublishRoot"
}
$stationOutput = Join-Path $appOutput 'StationHost'
if (Test-Path -LiteralPath $appOutput) {
    Remove-Item -LiteralPath $appOutput -Recurse -Force
}
& $dotnet publish (Join-Path $projectRoot 'src\UnpackVision.App\UnpackVision.App.csproj') -c Release -r $Runtime --self-contained true -o $appOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish (Join-Path $projectRoot 'src\UnpackVision.StationHost\UnpackVision.StationHost.csproj') -c Release -r $Runtime --self-contained true -o $stationOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$mediaRoot = Join-Path $projectRoot 'tools\mediamtx\1.18.2'
if (-not (Test-Path -LiteralPath (Join-Path $mediaRoot 'mediamtx.exe'))) {
    & (Join-Path $PSScriptRoot 'fetch-mediamtx.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$mediaOutput = Join-Path $stationOutput 'MediaMTX'
New-Item -ItemType Directory -Path $mediaOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $mediaRoot 'mediamtx.exe') -Destination $mediaOutput
Copy-Item -LiteralPath (Join-Path $mediaRoot 'LICENSE') -Destination (Join-Path $mediaOutput 'LICENSE-MediaMTX.txt')

# Release media binaries come from a reproducible, hash-pinned stage. The stage script
# copies only the reviewed dependency closure instead of the full GStreamer installer.
$gstreamerStage = Join-Path $projectRoot 'tools\gstreamer\1.28.5'
& (Join-Path $PSScriptRoot 'stage-media-runtimes.ps1') -Destination $gstreamerStage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$gstreamerOutput = Join-Path $appOutput 'runtimes\gstreamer\1.28.5'
New-Item -ItemType Directory -Path (Split-Path -Parent $gstreamerOutput) -Force | Out-Null
Copy-Item -LiteralPath $gstreamerStage -Destination $gstreamerOutput -Recurse
& (Join-Path $PSScriptRoot 'verify-media-runtime-package.ps1') -RuntimeRoot $gstreamerOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The optional Hikvision compatibility probe is the complete hash-pinned upstream CLI.
# UnpackVision invokes it only with the read-only discover:sadp command; the package also
# carries the audited Go-module licenses and an explicit command-surface NOTICE.
$sadpStage = Join-Path $projectRoot 'tools\hikvision-tooling\1.0.43'
& (Join-Path $PSScriptRoot 'stage-hikvision-sadp.ps1') -Destination $sadpStage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$sadpOutput = Join-Path $appOutput 'runtimes\hikvision-tooling\1.0.43'
New-Item -ItemType Directory -Path $sadpOutput -Force | Out-Null
$sadpPackageFiles = @(
    'sadp.exe',
    'NOTICE-hikvision-tooling.txt',
    'LICENSE-hikvision-tooling.txt',
    'LICENSE-caarlos0-env.txt',
    'LICENSE-google-uuid.txt',
    'LICENSE-uber-zap.txt',
    'LICENSE-uber-multierr.txt',
    'LICENSE-go.txt'
)
foreach ($sadpPackageFile in $sadpPackageFiles) {
    Copy-Item -LiteralPath (Join-Path $sadpStage $sadpPackageFile) -Destination $sadpOutput
}
& (Join-Path $PSScriptRoot 'verify-hikvision-sadp-package.ps1') -RuntimeRoot $sadpOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $appOutput
$scriptOutput = Join-Path $appOutput 'Scripts'
New-Item -ItemType Directory -Path $scriptOutput -Force | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $projectRoot 'scripts\configure-private-firewall.ps1') `
    -Destination $scriptOutput

& $dotnet publish (Join-Path $projectRoot 'src\UnpackVision.Service\UnpackVision.Service.csproj') -c Release -r $Runtime --self-contained true -o (Join-Path $appOutput 'Service')
exit $LASTEXITCODE
