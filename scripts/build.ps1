param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'

if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw '.NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    $dotnet = $dotnetCommand.Source
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'

& $dotnet restore (Join-Path $projectRoot 'UnpackVision.slnx')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $projectRoot 'UnpackVision.slnx') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# WPF only permits one Application instance per test AppDomain, including after Shutdown.
# Keep the two rendered-window acceptance tests in independent testhost processes while the
# remaining suite continues to run together with normal parallelism.
$solution = Join-Path $projectRoot 'UnpackVision.slnx'
& $dotnet test $solution -c $Configuration --no-build --no-restore `
    --filter 'FullyQualifiedName!~HistoryWindowUiTests&FullyQualifiedName!~VideoPlayerWindowUiTests'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($uiTest in @('HistoryWindowUiTests', 'VideoPlayerWindowUiTests')) {
    & $dotnet test $solution -c $Configuration --no-build --no-restore `
        --filter "FullyQualifiedName~$uiTest"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
