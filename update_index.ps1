# ===== mods.json 索引更新脚本 =====
# 用法：发布某个 mod 的新 Release 后，在索引仓库目录运行：
#   .\update_index.ps1 -ModId StudentAgeEditorPlus
# 脚本会查询该 mod 仓库的 latest release，更新 mods.json 里对应条目的 version/downloadUrl。
# 不带参数则更新所有 mod 条目。

param(
    [string]$ModId = "",
    [string]$IndexFile = "mods.json"
)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ErrorActionPreference = "Stop"

if (-not (Test-Path $IndexFile)) {
    Write-Host "[X] 找不到 $IndexFile" -ForegroundColor Red
    exit 1
}

$index = Get-Content $IndexFile -Raw -Encoding UTF8 | ConvertFrom-Json
$updated = 0

foreach ($mod in $index.mods) {
    if ($ModId -and $mod.id -ne $ModId) { continue }
    if (-not $mod.repo) { continue }

    Write-Host "查询 $($mod.id) ($($mod.repo)) ..." -ForegroundColor Cyan
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$($mod.repo)/releases/latest" `
            -Headers @{ "User-Agent" = "index-updater" } -TimeoutSec 20
    } catch {
        Write-Host "  [!] 获取失败: $($_.Exception.Message)" -ForegroundColor Yellow
        continue
    }

    $tag = $release.tag_name
    # 找资产：优先与 assetType 匹配的第一个
    $ext = if ($mod.assetType -eq "zip") { ".zip" } else { ".dll" }
    $asset = $release.assets | Where-Object { $_.name -like "*$ext" } | Select-Object -First 1
    if (-not $asset) {
        Write-Host "  [!] Release $tag 中没有 $ext 资产，跳过" -ForegroundColor Yellow
        continue
    }

    if ($mod.version -ne $tag -or $mod.downloadUrl -ne $asset.browser_download_url) {
        Write-Host "  $($mod.version) -> $tag" -ForegroundColor Green
        $mod.version = $tag
        $mod.downloadUrl = $asset.browser_download_url
        $updated++
    } else {
        Write-Host "  已是最新 ($tag)" -ForegroundColor DarkGray
    }
}

if ($updated -gt 0) {
    $index.updatedAt = (Get-Date).ToString("yyyy-MM-dd")
    # ConvertTo-Json 默认深度够用；缩进 2 空格
    $json = $index | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText((Resolve-Path $IndexFile), $json, (New-Object Text.UTF8Encoding $false))
    Write-Host ""
    Write-Host "[OK] 已更新 $updated 个条目，记得 git commit + push" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "没有需要更新的条目。" -ForegroundColor DarkGray
}
