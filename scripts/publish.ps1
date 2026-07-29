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

$appOutput = Join-Path $publishRoot $AppFolder
$stationOutput = Join-Path $appOutput 'StationHost'
if (Test-Path -LiteralPath $appOutput) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($appOutput)
    $resolvedPublishRoot = [System.IO.Path]::GetFullPath($publishRoot)
    if (-not $resolvedOutput.StartsWith($resolvedPublishRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a publish directory outside $resolvedPublishRoot"
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $appOutput
$scriptOutput = Join-Path $appOutput 'Scripts'
New-Item -ItemType Directory -Path $scriptOutput -Force | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $projectRoot 'scripts\configure-private-firewall.ps1') `
    -Destination $scriptOutput

& $dotnet publish (Join-Path $projectRoot 'src\UnpackVision.Service\UnpackVision.Service.csproj') -c Release -r $Runtime --self-contained true -o (Join-Path $appOutput 'Service')
exit $LASTEXITCODE
