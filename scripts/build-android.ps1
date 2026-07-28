[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory ".."))
$androidRoot = Join-Path $repositoryRoot "mobile\UnpackVision.Android"
$studioJbr = "C:\Program Files\Android\Android Studio\jbr"
$sdkRoot = Join-Path $env:LOCALAPPDATA "Android\Sdk"

if (-not (Test-Path -LiteralPath (Join-Path $studioJbr "bin\java.exe"))) {
    throw "Android Studio JBR was not found at $studioJbr"
}
if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot "platform-tools\adb.exe"))) {
    throw "Android SDK was not found at $sdkRoot"
}

$env:JAVA_HOME = $studioJbr
$env:ANDROID_HOME = $sdkRoot
$taskSuffix = if ($Configuration -eq "Release") { "Release" } else { "Debug" }
$tasks = @("app:assemble$taskSuffix")
if (-not $SkipTests -and $Configuration -eq "Debug") {
    $tasks = @("app:testDebugUnitTest") + $tasks
}

# Gradle's Windows test worker can misread non-ASCII class paths, and Kotlin's
# incremental cache requires the same root on every build. Always use Z:.
$mappedRoot = "Z:"
$existingMapping = (& subst) | Where-Object { $_ -match '^Z:\\:\s*=>\s*(.+)$' } | Select-Object -First 1
$createdMapping = $false
try {
    if ($existingMapping) {
        if (-not (Test-Path -LiteralPath "Z:\UnpackVision.slnx")) {
            throw "Z: is already occupied by another location. Remove that mapping before building Android."
        }
    }
    else {
        & subst $mappedRoot $repositoryRoot
        if ($LASTEXITCODE -ne 0) { throw "Could not map $mappedRoot to $repositoryRoot" }
        $createdMapping = $true
    }
    $mappedAndroidRoot = "$mappedRoot\mobile\UnpackVision.Android"
    & "$mappedAndroidRoot\gradlew.bat" -p $mappedAndroidRoot @tasks --no-daemon --console=plain
    if ($LASTEXITCODE -ne 0) { throw "Android Gradle build failed with exit code $LASTEXITCODE" }
}
finally {
    if ($createdMapping) {
        & subst $mappedRoot /D 2>$null
    }
}

$apkDirectory = Join-Path $androidRoot "app\build\outputs\apk\$($Configuration.ToLowerInvariant())"
$apk = Get-ChildItem -LiteralPath $apkDirectory -Filter '*.apk' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -ne $apk) {
    Write-Host "Android APK: $($apk.FullName)"
}
