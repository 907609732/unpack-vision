[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRootPath = [IO.Path]::GetFullPath($env:TEMP)
Get-ChildItem -LiteralPath $tempRootPath -Directory -Filter 'UnpackVision-SecurityTest-*' |
    ForEach-Object {
        $stalePath = [IO.Path]::GetFullPath($_.FullName)
        if (-not $stalePath.StartsWith($tempRootPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe stale temporary cleanup target: $stalePath"
        }
        Remove-Item -LiteralPath $stalePath -Recurse -Force
    }
$testRoot = Join-Path $env:TEMP ('UnpackVision-SecurityTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$stationBuild = Join-Path $repositoryRoot (
    'src\UnpackVision.StationHost\bin\Release\net10.0-windows')
$executable = Get-ChildItem -LiteralPath $stationBuild -Filter '*.exe' -File |
    Where-Object Name -ne 'createdump.exe' |
    Sort-Object Length -Descending |
    Select-Object -First 1
if ($null -eq $executable) {
    throw "StationHost Release executable was not found: $stationBuild"
}

$startInfo = [Diagnostics.ProcessStartInfo]::new($executable.FullName)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.Arguments = (@(
    "--Storage:DatabasePath=$testRoot\test.db",
    "--Storage:RecordingRoot=$testRoot\Videos",
    "--StationHost:SecurityDirectory=$testRoot\Security",
    '--StationHost:StationId=security-test'
) -join ' ')

$process = [Diagnostics.Process]::Start($startInfo)
try {
    $health = $null
    for ($attempt = 0; $attempt -lt 30 -and $null -eq $health; $attempt++) {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod `
                -Uri 'http://127.0.0.1:5271/api/v1/health' `
                -TimeoutSec 2
        }
        catch {
        }
    }
    if ($null -eq $health) {
        throw 'StationHost health check failed.'
    }
    $listeners = Get-NetTCPConnection -State Listen -OwningProcess $process.Id |
        Select-Object LocalAddress, LocalPort
    if ($listeners.LocalAddress -contains '0.0.0.0') {
        throw 'StationHost must never bind all network interfaces.'
    }
    $publicInterfaceAddresses = Get-NetConnectionProfile |
        Where-Object NetworkCategory -eq 'Public' |
        ForEach-Object {
            Get-NetIPAddress -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        } |
        Select-Object -ExpandProperty IPAddress
    foreach ($address in $publicInterfaceAddresses) {
        if ($listeners.LocalAddress -contains $address) {
            throw "StationHost bound a public-profile address: $address"
        }
    }
    $lanListener = $listeners |
        Where-Object LocalPort -eq 5273 |
        Select-Object -First 1
    if ($null -eq $lanListener) {
        throw 'StationHost did not bind a private HTTPS listener.'
    }
    $pairing = Invoke-RestMethod `
        -Uri (
            'http://127.0.0.1:5271/api/v1/pairing/sessions?address=' +
            [uri]::EscapeDataString($lanListener.LocalAddress)) `
        -Method Post `
        -TimeoutSec 3
    if ($pairing.stationAddress -ne "https://$($lanListener.LocalAddress):5273" -or
        $pairing.certificateFingerprint -notmatch '^[0-9a-f]{64}$') {
        throw 'Pairing descriptor did not contain the pinned HTTPS endpoint.'
    }
    $invalidHostStatus = & curl.exe `
        --silent `
        --noproxy '*' `
        --connect-timeout 3 `
        --output NUL `
        --write-out '%{http_code}' `
        --insecure `
        --header 'Host: attacker.example' `
        "https://$($lanListener.LocalAddress):5273/api/v1/health"
    if ($invalidHostStatus -ne '400') {
        throw "Invalid Host header was not rejected: $invalidHostStatus"
    }

    [pscustomobject]@{
        Health = $health.status
        Version = $health.version
        Tls = $health.tls
        ProcessId = $process.Id
        Listeners = ($listeners | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', '
        PairingAddress = $pairing.stationAddress
        InvalidHostStatus = $invalidHostStatus
    }
    Invoke-WebRequest `
        -Uri 'http://127.0.0.1:5271/internal/shutdown' `
        -Method Post `
        -UseBasicParsing `
        -TimeoutSec 3 |
        Out-Null
    $process.WaitForExit(5000) | Out-Null
}
catch {
    Write-Output $_.Exception.ToString()
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
    Write-Output $process.StandardError.ReadToEnd()
    Write-Output $process.StandardOutput.ReadToEnd()
    throw
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe temporary cleanup target: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
