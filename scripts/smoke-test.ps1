$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$smokeRoot = Join-Path $projectRoot 'artifacts\smoke'
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$serviceProject = Join-Path $projectRoot 'src\UnpackVision.Service\UnpackVision.Service.csproj'

if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw '.NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    $dotnet = $dotnetCommand.Source
}

New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'
$env:UNPACKVISION_ALLOW_TEST_INSTANCE = '1'
$env:Urls = 'http://127.0.0.1:5189'
$env:Storage__DatabasePath = Join-Path $smokeRoot 'smoke.db'
$env:Storage__RecordingRoot = Join-Path $smokeRoot 'recordings'
$env:Excel__WorkbookPath = Join-Path $smokeRoot 'missing.xlsx'
$env:Excel__BackupRoot = Join-Path $smokeRoot 'backups'
$env:HikCompatibility__UnpackingDirectory = Join-Path $smokeRoot 'hik-unpacking'
$env:HikCompatibility__PackingDirectory = Join-Path $smokeRoot 'hik-packing'
$env:Security__ApiKeyPath = Join-Path $smokeRoot 'api-key.protected'

$stdout = Join-Path $smokeRoot 'service.stdout.log'
$stderr = Join-Path $smokeRoot 'service.stderr.log'
$arguments = @('run', '--project', $serviceProject, '-c', 'Release', '--no-build')
$service = Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

try {
    $health = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5189/api/v1/health' -TimeoutSec 2
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if ($null -eq $health) {
        $details = Get-Content -Raw -ErrorAction SilentlyContinue -LiteralPath $stderr
        throw "Service did not start in time. $details"
    }

    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:5189/api/v1/records' -TimeoutSec 2 | Out-Null
        $authStatus = 200
    }
    catch {
        $authStatus = [int]$_.Exception.Response.StatusCode
    }

    [pscustomobject]@{
        HealthStatus = $health.status
        Database = $health.database
        ApiWithoutKeyStatus = $authStatus
        BoundAddress = '127.0.0.1:5189'
    } | Format-List

    if (-not $health.database.healthy -or $health.status -ne 'degraded' -or $authStatus -ne 401) {
        throw 'Smoke-test result did not match expectations.'
    }
}
finally {
    if (-not $service.HasExited) {
        Stop-Process -Id $service.Id
        $service.WaitForExit(5000) | Out-Null
    }
}
