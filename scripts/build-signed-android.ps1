[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$signingRoot = Join-Path $env:LOCALAPPDATA 'UnpackVision\Signing'
$keystorePath = Join-Path $signingRoot 'ecommerce-unpack-recorder-release.jks'
$credentialPath = Join-Path $signingRoot 'android-signing.protected.json'

if (-not (Test-Path -LiteralPath $keystorePath) -or -not (Test-Path -LiteralPath $credentialPath)) {
    & (Join-Path $PSScriptRoot 'initialize-android-signing.ps1')
}

$json = Get-Content -LiteralPath $credentialPath -Raw -Encoding UTF8 | ConvertFrom-Json
$protected = [Convert]::FromBase64String($json.protectedPassword)
$plain = [Security.Cryptography.ProtectedData]::Unprotect(
    $protected,
    $null,
    [Security.Cryptography.DataProtectionScope]::CurrentUser)
try {
    $password = [Text.Encoding]::UTF8.GetString($plain)
    $env:ANDROID_KEYSTORE_PATH = $keystorePath
    $env:ANDROID_KEYSTORE_PASSWORD = $password
    $env:ANDROID_KEY_ALIAS = [string]$json.keyAlias
    $env:ANDROID_KEY_PASSWORD = $password
    & (Join-Path $PSScriptRoot 'build-android.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Signed Android build failed with exit code $LASTEXITCODE" }
}
finally {
    [Array]::Clear($plain, 0, $plain.Length)
    $password = $null
    Remove-Item Env:ANDROID_KEYSTORE_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
}

$apk = Join-Path $repositoryRoot 'mobile\UnpackVision.Android\app\build\outputs\apk\release\app-release.apk'
if (-not (Test-Path -LiteralPath $apk)) {
    throw "Signed APK was not created: $apk"
}
Write-Host "Signed Android APK: $apk"
