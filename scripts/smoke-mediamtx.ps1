[CmdletBinding()]
param(
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$systemTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
Get-ChildItem -LiteralPath $systemTempRoot -Directory -Filter 'UnpackVision-MediaSmoke-*' |
    Where-Object LastWriteTimeUtc -lt ([DateTime]::UtcNow.AddMinutes(-2)) |
    ForEach-Object {
        $stale = [System.IO.Path]::GetFullPath($_.FullName)
        if (-not $stale.StartsWith($systemTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe stale smoke-test cleanup target: $stale"
        }
        Remove-Item -LiteralPath $stale -Recurse -Force
    }
$testId = [Guid]::NewGuid().ToString("N")
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "UnpackVision-MediaSmoke-$testId"
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$databasePath = Join-Path $temporaryRoot "station.db"
$relayRoot = Join-Path $temporaryRoot "relay"
$securityRoot = Join-Path $temporaryRoot "security"
$hostOutputDirectory = Join-Path $repositoryRoot "src\UnpackVision.StationHost\bin\Release\net10.0-windows"
$hostExecutable = Get-ChildItem -LiteralPath $hostOutputDirectory -Filter "*.exe" -File |
    Where-Object Name -ne 'createdump.exe' |
    Sort-Object Length -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$mediaExecutable = Join-Path $repositoryRoot "tools\mediamtx\1.18.2\mediamtx.exe"
$hostProcess = $null
$loopbackPort = 5282
$controlPort = 19997

try {
    $arguments = @(
        "--StationHost:LoopbackPort=$loopbackPort",
        "--StationHost:LanHttpsEnabled=false",
        "--StationHost:AdvertisedAddress=https://127.0.0.1:$loopbackPort",
        "--Storage:DatabasePath=$databasePath",
        "--Storage:RecordingRoot=$temporaryRoot\videos",
        "--StationHost:SecurityDirectory=$securityRoot",
        "--StationHost:StationId=media-smoke",
        "--MediaRelay:RuntimeDirectory=$relayRoot",
        "--MediaRelay:ExecutablePath=$mediaExecutable",
        "--MediaRelay:AuthHttpAddress=http://127.0.0.1:$loopbackPort/internal/media/auth",
        "--MediaRelay:ControlApiAddress=http://127.0.0.1:$controlPort",
        "--MediaRelay:RtspPort=18554",
        "--MediaRelay:RtspsPort=18555",
        "--MediaRelay:WebRtcPort=18889",
        "--MediaRelay:WebRtcUdpPort=18189"
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
    $processInfo.Environment["UNPACKVISION_ALLOW_TEST_INSTANCE"] = "1"
    $hostProcess = [System.Diagnostics.Process]::Start($processInfo)

    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$loopbackPort/api/v1/health" -TimeoutSec 2
            break
        }
        catch {
            # The host is still starting.
        }
    }
    if ($null -eq $health) {
        throw "StationHost did not become healthy."
    }

    $session = Invoke-RestMethod -Uri "http://127.0.0.1:$loopbackPort/api/v1/pairing/sessions" -Method Post
    $pairBody = @{
        sessionId = $session.id
        token = $session.token
        name = "Media smoke phone"
        publicKey = "test-public-key"
        roles = @("camera", "scanner")
        scopes = @("scan:send", "camera:publish", "records:read", "video:read")
    } | ConvertTo-Json
    $credential = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$loopbackPort/device/v1/pair" `
        -Method Post `
        -ContentType "application/json" `
        -Body $pairBody
    $headers = @{
        "X-UnpackVision-Device" = $credential.device.id
        Authorization = "Bearer $($credential.accessToken)"
    }
    $publish = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$loopbackPort/api/v1/media/publish-session" `
        -Method Post `
        -Headers $headers `
        -TimeoutSec 15
    $relayInfo = Invoke-RestMethod -Uri "http://127.0.0.1:$controlPort/v3/info" -TimeoutSec 5
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
        -Uri "http://127.0.0.1:$loopbackPort/internal/media/auth" `
        -Method Post `
        -ContentType "application/json" `
        -Body $authBody `
        -TimeoutSec 5
    $stationId = [uri]::EscapeDataString('media-smoke')
    $stationState = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$loopbackPort/api/v1/stations/$stationId/state" `
        -Headers $headers `
        -TimeoutSec 5
    $live = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$loopbackPort/api/v1/stations/$stationId/live?deviceId=$($credential.device.id)" `
        -Headers $headers `
        -TimeoutSec 10

    $result = [pscustomobject]@{
        StationHealthy = $health.status
        MediaRelayRunning = $publish.rtspUrl -like "rtsps://*"
        RelayVersion = $relayInfo.version
        PublishPath = $publish.streamPath
        PublishUrl = $publish.rtspUrl
        AuthStatus = 200
        StationState = $stationState.recordingState
        WhepUrl = $live.whepUrl
        RuntimeConfigExists = Test-Path (Join-Path $relayRoot "mediamtx.yml")
    }
    $result
    if ($result.StationHealthy -ne 'healthy' -or
        -not $result.MediaRelayRunning -or
        $result.AuthStatus -ne 200 -or
        -not $result.RuntimeConfigExists -or
        [string]::IsNullOrWhiteSpace($result.WhepUrl)) {
        throw "Media relay smoke-test result did not match the release contract."
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
    if ($KeepArtifacts) {
        Write-Host "Smoke-test artifacts: $temporaryRoot"
    }
    else {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        if (-not $resolvedTemporaryRoot.StartsWith(
                $systemTempRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe smoke-test cleanup target: $resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
