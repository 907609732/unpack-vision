param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$AppFolder = 'App-1.3.1'
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

& $dotnet publish (Join-Path $projectRoot 'src\UnpackVision.App\UnpackVision.App.csproj') -c Release -r $Runtime --self-contained true -o (Join-Path $publishRoot $AppFolder)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish (Join-Path $projectRoot 'src\UnpackVision.Service\UnpackVision.Service.csproj') -c Release -r $Runtime --self-contained true -o (Join-Path $publishRoot 'Service')
exit $LASTEXITCODE
