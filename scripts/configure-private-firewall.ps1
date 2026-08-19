[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '必须使用管理员权限配置 Windows 防火墙。'
}

$stationDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\StationHost'))
$stationHost = Get-ChildItem -LiteralPath $stationDirectory -Filter '*.exe' -File |
    Where-Object Name -ne 'createdump.exe' |
    Sort-Object Length -Descending |
    Select-Object -First 1
if ($null -eq $stationHost) {
    throw "未找到工位主机程序：$stationDirectory"
}

$rules = @(
    @{ Name = '拆包智录-HTTPS'; Protocol = 'TCP'; Port = '5273' },
    @{ Name = '拆包智录-RTSPS'; Protocol = 'TCP'; Port = '8555' },
    @{ Name = '拆包智录-WebRTC-TCP'; Protocol = 'TCP'; Port = '8889' },
    @{ Name = '拆包智录-WebRTC-UDP'; Protocol = 'UDP'; Port = '8189' }
)

foreach ($rule in $rules) {
    Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule
    New-NetFirewallRule `
        -DisplayName $rule.Name `
        -Direction Inbound `
        -Action Allow `
        -Profile Private `
        -Program $stationHost.FullName `
        -Protocol $rule.Protocol `
        -LocalPort $rule.Port `
        -Description '仅允许专用网络中的已配对手机访问拆包智录工位。' |
        Out-Null
}
