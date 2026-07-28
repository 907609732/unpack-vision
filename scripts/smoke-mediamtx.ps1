[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testId = [Guid]::NewGuid().ToString("N")
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "UnpackVision-MediaSmoke-$testId"
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$databasePath = Join-Path $temporaryRoot "station.db"
$relayRoot = Join-Path $temporaryRoot "relay"
$hostOutputDirectory = Join-Path $repositoryRoot "src\UnpackVision.StationHost\bin\Release\net10.0-windows"
$hostExecutable = Get-ChildItem -LiteralPath $hostOutputDirectory -Filter "*.exe" -File |
    Select-Object -First 1 -ExpandProperty FullName
$mediaExecutable = Join-Path $repositoryRoot "tools\mediamtx\1.18.2\mediamtx.exe"
$hostProcess = $null

try {
    $arguments = @(
        "--urls", "http://127.0.0.1:5271",
        "--Storage:DatabasePath=$databasePath",
        "--MediaRelay:RuntimeDirectory=$relayRoot",
        "--MediaRelay:ExecutablePath=$mediaExecutable"
    )
    $hostExists = Test-Path -LiteralPath $hostExecutable
    $mediaExists = Test-Path -LiteralPath $mediaExecutable
    if (-not $hostExists -or -not $mediaExists) {
        throw "Missing prerequisites. StationHost=$hostExists ($hostExecutable); MediaMTX=$mediaExists ($mediaExecutable)"
    }
    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $hostExecutable
    $processInfo.WorkingDirectory = Split-Path $hostExecutable
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.Arguments = $arguments -join " "
    $hostProcess = [System.Diagnostics.Process]::Start($processInfo)

    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:5271/api/v1/health" -TimeoutSec 2
            break
        }
        catch {
            # The host is still starting.
        }
    }
    if ($null -eq $health) {
        throw "StationHost did not become healthy."
    }

    $session = Invoke-RestMethod -Uri "http://127.0.0.1:5271/api/v1/pairing/sessions" -Method Post
    $pairBody = @{
        sessionId = $session.id
        token = $session.token
        name = "Media smoke phone"
        publicKey = "test-public-key"
        roles = @("camera", "scanner")
        scopes = @("scan:send", "camera:publish", "records:read", "video:read")
    } | ConvertTo-Json
    $credential = Invoke-RestMethod `
        -Uri "http://127.0.0.1:5271/device/v1/pair" `
        -Method Post `
        -ContentType "application/json" `
        -Body $pairBody
    $headers = @{
        "X-UnpackVision-Device" = $credential.device.id
        Authorization = "Bearer $($credential.accessToken)"
    }
    $publish = Invoke-RestMethod `
        -Uri "http://127.0.0.1:5271/api/v1/media/publish-session" `
        -Method Post `
        -Headers $headers `
        -TimeoutSec 15
    $relayInfo = Invoke-RestMethod -Uri "http://127.0.0.1:9997/v3/info" -TimeoutSec 5
    $authBody = @{
        user = $credential.device.id
        password = $credential.accessToken
        token = ""
        ip = "127.0.0.1"
        action = "publish"
        path = $publish.streamPath
        protocol = "rtsp"
    } | ConvertTo-Json
    $null = Invoke-RestMethod `
        -Uri "http://127.0.0.1:5271/internal/media/auth" `
        -Method Post `
        -ContentType "application/json" `
        -Body $authBody `
        -TimeoutSec 5
    $stationId = [uri]::EscapeDataString($health.station.stationId)
    $stationState = Invoke-RestMethod `
        -Uri "http://127.0.0.1:5271/api/v1/stations/$stationId/state" `
        -Headers $headers `
        -TimeoutSec 5
    $live = Invoke-RestMethod `
        -Uri "http://127.0.0.1:5271/api/v1/stations/$stationId/live?deviceId=$($credential.device.id)" `
        -Headers $headers `
        -TimeoutSec 10

    [pscustomobject]@{
        StationHealthy = $health.status
        MediaRelayRunning = $publish.rtspUrl -like "rtsp://*"
        RelayVersion = $relayInfo.version
        PublishPath = $publish.streamPath
        PublishUrl = $publish.rtspUrl
        AuthStatus = 200
        StationState = $stationState.recordingState
        WhepUrl = $live.whepUrl
        RuntimeConfigExists = Test-Path (Join-Path $relayRoot "mediamtx.yml")
    }
}
finally {
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id
    }
    Start-Sleep -Milliseconds 500
    Get-CimInstance Win32_Process |
        Where-Object { $_.Name -eq "mediamtx.exe" -and $_.CommandLine -like "*$testId*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId }
    Write-Host "Smoke-test artifacts: $temporaryRoot"
}
