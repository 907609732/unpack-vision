param(
    [switch]$ShowApiKey
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$project = Join-Path $projectRoot 'src\UnpackVision.Service\UnpackVision.Service.csproj'

if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw '.NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    $dotnet = $dotnetCommand.Source
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'

$arguments = @('run', '--project', $project, '-c', 'Release')
if ($ShowApiKey) {
    $arguments += @('--', '--show-api-key')
}

& $dotnet @arguments
exit $LASTEXITCODE
