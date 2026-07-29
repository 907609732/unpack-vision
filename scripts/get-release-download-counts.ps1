[CmdletBinding()]
param(
    [string]$Repository = '907609732/unpack-vision'
)

$ErrorActionPreference = 'Stop'
$headers = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'EcommerceUnpackRecorder-ReleaseMetrics'
}
$releases = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/$Repository/releases?per_page=100" `
    -Headers $headers

$trackedNames = @(
    'EcommerceUnpackRecorder-win-Setup.exe',
    '电商拆包智能录像-Setup.exe',
    'EcommerceUnpackRecorder-Android.apk'
)
$rows = foreach ($release in $releases) {
    foreach ($asset in $release.assets) {
        if ($trackedNames -contains $asset.name -or $asset.name -like '*.nupkg') {
            [pscustomobject]@{
                Version = $release.tag_name
                PublishedAt = $release.published_at
                Prerelease = [bool]$release.prerelease
                Asset = $asset.name
                Downloads = [int64]$asset.download_count
            }
        }
    }
}

$rows | Sort-Object PublishedAt, Asset -Descending | Format-Table -AutoSize
$total = ($rows | Measure-Object Downloads -Sum).Sum
Write-Host "累计资源下载次数（不等于用户数或活跃人数）：$total"
