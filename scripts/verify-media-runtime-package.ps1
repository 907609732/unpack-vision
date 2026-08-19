[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RuntimeRoot)
$manifestPath = Join-Path $root 'runtime-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Runtime manifest is missing: $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw "Unsupported runtime manifest schema: $($manifest.schemaVersion)" }
if ($manifest.gstreamer.version -ne '1.28.5') { throw 'Bundled GStreamer must be version 1.28.5.' }
if ($manifest.gstreamer.installerUrl -ne 'https://gstreamer.freedesktop.org/data/pkg/windows/1.28.5/msvc/gstreamer-1.0-msvc-x86_64-1.28.5.exe') {
    throw 'The GStreamer installer URL is not the audited official URL.'
}
if ($manifest.gstreamer.installerSha256 -ne '51ee5eaec33008e8409d8cf6f6884457f22aa3bd515f8856f993a3eaab903530') {
    throw 'The GStreamer installer SHA256 is not the audited value.'
}

$forbiddenNames = '(?i)(^|[-_.])(x264|x265|avcodec|avdevice|avfilter|avformat|postproc|fdk-aac)([-_.]|$)'
$binaryEntries = @($manifest.binaries)
if ($binaryEntries.Count -eq 0) { throw 'Runtime manifest contains no audited binaries.' }
foreach ($entry in $binaryEntries) {
    $relative = ([string]$entry.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if ($relative -match $forbiddenNames) { throw "Forbidden GPL/nonfree binary name found: $relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime manifest path escapes the runtime root: $relative"
    }
    if (-not (Test-Path -LiteralPath $path)) { throw "Manifest binary is missing: $relative" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Runtime binary SHA256 mismatch: $relative" }
}

$inspect = Join-Path $root 'bin\gst-inspect-1.0.exe'
$launch = Join-Path $root 'bin\gst-launch-1.0.exe'
foreach ($required in @($inspect, $launch, (Join-Path $root 'libexec\gstreamer-1.0\gst-plugin-scanner.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required GStreamer executable is missing: $required" }
}

$oldPath = $env:PATH
$oldPluginPath = $env:GST_PLUGIN_PATH_1_0
$oldSystemPluginPath = $env:GST_PLUGIN_SYSTEM_PATH_1_0
$oldScanner = $env:GST_PLUGIN_SCANNER
$oldRegistry = $env:GST_REGISTRY
$registry = Join-Path ([IO.Path]::GetTempPath()) "UnpackVision-GStreamer-$([Guid]::NewGuid().ToString('N')).bin"
try {
    $env:PATH = "$(Join-Path $root 'bin');$oldPath"
    $env:GST_PLUGIN_PATH_1_0 = Join-Path $root 'lib\gstreamer-1.0'
    $env:GST_PLUGIN_SYSTEM_PATH_1_0 = $env:GST_PLUGIN_PATH_1_0
    $env:GST_PLUGIN_SCANNER = Join-Path $root 'libexec\gstreamer-1.0\gst-plugin-scanner.exe'
    $env:GST_REGISTRY = $registry
    $nativeErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $versionOutput = & $inspect --version 2>&1 | Out-String
    $versionExitCode = $LASTEXITCODE
    $ErrorActionPreference = $nativeErrorPreference
    if ($versionExitCode -ne 0 -or $versionOutput -notmatch '(?im)^GStreamer\s+1\.28\.5\b') {
        throw "Staged gst-inspect did not report GStreamer 1.28.5 (exit $versionExitCode): $($versionOutput.Trim())"
    }
    foreach ($element in @('mfvideosrc','rtspsrc','textoverlay','mp4mux','appsink','fdsrc','videoparse','queue','videoconvert','h264parse','filesink')) {
        & $inspect --exists $element 2>$null
        if ($LASTEXITCODE -ne 0) { throw "Staged GStreamer element is missing: $element" }
    }
    foreach ($plugin in @($manifest.gstreamer.plugins)) {
        $license = [string]$plugin.license
        if ($license -notin @('LGPL','LGPL-2.0','LGPL-2.0+','LGPL-2.1','LGPL-2.1+','BSD','MIT','MPL-2.0') -or
            $license -match '(?i)(^|[^A-Z])GPL(?:[^A-Z]|$)|nonfree|proprietary') {
            throw "Plugin license is not allowed: $($plugin.name) ($license)"
        }
        $nativeErrorPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $pluginOutput = & $inspect --plugin ([string]$plugin.name) 2>&1 | Out-String
        $pluginExitCode = $LASTEXITCODE
        $ErrorActionPreference = $nativeErrorPreference
        if ($pluginExitCode -ne 0 -or $pluginOutput -notmatch '(?im)^\s*License\s+') {
            throw "Manifest plugin could not be loaded: $($plugin.name)"
        }
    }

    $ffmpeg = Join-Path $root 'ffmpeg\8.1.2\bin\ffmpeg.exe'
    if ($null -ne $manifest.ffmpeg) {
        if (-not (Test-Path -LiteralPath $ffmpeg)) { throw 'Manifest declares FFmpeg but ffmpeg.exe is missing.' }
        $output = & $ffmpeg -hide_banner -version 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $output -notmatch '(?im)^ffmpeg version\s+(?:n-)?8\.1\.2\b') {
            throw 'Bundled FFmpeg did not report version 8.1.2.'
        }
        if ($output -match '(?i)--enable-gpl' -or $output -match '(?i)--enable-nonfree') {
            throw 'Bundled FFmpeg contains GPL or nonfree build flags.'
        }
    }
    elseif (Test-Path -LiteralPath $ffmpeg) {
        throw 'An unmanifested FFmpeg binary was found in the runtime stage.'
    }
}
finally {
    $env:PATH = $oldPath
    $env:GST_PLUGIN_PATH_1_0 = $oldPluginPath
    $env:GST_PLUGIN_SYSTEM_PATH_1_0 = $oldSystemPluginPath
    $env:GST_PLUGIN_SCANNER = $oldScanner
    $env:GST_REGISTRY = $oldRegistry
    Remove-Item -LiteralPath $registry -Force -ErrorAction SilentlyContinue
}

Write-Host "Media runtime package verified: $root"
