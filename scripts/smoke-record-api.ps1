[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testId = [Guid]::NewGuid().ToString("N")
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "UnpackVision-RecordApi-$testId"
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$databasePath = Join-Path $temporaryRoot "station.db"
$securityRoot = Join-Path $temporaryRoot "security"
$videoPath = Join-Path $temporaryRoot "sample.mp4"
$headersPath = Join-Path $temporaryRoot "range-headers.txt"
$chunkPath = Join-Path $temporaryRoot "range-chunk.bin"
$mediaHeadersPath = Join-Path $temporaryRoot "media-range-headers.txt"
$mediaChunkPath = Join-Path $temporaryRoot "media-range-chunk.bin"
[System.IO.File]::WriteAllBytes($videoPath, [byte[]](0..255))
$hostOutputDirectory = Join-Path $repositoryRoot "src\UnpackVision.StationHost\bin\Release\net10.0-windows"
$hostExecutable = Get-ChildItem -LiteralPath $hostOutputDirectory -Filter "*.exe" -File |
    Select-Object -First 1 -ExpandProperty FullName
$hostProcess = $null
$port = 5281

try {
    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $hostExecutable
    $processInfo.WorkingDirectory = Split-Path $hostExecutable
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.Arguments =
        "--StationHost:LoopbackPort=$port " +
        "--StationHost:LanHttpsEnabled=false " +
        "--StationHost:SecurityDirectory=$securityRoot " +
        "--Storage:DatabasePath=$databasePath " +
        "--Storage:RecordingRoot=$temporaryRoot"
    $processInfo.Environment["UNPACKVISION_ALLOW_TEST_INSTANCE"] = "1"
    $hostProcess = [System.Diagnostics.Process]::Start($processInfo)

    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/v1/health" -TimeoutSec 2
            break
        }
        catch {
            # The host is still starting.
        }
    }
    if ($null -eq $health) {
        throw "StationHost did not become healthy."
    }

    $importBody = @{
        trackingNo = "RANGE-TEST-001"
        videoPath = $videoPath
        workflow = "Unpacking"
    } | ConvertTo-Json
    $created = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$port/api/v1/records" `
        -Method Post `
        -ContentType "application/json" `
        -Body $importBody
    $page = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/v1/records?limit=1"
    $publicJson = $page | ConvertTo-Json -Depth 8 -Compress
    $media = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/v1/records/$($created.id)/media"
    $mediaJson = $media | ConvertTo-Json -Depth 8 -Compress
    $videoUrl = "http://127.0.0.1:$port/api/v1/records/$($created.id)/video"
    & curl.exe --silent --show-error --dump-header $headersPath --header "Range: bytes=0-3" --output $chunkPath $videoUrl
    if ($LASTEXITCODE -ne 0) {
        throw "curl range request failed with exit code $LASTEXITCODE"
    }
    $headers = Get-Content -LiteralPath $headersPath
    $statusLine = $headers | Select-Object -First 1
    $etag = $headers | Where-Object { $_ -like "ETag:*" } | Select-Object -First 1
    $mediaAssetId = $media[0].id
    $mediaVideoUrl = "http://127.0.0.1:$port/api/v1/records/$($created.id)/media/$mediaAssetId/video"
    & curl.exe --silent --show-error --dump-header $mediaHeadersPath --header "Range: bytes=4-7" --output $mediaChunkPath $mediaVideoUrl
    if ($LASTEXITCODE -ne 0) {
        throw "curl media range request failed with exit code $LASTEXITCODE"
    }
    $mediaHeaders = Get-Content -LiteralPath $mediaHeadersPath
    $mediaStatusLine = $mediaHeaders | Select-Object -First 1
    $mediaEtag = $mediaHeaders | Where-Object { $_ -like "ETag:*" } | Select-Object -First 1

    $result = [pscustomobject]@{
        StationHealthy = $health.status
        RecordCount = $page.items.Count
        TrackingNo = $page.items[0].trackingNo
        LocalPathHidden = -not $publicJson.Contains($temporaryRoot)
        HasVideo = $page.items[0].hasVideo
        MediaAssetCount = $media.Count
        MediaPathHidden = -not $mediaJson.Contains($temporaryRoot)
        RangeStatus = $statusLine.Trim()
        RangeBytes = (Get-Item -LiteralPath $chunkPath).Length
        ETagPresent = -not [string]::IsNullOrWhiteSpace($etag)
        MediaRangeStatus = $mediaStatusLine.Trim()
        MediaRangeBytes = (Get-Item -LiteralPath $mediaChunkPath).Length
        MediaETagPresent = -not [string]::IsNullOrWhiteSpace($mediaEtag)
        NextCursor = $page.nextCursor
    }
    $result
    if ($result.StationHealthy -ne 'healthy' -or
        $result.RecordCount -ne 1 -or
        -not $result.LocalPathHidden -or
        -not $result.HasVideo -or
        $result.MediaAssetCount -ne 1 -or
        -not $result.MediaPathHidden -or
        $result.RangeStatus -notlike '* 206 *' -or
        $result.RangeBytes -ne 4 -or
        -not $result.ETagPresent -or
        $result.MediaRangeStatus -notlike '* 206 *' -or
        $result.MediaRangeBytes -ne 4 -or
        -not $result.MediaETagPresent) {
        throw "Record API smoke-test result did not match the release contract."
    }
}
finally {
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id
    }
    Write-Host "Record API smoke-test artifacts: $temporaryRoot"
}
