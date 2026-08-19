[CmdletBinding()]
param(
    [string]$Destination,
    [string]$CacheDirectory,
    [string]$GStreamerSourceRoot,
    [string]$FfmpegPath,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$gstreamerVersion = '1.28.5'
$ffmpegVersion = '8.1.2'
$installerName = "gstreamer-1.0-msvc-x86_64-$gstreamerVersion.exe"
$installerUrl = "https://gstreamer.freedesktop.org/data/pkg/windows/$gstreamerVersion/msvc/$installerName"
$installerSha256 = '51ee5eaec33008e8409d8cf6f6884457f22aa3bd515f8856f993a3eaab903530'

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repositoryRoot "tools\gstreamer\$gstreamerVersion"
}
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) {
    $CacheDirectory = Join-Path $artifactRoot 'cache\media-runtimes'
}
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$cacheRoot = [IO.Path]::GetFullPath($CacheDirectory)

function Assert-SafeBuildPath([string]$Path, [string]$Purpose) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedRoots = @(
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')),
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'tools'))
    )
    if (-not ($allowedRoots | Where-Object {
        $fullPath.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    })) {
        throw "$Purpose must be below this repository's artifacts or tools directory: $fullPath"
    }
    return $fullPath
}

function Get-ChildRelativePath([string]$Root, [string]$Path) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the audited runtime root: $fullPath"
    }
    return $fullPath.Substring($fullRoot.Length)
}

$destinationRoot = Assert-SafeBuildPath $destinationRoot 'Runtime staging destination'
$cacheRoot = Assert-SafeBuildPath $cacheRoot 'Runtime download cache'
$manifestPath = Join-Path $destinationRoot 'runtime-manifest.json'
if ((Test-Path -LiteralPath $manifestPath) -and -not $Rebuild) {
    & (Join-Path $PSScriptRoot 'verify-media-runtime-package.ps1') -RuntimeRoot $destinationRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Reusing verified media runtime stage: $destinationRoot"
    exit 0
}
if ((Test-Path -LiteralPath $destinationRoot) -and -not $Rebuild) {
    throw "Runtime destination exists without a valid manifest. Inspect it or rerun with -Rebuild: $destinationRoot"
}

$workRoot = Join-Path $artifactRoot "temp\media-runtime-$([Guid]::NewGuid().ToString('N'))"
$stageRoot = Join-Path $workRoot 'stage'
$temporaryInstallRoot = Join-Path $workRoot 'gstreamer-source'
$installedForStaging = $false
$oldPath = $env:PATH
$oldPluginPath = $env:GST_PLUGIN_PATH_1_0
$oldSystemPluginPath = $env:GST_PLUGIN_SYSTEM_PATH_1_0
$oldScanner = $env:GST_PLUGIN_SCANNER
$oldRegistry = $env:GST_REGISTRY

# The exact allowlist is deliberately narrower than the official full installer.
# Any new transitive DLL requires an explicit license review and a source change.
$approvedBinaryPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    'bin\bz2.dll', 'bin\cairo-2.dll', 'bin\ffi-7.dll', 'bin\fontconfig-1.dll',
    'bin\freetype-6.dll', 'bin\fribidi-0.dll', 'bin\gio-2.0-0.dll',
    'bin\glib-2.0-0.dll', 'bin\gmodule-2.0-0.dll', 'bin\gobject-2.0-0.dll',
    'bin\gstapp-1.0-0.dll', 'bin\gstaudio-1.0-0.dll', 'bin\gstbase-1.0-0.dll',
    'bin\gstcodecparsers-1.0-0.dll', 'bin\gstcodecs-1.0-0.dll',
    'bin\gstcuda-1.0-0.dll', 'bin\gstd3d11-1.0-0.dll', 'bin\gstd3d12-1.0-0.dll',
    'bin\gstd3dshader-1.0-0.dll', 'bin\gstgl-1.0-0.dll',
    'bin\gst-inspect-1.0.exe', 'bin\gst-launch-1.0.exe', 'bin\gstnet-1.0-0.dll',
    'bin\gstpbutils-1.0-0.dll', 'bin\gstreamer-1.0-0.dll', 'bin\gstriff-1.0-0.dll',
    'bin\gstrtp-1.0-0.dll', 'bin\gstrtsp-1.0-0.dll', 'bin\gstsdp-1.0-0.dll',
    'bin\gsttag-1.0-0.dll', 'bin\gstvideo-1.0-0.dll', 'bin\gstwinrt-1.0-0.dll',
    'bin\harfbuzz.dll', 'bin\intl-8.dll', 'bin\libexpat.dll', 'bin\openh264-7.dll',
    'bin\orc-0.4-0.dll', 'bin\pango-1.0-0.dll', 'bin\pangocairo-1.0-0.dll',
    'bin\pangoft2-1.0-0.dll', 'bin\pangowin32-1.0-0.dll', 'bin\pcre2-8-0.dll',
    'bin\pixman-1-0.dll', 'bin\png16.dll', 'bin\z-1.dll',
    'lib\gstreamer-1.0\gstamfcodec.dll', 'lib\gstreamer-1.0\gstapp.dll',
    'lib\gstreamer-1.0\gstcoreelements.dll', 'lib\gstreamer-1.0\gstisomp4.dll',
    'lib\gstreamer-1.0\gstlegacyrawparse.dll',
    'lib\gstreamer-1.0\gstmediafoundation.dll', 'lib\gstreamer-1.0\gstnvcodec.dll',
    'lib\gstreamer-1.0\gstopenh264.dll', 'lib\gstreamer-1.0\gstpango.dll',
    'lib\gstreamer-1.0\gstqsv.dll', 'lib\gstreamer-1.0\gstrawparse.dll',
    'lib\gstreamer-1.0\gstrtsp.dll', 'lib\gstreamer-1.0\gstvideoconvertscale.dll',
    'lib\gstreamer-1.0\gstvideoparsersbad.dll', 'lib\gstreamer-1.0\gstvideotestsrc.dll',
    'libexec\gstreamer-1.0\gst-plugin-scanner.exe'
) | ForEach-Object { [void]$approvedBinaryPaths.Add($_) }

$pluginPolicy = @(
    @{ Name = 'app'; Required = $true },
    @{ Name = 'coreelements'; Required = $true },
    @{ Name = 'isomp4'; Required = $true },
    @{ Name = 'legacyrawparse'; Required = $true },
    @{ Name = 'mediafoundation'; Required = $true },
    @{ Name = 'openh264'; Required = $true },
    @{ Name = 'pango'; Required = $true },
    @{ Name = 'rawparse'; Required = $true },
    @{ Name = 'rtsp'; Required = $true },
    @{ Name = 'videoconvertscale'; Required = $true },
    @{ Name = 'videoparsersbad'; Required = $true },
    @{ Name = 'videotestsrc'; Required = $true },
    @{ Name = 'qsv'; Required = $false },
    @{ Name = 'nvcodec'; Required = $false },
    @{ Name = 'amfcodec'; Required = $false }
)
$allowedPluginLicenses = @('LGPL', 'LGPL-2.0', 'LGPL-2.0+', 'LGPL-2.1', 'LGPL-2.1+', 'BSD', 'MIT', 'MPL-2.0')

function Get-GStreamerVersion([string]$Root) {
    $inspect = Join-Path $Root 'bin\gst-inspect-1.0.exe'
    if (-not (Test-Path -LiteralPath $inspect)) { return $null }
    $output = & $inspect --version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { return $null }
    $match = [regex]::Match($output, '(?im)^GStreamer\s+(?<version>\d+\.\d+\.\d+)')
    if ($match.Success) { return $match.Groups['version'].Value }
    return $null
}

function Find-Dumpbin {
    $command = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $visualStudioRoots = @(
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }
    foreach ($root in $visualStudioRoots) {
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter dumpbin.exe -File -ErrorAction SilentlyContinue |
            Where-Object FullName -Match '\\bin\\Hostx64\\x64\\dumpbin\.exe$' |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) { return $candidate.FullName }
    }
    throw 'dumpbin.exe was not found. Install the Visual Studio C++ build tools before staging the runtime.'
}

function Get-PluginMetadata([string]$SourceRoot, [string]$PluginName, [bool]$Required) {
    $inspect = Join-Path $SourceRoot 'bin\gst-inspect-1.0.exe'
    $output = & $inspect --plugin $PluginName 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        if ($Required) { throw "Required GStreamer plugin is unavailable: $PluginName" }
        return $null
    }
    $fileMatch = [regex]::Match($output, '(?im)^\s*Filename\s+(?<value>.+?)\s*$')
    $licenseMatch = [regex]::Match($output, '(?im)^\s*License\s+(?<value>.+?)\s*$')
    if (-not $fileMatch.Success -or -not $licenseMatch.Success) {
        throw "Could not read filename/license metadata for GStreamer plugin: $PluginName"
    }
    $license = $licenseMatch.Groups['value'].Value.Trim()
    if ($allowedPluginLicenses -notcontains $license -or
        $license -match '(?i)(^|[^A-Z])GPL(?:[^A-Z]|$)|nonfree|proprietary') {
        throw "GStreamer plugin '$PluginName' uses a non-whitelisted license: $license"
    }
    $file = [IO.Path]::GetFullPath($fileMatch.Groups['value'].Value.Trim())
    $pluginRoot = [IO.Path]::GetFullPath((Join-Path $SourceRoot 'lib\gstreamer-1.0'))
    if (-not $file.StartsWith($pluginRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "GStreamer plugin resolved outside the expected runtime root: $file"
    }
    return [pscustomobject]@{ Name = $PluginName; License = $license; File = $file }
}

function Get-DependencyClosure([string]$SourceRoot, [string[]]$RootFiles) {
    $dumpbin = Find-Dumpbin
    $queue = [Collections.Generic.Queue[string]]::new()
    $RootFiles | ForEach-Object { $queue.Enqueue([IO.Path]::GetFullPath($_)) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($queue.Count -gt 0) {
        $file = $queue.Dequeue()
        if (-not $seen.Add($file)) { continue }
        if (-not (Test-Path -LiteralPath $file)) { throw "Runtime binary is missing: $file" }
        $relative = Get-ChildRelativePath $SourceRoot $file
        if (-not $approvedBinaryPaths.Contains($relative)) {
            throw "Runtime binary is not on the audited license allowlist: $relative"
        }
        $output = & $dumpbin /nologo /dependents $file 2>&1
        if ($LASTEXITCODE -ne 0) { throw "dumpbin failed while auditing dependencies for $file" }
        foreach ($line in $output) {
            if ($line -notmatch '^\s+([A-Za-z0-9_.+-]+\.dll)\s*$') { continue }
            $dependency = Join-Path $SourceRoot "bin\$($Matches[1])"
            if (Test-Path -LiteralPath $dependency) { $queue.Enqueue($dependency) }
        }
    }
    return @($seen)
}

New-Item -ItemType Directory -Path $workRoot, $cacheRoot -Force | Out-Null
try {
    $sourceRoot = $null
    if (-not [string]::IsNullOrWhiteSpace($GStreamerSourceRoot)) {
        $sourceRoot = [IO.Path]::GetFullPath($GStreamerSourceRoot)
    }
    else {
        $installer = Join-Path $cacheRoot $installerName
        if (-not (Test-Path -LiteralPath $installer)) {
            $partial = "$installer.partial-$([Guid]::NewGuid().ToString('N'))"
            try {
                Invoke-WebRequest -Uri $installerUrl -OutFile $partial
                Move-Item -LiteralPath $partial -Destination $installer
            }
            finally {
                Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            }
        }
        $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $installerSha256) {
            throw "GStreamer installer SHA256 mismatch. Expected $installerSha256, got $actualHash"
        }
        $install = Start-Process -FilePath $installer -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS',
            '/CURRENTUSER', '/TYPE=runtime', "/DIR=`"$temporaryInstallRoot`""
        )
        if ($install.ExitCode -ne 0) { throw "GStreamer installer failed with exit code $($install.ExitCode)." }
        $sourceRoot = $temporaryInstallRoot
        $installedForStaging = $true
    }

    if (-not (Test-Path -LiteralPath $sourceRoot)) { throw "GStreamer source root was not found: $sourceRoot" }
    # Prevent a machine-wide registry or plugin path from satisfying the audit with files
    # outside the hash-pinned source tree.
    $env:PATH = "$(Join-Path $sourceRoot 'bin');$oldPath"
    $env:GST_PLUGIN_PATH_1_0 = Join-Path $sourceRoot 'lib\gstreamer-1.0'
    $env:GST_PLUGIN_SYSTEM_PATH_1_0 = $env:GST_PLUGIN_PATH_1_0
    $env:GST_PLUGIN_SCANNER = Join-Path $sourceRoot 'libexec\gstreamer-1.0\gst-plugin-scanner.exe'
    $env:GST_REGISTRY = Join-Path $workRoot 'source-registry.bin'
    $actualVersion = Get-GStreamerVersion $sourceRoot
    if ($actualVersion -ne $gstreamerVersion) {
        throw "GStreamer source version must be $gstreamerVersion, found '$actualVersion'."
    }

    $plugins = @()
    foreach ($policy in $pluginPolicy) {
        $metadata = Get-PluginMetadata $sourceRoot $policy.Name $policy.Required
        if ($null -ne $metadata) { $plugins += $metadata }
    }
    $rootFiles = @(
        (Join-Path $sourceRoot 'bin\gst-inspect-1.0.exe'),
        (Join-Path $sourceRoot 'bin\gst-launch-1.0.exe'),
        (Join-Path $sourceRoot 'libexec\gstreamer-1.0\gst-plugin-scanner.exe')
    ) + @($plugins.File)
    $binaryFiles = Get-DependencyClosure $sourceRoot $rootFiles

    foreach ($sourceFile in $binaryFiles) {
        $relative = Get-ChildRelativePath $sourceRoot $sourceFile
        $target = Join-Path $stageRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile -Destination $target
    }
    foreach ($relativeDirectory in @('etc\fonts', 'share\fontconfig', 'share\glib-2.0\schemas', 'share\licenses')) {
        $sourceDirectory = Join-Path $sourceRoot $relativeDirectory
        if (Test-Path -LiteralPath $sourceDirectory) {
            $targetDirectory = Join-Path $stageRoot $relativeDirectory
            New-Item -ItemType Directory -Path (Split-Path -Parent $targetDirectory) -Force | Out-Null
            Copy-Item -LiteralPath $sourceDirectory -Destination $targetDirectory -Recurse
        }
    }

    $ffmpegManifest = $null
    if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
        $resolvedFfmpeg = [IO.Path]::GetFullPath($FfmpegPath)
        $ffmpegPolicy = & (Join-Path $PSScriptRoot 'test-ffmpeg-redistribution.ps1') `
            -FfmpegPath $resolvedFfmpeg `
            -PassThru
        $ffmpegTarget = Join-Path $stageRoot "ffmpeg\$ffmpegVersion\bin\ffmpeg.exe"
        New-Item -ItemType Directory -Path (Split-Path -Parent $ffmpegTarget) -Force | Out-Null
        Copy-Item -LiteralPath $resolvedFfmpeg -Destination $ffmpegTarget
        $ffmpegManifest = @{
            version = $ffmpegVersion
            path = "ffmpeg/$ffmpegVersion/bin/ffmpeg.exe"
            sha256 = $ffmpegPolicy.Sha256
            policy = $ffmpegPolicy.Policy
        }
    }

    $binaryManifest = foreach ($file in (Get-ChildItem -LiteralPath $stageRoot -Recurse -File | Sort-Object FullName)) {
        $relative = (Get-ChildRelativePath $stageRoot $file.FullName).Replace('\', '/')
        if ($file.Extension -in @('.exe', '.dll')) {
            @{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        gstreamer = [ordered]@{
            version = $gstreamerVersion
            installerUrl = $installerUrl
            installerSha256 = $installerSha256
            architecture = 'msvc_x86_64'
            plugins = @($plugins | ForEach-Object { @{ name = $_.Name; license = $_.License; file = [IO.Path]::GetFileName($_.File) } })
        }
        ffmpeg = $ffmpegManifest
        binaries = @($binaryManifest)
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stageRoot 'runtime-manifest.json') -Encoding UTF8
    & (Join-Path $PSScriptRoot 'verify-media-runtime-package.ps1') -RuntimeRoot $stageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Media runtime stage validation failed.' }

    if (Test-Path -LiteralPath $destinationRoot) {
        $destinationRoot = Assert-SafeBuildPath $destinationRoot 'Runtime staging destination'
        Remove-Item -LiteralPath $destinationRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationRoot) -Force | Out-Null
    Move-Item -LiteralPath $stageRoot -Destination $destinationRoot
    Write-Host "Verified media runtime stage: $destinationRoot"
}
finally {
    $env:PATH = $oldPath
    $env:GST_PLUGIN_PATH_1_0 = $oldPluginPath
    $env:GST_PLUGIN_SYSTEM_PATH_1_0 = $oldSystemPluginPath
    $env:GST_PLUGIN_SCANNER = $oldScanner
    $env:GST_REGISTRY = $oldRegistry
    if ($installedForStaging -and (Test-Path -LiteralPath (Join-Path $temporaryInstallRoot 'unins000.exe'))) {
        Start-Process -FilePath (Join-Path $temporaryInstallRoot 'unins000.exe') -Wait -WindowStyle Hidden -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
        ) | Out-Null
    }
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
