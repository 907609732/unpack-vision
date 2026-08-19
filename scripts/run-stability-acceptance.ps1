[CmdletBinding()]
param(
    [ValidateSet('EightCamera', 'SixteenCamera', 'HundredPackage', 'MixedSource', 'StoragePool', 'LowEnd', 'Custom')]
    [string]$Mode = 'Custom',
    [double]$DurationMinutes = 120,
    [int]$ExpectedCameraCount = 0,
    [int]$ExpectedPackages = 0,
    [string]$DatabasePath = (Join-Path $env:LOCALAPPDATA 'UnpackVision\unpackvision.db'),
    [string]$WorkbookPath = '',
    [string[]]$RecordingRoot = @(),
    [string]$SettingsPath = (Join-Path $env:LOCALAPPDATA 'UnpackVision\settings.json'),
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'UnpackVision\Acceptance'),
    [int]$PollSeconds = 10,
    [switch]$ProbeMedia,
    [string]$FfprobePath = '',
    [switch]$StopWhenDesktopExits
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Min($sorted.Count - 1, [Math]::Max(0, [Math]::Ceiling($sorted.Count * $Percentile) - 1))
    return [Math]::Round([double]$sorted[$index], 2)
}

function Get-OptionalProperty {
    param([object]$InputObject, [string]$Name)
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-DesktopProcesses {
    $names = @('拆包智录', 'UnpackVision.App', 'EcommerceUnpackRecorder')
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName })
}

function Get-DiskSample {
    param([string[]]$Roots)
    $samples = @()
    foreach ($root in $Roots) {
        $exists = Test-Path -LiteralPath $root -PathType Container
        $freeBytes = $null
        $totalBytes = $null
        $driveName = [System.IO.Path]::GetPathRoot($root)
        if ($driveName) {
            $trimmed = $driveName.TrimEnd('\').TrimEnd(':')
            $drive = Get-PSDrive -Name $trimmed -ErrorAction SilentlyContinue
            if ($drive) {
                $freeBytes = [long]$drive.Free
                $totalBytes = [long]($drive.Free + $drive.Used)
            }
        }
        $samples += [pscustomobject]@{
            rootId = if ($driveName) { $driveName.TrimEnd('\') } else { 'network' }
            online = [bool]$exists
            freeBytes = $freeBytes
            totalBytes = $totalBytes
        }
    }
    return $samples
}

function Get-ResourceSample {
    param([string[]]$Roots)
    $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction SilentlyContinue
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
    $processes = @(Get-DesktopProcesses)
    $nvidia = Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue
    $gpu = $null
    if ($nvidia) {
        $gpuLine = & $nvidia.Source --query-gpu=utilization.gpu --format=csv,noheader,nounits 2>$null | Select-Object -First 1
        $parsedGpu = 0.0
        if ([double]::TryParse(($gpuLine -as [string]), [ref]$parsedGpu)) { $gpu = $parsedGpu }
    }
    $freeMemoryMb = $null
    if ($os) { $freeMemoryMb = [Math]::Round([double]$os.FreePhysicalMemory / 1024, 1) }
    $cpuValue = $null
    if ($cpu) { $cpuValue = [double]$cpu.PercentProcessorTime }
    $workingSetBytes = 0.0
    if ($processes.Count -gt 0) {
        $workingSetBytes = [double](($processes | Measure-Object WorkingSet64 -Sum).Sum)
    }
    $workingSetMb = [Math]::Round(($workingSetBytes / 1MB), 1)
    return [pscustomobject]@{
        sampledAt = [DateTimeOffset]::Now.ToString('O')
        cpuPercent = $cpuValue
        gpuPercent = $gpu
        freeMemoryMb = $freeMemoryMb
        desktopProcessCount = $processes.Count
        desktopWorkingSetMb = $workingSetMb
        disks = @(Get-DiskSample -Roots $Roots)
    }
}

function Write-ResourceCsv {
    param([object]$Sample, [string]$Path)
    $lowestFree = $null
    $onlineCount = 0
    if (@($Sample.disks).Count -gt 0) {
        $onlineCount = @($Sample.disks | Where-Object online).Count
        $freeValues = @($Sample.disks | Where-Object { $null -ne $_.freeBytes } | ForEach-Object { [double]$_.freeBytes })
        if (@($freeValues).Count -gt 0) { $lowestFree = ($freeValues | Measure-Object -Minimum).Minimum }
    }
    [pscustomobject]@{
        sampledAt = $Sample.sampledAt
        cpuPercent = $Sample.cpuPercent
        gpuPercent = $Sample.gpuPercent
        freeMemoryMb = $Sample.freeMemoryMb
        desktopProcessCount = $Sample.desktopProcessCount
        desktopWorkingSetMb = $Sample.desktopWorkingSetMb
        onlineStorageTargets = $onlineCount
        lowestFreeBytes = $lowestFree
    } | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8 -Append
}

function Write-Event {
    param([string]$Type, [string]$Message, [string]$Path)
    $event = [ordered]@{
        occurredAt = [DateTimeOffset]::Now.ToString('O')
        type = $Type
        message = $Message
    }
    Add-Content -LiteralPath $Path -Value ($event | ConvertTo-Json -Compress) -Encoding UTF8
}

function Invoke-AcceptanceSnapshot {
    param(
        [string]$Output,
        [DateTimeOffset]$Since,
        [DateTimeOffset]$Until,
        [bool]$IncludeMediaProbe
    )
    $arguments = @(
        $acceptanceDll,
        '--database', $DatabasePath,
        '--output', $Output,
        '--since', $Since.ToString('O'),
        '--until', $Until.ToString('O'),
        '--expected-cameras', $ExpectedCameraCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    if (-not [string]::IsNullOrWhiteSpace($WorkbookPath)) { $arguments += @('--workbook', $WorkbookPath) }
    foreach ($root in $RecordingRoot) { $arguments += @('--root', $root) }
    if ($IncludeMediaProbe) { $arguments += @('--ffprobe', $FfprobePath, '--probe-media') }
    & dotnet @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "验收快照失败，退出码 $LASTEXITCODE" }
    return Get-Content -Raw -LiteralPath $Output | ConvertFrom-Json
}

if ($DurationMinutes -le 0) { throw 'DurationMinutes 必须大于 0。' }
if ($PollSeconds -lt 2 -or $PollSeconds -gt 300) { throw 'PollSeconds 必须介于 2 和 300 秒。' }
if ($Mode -eq 'EightCamera' -and $ExpectedCameraCount -eq 0) { $ExpectedCameraCount = 8 }
if ($Mode -eq 'SixteenCamera' -and $ExpectedCameraCount -eq 0) { $ExpectedCameraCount = 16 }
if ($Mode -eq 'HundredPackage' -and $ExpectedPackages -eq 0) { $ExpectedPackages = 100 }

$settings = $null
if (Test-Path -LiteralPath $SettingsPath) {
    try { $settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json } catch { throw "设置文件无法解析：$($_.Exception.Message)" }
}
if ($settings) {
    $excelSetting = Get-OptionalProperty -InputObject $settings -Name 'excelWorkbookPath'
    $storagePoolSetting = Get-OptionalProperty -InputObject $settings -Name 'storagePool'
    $recordingRootSetting = Get-OptionalProperty -InputObject $settings -Name 'recordingRoot'
    if ([string]::IsNullOrWhiteSpace($WorkbookPath) -and $excelSetting) { $WorkbookPath = [string]$excelSetting }
    if (@($RecordingRoot).Count -eq 0) {
        $roots = @()
        $targets = Get-OptionalProperty -InputObject $storagePoolSetting -Name 'targets'
        if ($targets) {
            $roots = @($targets | Where-Object {
                    (Get-OptionalProperty -InputObject $_ -Name 'enabled') -and
                    (Get-OptionalProperty -InputObject $_ -Name 'rootPath')
                } | ForEach-Object { [string](Get-OptionalProperty -InputObject $_ -Name 'rootPath') })
        }
        if (@($roots).Count -eq 0 -and $recordingRootSetting) { $roots = @([string]$recordingRootSetting) }
        $RecordingRoot = $roots
    }
}

if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) { throw "找不到 SQLite 数据库：$DatabasePath" }
if (-not [string]::IsNullOrWhiteSpace($WorkbookPath) -and -not (Test-Path -LiteralPath $WorkbookPath -PathType Leaf)) {
    throw "找不到绑定的 Excel：$WorkbookPath"
}
foreach ($root in $RecordingRoot) {
    if ([string]::IsNullOrWhiteSpace($root)) { throw '录像根目录不能为空。' }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$acceptanceProject = Join-Path $scriptRoot 'acceptance\UnpackVision.Acceptance.csproj'
$acceptanceDll = Join-Path $scriptRoot 'acceptance\bin\Release\net10.0-windows\UnpackVision.Acceptance.dll'
& dotnet build $acceptanceProject -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $acceptanceDll)) { throw '无法生成只读验收分析器。' }

if ($ProbeMedia) {
    if ([string]::IsNullOrWhiteSpace($FfprobePath)) {
        $ffprobe = Get-Command 'ffprobe.exe' -ErrorAction SilentlyContinue
        if ($ffprobe) { $FfprobePath = $ffprobe.Source }
    }
    if ([string]::IsNullOrWhiteSpace($FfprobePath) -or -not (Test-Path -LiteralPath $FfprobePath -PathType Leaf)) {
        throw 'ProbeMedia 需要可用的 ffprobe.exe。'
    }
}

$sessionStart = [DateTimeOffset]::Now
$sessionId = '{0}-{1}' -f $sessionStart.ToString('yyyyMMdd-HHmmss'), $Mode
$sessionRoot = Join-Path $OutputRoot $sessionId
New-Item -ItemType Directory -Path $sessionRoot -Force | Out-Null
$resourceCsv = Join-Path $sessionRoot 'resources.csv'
$eventsPath = Join-Path $sessionRoot 'events.jsonl'
$privateStatePath = Join-Path $sessionRoot 'session-private.json'
$startSnapshotPath = Join-Path $sessionRoot 'start-snapshot.json'
$finalSnapshotPath = Join-Path $sessionRoot 'final-snapshot.json'
$reportPath = Join-Path $sessionRoot 'report.md'

$sourceCounts = [ordered]@{ windows = 0; ipc = 0; nvr = 0; mobile = 0; other = 0 }
$configuredCameraCount = 0
$cameraRigSetting = Get-OptionalProperty -InputObject $settings -Name 'cameraRig'
$cameraSettings = Get-OptionalProperty -InputObject $cameraRigSetting -Name 'cameras'
if ($cameraSettings) {
    $enabledCameras = @($cameraSettings | Where-Object { Get-OptionalProperty -InputObject $_ -Name 'enabled' })
    $rigMode = [string](Get-OptionalProperty -InputObject $cameraRigSetting -Name 'mode')
    if ($rigMode -eq '0' -or $rigMode -eq 'SingleCamera') {
        $enabledCameras = @($enabledCameras | Where-Object { Get-OptionalProperty -InputObject $_ -Name 'isPrimary' } | Select-Object -First 1)
    }
    $configuredCameraCount = @($enabledCameras).Count
    foreach ($camera in $enabledCameras) {
        $source = ([string](Get-OptionalProperty -InputObject $camera -Name 'sourceType')).ToLowerInvariant()
        if ($source -match '^(0|1)$|windows|local|usb') { $sourceCounts.windows++ }
        elseif ($source -match '^3$|hikvision') { $sourceCounts.nvr++ }
        elseif ($source -match '^2$|network|rtsp|ipc') { $sourceCounts.ipc++ }
        elseif ($source -match 'mobile|android') { $sourceCounts.mobile++ }
        else { $sourceCounts.other++ }
    }
}

# This private state is required to reproduce a local run and can contain local paths.
# The shareable JSON/Markdown summaries below deliberately omit those paths.
[ordered]@{
    sessionId = $sessionId
    startedAt = $sessionStart.ToString('O')
    mode = $Mode
    databasePath = $DatabasePath
    workbookPath = $WorkbookPath
    recordingRoots = $RecordingRoot
    expectedCameraCount = $ExpectedCameraCount
    expectedPackages = $ExpectedPackages
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $privateStatePath -Encoding UTF8

$endTarget = $sessionStart.AddMinutes($DurationMinutes)
$cancelled = $false
$previousProcessCount = @(Get-DesktopProcesses).Count
$previousOnlineRoots = @{}
$lastCheckpoint = [DateTimeOffset]::MinValue
Write-Event -Type 'session.started' -Message "模式 $Mode，计划 $([Math]::Round($DurationMinutes, 2)) 分钟。" -Path $eventsPath
$startSnapshot = Invoke-AcceptanceSnapshot -Output $startSnapshotPath -Since $sessionStart -Until ([DateTimeOffset]::Now) -IncludeMediaProbe $false

try {
    while ([DateTimeOffset]::Now -lt $endTarget) {
        $now = [DateTimeOffset]::Now
        $elapsedSeconds = ($now - $sessionStart).TotalSeconds
        $totalSeconds = ($endTarget - $sessionStart).TotalSeconds
        $percent = [Math]::Min(99, [Math]::Floor($elapsedSeconds * 100 / $totalSeconds))
        $remaining = $endTarget - $now
        Write-Progress -Activity "拆包智录稳定版验收：$Mode" -Status ("已运行 {0:hh\:mm\:ss}，剩余 {1:hh\:mm\:ss}" -f ($now - $sessionStart), $remaining) -PercentComplete $percent

        $sample = Get-ResourceSample -Roots $RecordingRoot
        Write-ResourceCsv -Sample $sample -Path $resourceCsv
        if ($sample.desktopProcessCount -ne $previousProcessCount) {
            Write-Event -Type 'desktop.process-count-changed' -Message "桌面进程数量从 $previousProcessCount 变为 $($sample.desktopProcessCount)。" -Path $eventsPath
            $previousProcessCount = $sample.desktopProcessCount
            if ($StopWhenDesktopExits -and $sample.desktopProcessCount -eq 0) { throw '桌面程序已退出，按参数要求停止验收。' }
        }
        foreach ($disk in $sample.disks) {
            $key = [string]$disk.rootId
            if ($previousOnlineRoots.ContainsKey($key) -and $previousOnlineRoots[$key] -ne $disk.online) {
                Write-Event -Type 'storage.online-changed' -Message "盘位 $key 在线状态变为 $($disk.online)。" -Path $eventsPath
            }
            $previousOnlineRoots[$key] = $disk.online
        }

        if (($now - $lastCheckpoint).TotalSeconds -ge 60) {
            $checkpointPath = Join-Path $sessionRoot 'checkpoint.json'
            $null = Invoke-AcceptanceSnapshot -Output $checkpointPath -Since $sessionStart -Until $now -IncludeMediaProbe $false
            $lastCheckpoint = $now
        }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds, [Math]::Max(1, [int]($endTarget - [DateTimeOffset]::Now).TotalSeconds)))
    }
}
catch [System.Management.Automation.PipelineStoppedException] {
    $cancelled = $true
    Write-Event -Type 'session.cancelled' -Message '操作者中止了验收。' -Path $eventsPath
}
catch {
    $cancelled = $true
    Write-Event -Type 'session.failed' -Message $_.Exception.Message -Path $eventsPath
}
finally {
    Write-Progress -Activity "拆包智录稳定版验收：$Mode" -Completed
}

$sessionEnd = [DateTimeOffset]::Now
$final = Invoke-AcceptanceSnapshot -Output $finalSnapshotPath -Since $sessionStart -Until $sessionEnd -IncludeMediaProbe ([bool]$ProbeMedia)
$samples = @()
if (Test-Path -LiteralPath $resourceCsv) { $samples = @(Import-Csv -LiteralPath $resourceCsv) }
$cpuP95 = Get-Percentile -Values @($samples | Where-Object { $_.cpuPercent -ne '' } | ForEach-Object { [double]$_.cpuPercent }) -Percentile 0.95
$gpuP95 = Get-Percentile -Values @($samples | Where-Object { $_.gpuPercent -ne '' } | ForEach-Object { [double]$_.gpuPercent }) -Percentile 0.95
$memoryMinimum = $null
$memoryValues = @($samples | Where-Object { $_.freeMemoryMb -ne '' } | ForEach-Object { [double]$_.freeMemoryMb })
if (@($memoryValues).Count -gt 0) { $memoryMinimum = [Math]::Round(($memoryValues | Measure-Object -Minimum).Minimum, 1) }

$failures = New-Object System.Collections.Generic.List[string]
$pending = New-Object System.Collections.Generic.List[string]
if ($cancelled) { $failures.Add('验收未运行到计划结束时间。') }
if (-not $final.databaseReadable) { $failures.Add('SQLite 快照不可读。') }
if ($ExpectedCameraCount -gt 0 -and $configuredCameraCount -lt $ExpectedCameraCount) { $failures.Add("仅配置 $configuredCameraCount 路，少于预期 $ExpectedCameraCount 路。") }
if ($ExpectedPackages -gt 0 -and $final.completedRecords -lt $ExpectedPackages) { $failures.Add("完成记录 $($final.completedRecords) 条，少于预期 $ExpectedPackages 条。") }
if ($final.failedRecords -gt 0) { $failures.Add("窗口内有 $($final.failedRecords) 条失败记录。") }
if ($final.missingMediaFiles -gt 0 -or $final.zeroByteMediaFiles -gt 0) { $failures.Add('存在缺失或零字节录像。') }
if ($final.recordsMissingExpectedCameraAssets -gt 0) { $failures.Add("有 $($final.recordsMissingExpectedCameraAssets) 条记录缺少预期机位原片。") }
if ($final.outsideTrustedRootFiles -gt 0) { $failures.Add('存在不在已配置录像根目录下的媒体。') }
if ($final.unrecoveredMediaGaps -gt 0) { $failures.Add("存在 $($final.unrecoveredMediaGaps) 个未恢复媒体缺口。") }
if (-not [string]::IsNullOrWhiteSpace($WorkbookPath)) {
    if (-not $final.workbookReadable) { $failures.Add('Excel 工作簿无法只读检查。') }
    if ($final.completedRecordsMissingExcelMarker -gt 0) { $failures.Add("有 $($final.completedRecordsMissingExcelMarker) 条完成记录缺少 Excel 同步标记。") }
} else { $pending.Add('没有绑定 Excel，本轮不能完成 SQLite/Excel 对账。') }
if ($ProbeMedia) {
    if ($final.probeFailures -gt 0) { $failures.Add("有 $($final.probeFailures) 个视频无法由 ffprobe 读取。") }
    if ($final.mediaBelowNinetyPercentTargetFps -gt 0) { $failures.Add("有 $($final.mediaBelowNinetyPercentTargetFps) 个视频平均帧率低于目标 90%。") }
    if ($ExpectedCameraCount -ge 8 -and $final.maximumAssetDurationDriftMilliseconds -gt 100) { $failures.Add("同单机位文件最大时长差 $($final.maximumAssetDurationDriftMilliseconds)ms，超过 100ms。") }
} else { $pending.Add('未启用 ffprobe，视频可播放性、平均帧率和时长漂移未自动核验。') }
if ($null -eq $gpuP95) { $pending.Add('当前硬件没有可用的 NVIDIA GPU 指标；Intel/AMD GPU 余量需在性能测试窗口另行留证。') }
$pending.Add('运行期单路丢帧率和编码队列尚无外部只读接口，必须保存软件内 60 秒性能测试结果作为补充证据。')
if ($Mode -eq 'StoragePool') { $pending.Add('脚本不会拔盘、填满或写坏真实硬盘；拔盘/换盘/只读/空间阈值动作需由操作者在备用盘上执行。') }
if ($Mode -eq 'LowEnd') { $pending.Add('当前电脑的结果不能替代第二台真实低配电脑验收。') }

$result = if ($failures.Count -gt 0) { '未通过' } elseif ($pending.Count -gt 0) { '待补证' } else { '通过' }
$sourceText = "Windows/USB=$($sourceCounts.windows)，IPC/RTSP=$($sourceCounts.ipc)，海康NVR=$($sourceCounts.nvr)，手机=$($sourceCounts.mobile)，其他=$($sourceCounts.other)"
$report = @"
# 拆包智录稳定性验收报告

- 会话：$sessionId
- 模式：$Mode
- 结果：**$result**
- 实际时长：$([Math]::Round(($sessionEnd - $sessionStart).TotalMinutes, 2)) 分钟
- 配置机位：$configuredCameraCount 路（$sourceText）
- 预期机位：$ExpectedCameraCount 路
- 预期包裹：$ExpectedPackages 个

## 只读对账

- SQLite 完成记录：$($final.completedRecords)
- 唯一单号数：$($final.uniqueTrackingNumbers)
- 独立机位原片：$($final.independentMediaAssets)
- 合成视频：$($final.compositeMediaAssets)
- 缺失/零字节文件：$($final.missingMediaFiles) / $($final.zeroByteMediaFiles)
- Partial/Failed 资产：$($final.partialMediaAssets) / $($final.failedMediaAssets)
- 媒体缺口/未恢复：$($final.mediaGaps) / $($final.unrecoveredMediaGaps)
- Excel 可读：$($final.workbookReadable)
- Excel 同步标记：$($final.excelMarkers)
- 完成记录缺少 Excel 标记：$($final.completedRecordsMissingExcelMarker)
- 同步成功/等待/失败：$($final.syncSucceeded) / $($final.syncPending) / $($final.syncFailed)

## 媒体检查

- ffprobe 已检查：$($final.probedMediaFiles)
- ffprobe 失败：$($final.probeFailures)
- 平均帧率低于目标 90%：$($final.mediaBelowNinetyPercentTargetFps)
- 同单最大文件时长差：$($final.maximumAssetDurationDriftMilliseconds) ms

## 资源

- CPU P95：$cpuP95 %
- GPU P95：$gpuP95 %
- 最低空闲内存：$memoryMinimum MB
- 采样数：$(@($samples).Count)

## 未通过项

$((@($failures | ForEach-Object { "- $_" }) -join "`n"))

## 待补证项

$((@($pending | ForEach-Object { "- $_" }) -join "`n"))

## 安全说明

本脚本只读 SQLite、工作簿和录像元数据，不生成扫码、不写 Excel、不删除录像、不填满硬盘，也不输出单号、设备密码、流地址或完整业务路径。`session-private.json` 含本机路径，仅用于本机复现，不应上传或分享。
"@
$report | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Event -Type 'session.completed' -Message "验收结果：$result。" -Path $eventsPath

Write-Host ""
Write-Host "验收已结束：$result" -ForegroundColor $(if ($result -eq '通过') { 'Green' } elseif ($result -eq '待补证') { 'Yellow' } else { 'Red' })
Write-Host "报告：$reportPath"
Write-Host "本机私有状态：$privateStatePath"
