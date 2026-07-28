[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$signingRoot = Join-Path $env:LOCALAPPDATA 'UnpackVision\Signing'
$keystorePath = Join-Path $signingRoot 'ecommerce-unpack-recorder-release.jks'
$credentialPath = Join-Path $signingRoot 'android-signing.protected.json'
$keytool = 'C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe'
$alias = 'ecommerce-unpack-recorder'

function Get-AndroidSigningPassword([string]$path) {
    $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $protected = [Convert]::FromBase64String($json.protectedPassword)
    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        $protected,
        $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try {
        return [Text.Encoding]::UTF8.GetString($plain)
    }
    finally {
        [Array]::Clear($plain, 0, $plain.Length)
    }
}

if (-not (Test-Path -LiteralPath $keytool)) {
    throw "Android Studio keytool was not found at $keytool"
}
New-Item -ItemType Directory -Force -Path $signingRoot | Out-Null

if ((Test-Path -LiteralPath $keystorePath) -xor (Test-Path -LiteralPath $credentialPath)) {
    throw "The signing key or its DPAPI credential is missing. Do not overwrite the remaining file."
}

if (-not (Test-Path -LiteralPath $keystorePath)) {
    $random = [byte[]]::new(36)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($random)
    }
    finally {
        $generator.Dispose()
    }
    $password = [Convert]::ToBase64String($random).TrimEnd('=').Replace('+', 'A').Replace('/', 'B')
    & $keytool -genkeypair `
        -keystore $keystorePath `
        -storepass $password `
        -keypass $password `
        -alias $alias `
        -keyalg RSA `
        -keysize 4096 `
        -validity 36500 `
        -dname 'CN=Wucheng, OU=Open Source, O=Ecommerce Unpack Recorder, C=CN'
    if ($LASTEXITCODE -ne 0) { throw "keytool failed with exit code $LASTEXITCODE" }

    $plainBytes = [Text.Encoding]::UTF8.GetBytes($password)
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $plainBytes,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        @{
            keyAlias = $alias
            protectedPassword = [Convert]::ToBase64String($protected)
            createdAt = [DateTimeOffset]::UtcNow.ToString('o')
            certificateSha256 = ''
        } | ConvertTo-Json | Set-Content -LiteralPath $credentialPath -Encoding UTF8
    }
    finally {
        [Array]::Clear($plainBytes, 0, $plainBytes.Length)
        $password = $null
    }
}

Write-Host "Android release signing is ready: $keystorePath"
