param(
    [string]$Device
)

$ErrorActionPreference = 'Stop'
$sdkRoot = if ($env:ANDROID_HOME) {
    $env:ANDROID_HOME
} else {
    Join-Path $env:LOCALAPPDATA 'Android\Sdk'
}
$adb = Join-Path $sdkRoot 'platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) {
    throw "未找到 ADB：$adb"
}

$connected = @(
    & $adb devices |
        Select-Object -Skip 1 |
        ForEach-Object { ($_ -split '\s+')[0] } |
        Where-Object { $_ }
)
if ($Device) {
    $connected = @($connected | Where-Object { $_ -eq $Device })
}
if ($connected.Count -eq 0) {
    throw '未检测到已授权的安卓设备。请连接 USB 并在手机上允许 USB 调试。'
}

foreach ($serial in $connected) {
    & $adb -s $serial reverse tcp:5271 tcp:5271
    if ($LASTEXITCODE -ne 0) {
        throw "设备 $serial 的 USB 直连通道配置失败。"
    }
    Write-Output "设备 $serial 已启用 USB 直连兜底（手机 127.0.0.1:5271 → 电脑 5271）。"
}
