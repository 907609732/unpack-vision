param(
    [Parameter(Mandatory = $true)]
    [string]$DatabaseId
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$template = Join-Path $root "wrangler.example.toml"
$target = Join-Path $root "wrangler.toml"
$content = Get-Content -LiteralPath $template -Raw -Encoding UTF8
$content = $content.Replace("00000000-0000-0000-0000-000000000000", $DatabaseId)
Set-Content -LiteralPath $target -Value $content -Encoding UTF8
Push-Location $root
try {
    npm.cmd install
    npx.cmd wrangler d1 execute unpack-vision-dau --remote --file schema.sql
    npx.cmd wrangler deploy
}
finally {
    Pop-Location
}
