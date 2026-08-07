[CmdletBinding()]
param(
    [string]$Version = '2.3.2',
    [string]$AndroidApk,
    [switch]$SkipPublish,
    [switch]$RebuildExisting
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishFolder = "staging\$Version\App"
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\$publishFolder"
$velopackDirectory = Join-Path $repositoryRoot 'artifacts\velopack-release'
$releaseDirectory = Join-Path $repositoryRoot 'artifacts\release-output'
$toolDirectory = Join-Path $repositoryRoot 'artifacts\tools'
$iconPath = Join-Path $repositoryRoot 'src\UnpackVision.App\Assets\EcommerceUnpackRecorder.ico'
$releaseNotes = Join-Path $repositoryRoot "docs\releases\$Version.md"
$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion) -or
    $parsedVersion.Major -gt 2147 -or
    $parsedVersion.Minor -gt 99 -or
    $parsedVersion.Build -gt 99) {
    throw "Version must be numeric major.minor.patch with minor and patch below 100: $Version"
}
$versionCode = [int](
    $parsedVersion.Major * 10000 +
    $parsedVersion.Minor * 100 +
    [Math]::Max(0, $parsedVersion.Build))

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -AppFolder $publishFolder
    if ($LASTEXITCODE -ne 0) { throw "Windows publish failed with exit code $LASTEXITCODE" }
}

$mainExecutable = Get-ChildItem -LiteralPath $publishDirectory -Filter '*.exe' -File |
    Where-Object Name -ne 'createdump.exe' |
    Sort-Object Length -Descending |
    Select-Object -First 1
if ($null -eq $mainExecutable) {
    throw "The published desktop executable was not found in $publishDirectory"
}
$productName = [System.IO.Path]::GetFileNameWithoutExtension($mainExecutable.Name)

foreach ($required in @($iconPath, $releaseNotes)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required release input was not found: $required"
    }
}

New-Item -ItemType Directory -Force -Path $velopackDirectory, $releaseDirectory, $toolDirectory | Out-Null
if ($RebuildExisting) {
    $velopackRoot = [IO.Path]::GetFullPath($velopackDirectory)
    $rebuildNames = @(
        "EcommerceUnpackRecorder-$Version-full.nupkg",
        "EcommerceUnpackRecorder-$Version-delta.nupkg",
        'EcommerceUnpackRecorder-win-Setup.exe',
        'EcommerceUnpackRecorder-win-Portable.zip',
        'releases.win.json',
        'assets.win.json',
        'RELEASES'
    )
    foreach ($name in $rebuildNames) {
        $target = [IO.Path]::GetFullPath((Join-Path $velopackRoot $name))
        if (-not $target.StartsWith($velopackRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a Velopack file outside $velopackRoot"
        }
        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    }
}
$vpk = Join-Path $toolDirectory 'vpk.exe'
if (-not (Test-Path -LiteralPath $vpk)) {
    & dotnet tool install vpk --version 1.2.0 --tool-path $toolDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the pinned Velopack CLI.' }
}
else {
    & dotnet tool update vpk --version 1.2.0 --tool-path $toolDirectory | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not update the pinned Velopack CLI.' }
}

$existingFullPackage = Join-Path $velopackDirectory "EcommerceUnpackRecorder-$Version-full.nupkg"
$existingSetup = Join-Path $velopackDirectory 'EcommerceUnpackRecorder-win-Setup.exe'
if ((Test-Path -LiteralPath $existingFullPackage) -and (Test-Path -LiteralPath $existingSetup)) {
    Write-Host "Reusing existing Velopack $Version package."
}
else {
    & $vpk pack `
        --packId EcommerceUnpackRecorder `
        --packVersion $Version `
        --packDir $publishDirectory `
        --mainExe $mainExecutable.Name `
        --packTitle $productName `
        --packAuthors 'Wucheng' `
        --releaseNotes $releaseNotes `
        --icon $iconPath `
        --runtime win-x64 `
        --channel win `
        --delta BestSpeed `
        --shortcuts Desktop,StartMenuRoot `
        --instLocation PerUser `
        --outputDir $velopackDirectory
    if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed with exit code $LASTEXITCODE" }
}

Get-ChildItem -LiteralPath $releaseDirectory -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$releaseFiles = Get-ChildItem -LiteralPath $velopackDirectory -File |
    Where-Object {
        $_.Name -match [regex]::Escape($Version) -or
        $_.Name -match '^releases\.win\.json$' -or
        $_.Name -match 'Setup\.exe$'
    }
foreach ($file in $releaseFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination $releaseDirectory -Force
}

$setup = Get-ChildItem -LiteralPath $velopackDirectory -File -Filter '*Setup.exe' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $setup) { throw 'Velopack did not produce a Windows setup executable.' }
Copy-Item -LiteralPath $setup.FullName -Destination (Join-Path $releaseDirectory "$productName-Setup.exe") -Force

if (-not [string]::IsNullOrWhiteSpace($AndroidApk)) {
    $resolvedApk = [System.IO.Path]::GetFullPath($AndroidApk)
    if (-not (Test-Path -LiteralPath $resolvedApk)) {
        throw "Signed Android APK not found: $resolvedApk"
    }
    $apkDestination = Join-Path $releaseDirectory 'EcommerceUnpackRecorder-Android.apk'
    Copy-Item -LiteralPath $resolvedApk -Destination $apkDestination -Force
    $apkHash = (Get-FileHash -LiteralPath $apkDestination -Algorithm SHA256).Hash.ToLowerInvariant()
    $mobileManifest = @{
        versionName = $Version
        versionCode = $versionCode
        apkUrl = 'https://github.com/907609732/unpack-vision/releases/latest/download/EcommerceUnpackRecorder-Android.apk'
        sha256 = $apkHash
        minSdk = 26
        publishedAt = [DateTimeOffset]::UtcNow.ToString('o')
        notesUrl = "https://github.com/907609732/unpack-vision/releases/tag/v$Version"
        releaseNotesUrl = "https://github.com/907609732/unpack-vision/releases/tag/v$Version"
        minimumSupportedVersion = '2.1.0'
        critical = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $releaseDirectory 'mobile-update.json'),
        $mobileManifest,
        [Text.UTF8Encoding]::new($false))
}

$desktopManifest = @{
    version = $Version
    minimumSupportedVersion = '2.1.0'
    critical = $false
    releaseNotesUrl = "https://github.com/907609732/unpack-vision/releases/tag/v$Version"
    publishedAt = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json
[IO.File]::WriteAllText(
    (Join-Path $releaseDirectory 'desktop-update.json'),
    $desktopManifest,
    [Text.UTF8Encoding]::new($false))

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination $releaseDirectory -Force

$hashLines = Get-ChildItem -LiteralPath $releaseDirectory -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
$hashLines | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding UTF8

Write-Host "Release output: $releaseDirectory"
Get-ChildItem -LiteralPath $releaseDirectory -File | Select-Object Name, Length
