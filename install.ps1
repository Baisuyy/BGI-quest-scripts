# 安装 BetterGI 脚本：把 scripts/ 下所有脚本项目平铺复制到 <BetterGI>\User\JsScript
#
# 用法（在仓库根目录打开 PowerShell）：
#   .\install.ps1 -BgiRoot "D:\BetterGI"
#   .\install.ps1                       # 默认当前目录，适合把仓库放在 BetterGI 目录内
#
# 为什么需要平铺：BetterGI 的 JsListViewModel.LoadScriptFolder 只枚举 User\JsScript
# 的直接子目录，且每个子目录必须是含 manifest.json 的脚本项目。仓库按「篇章/幕」
# 分了层，直接整目录拷贝会因多一层而被当成无效项目。

param(
    [string]$BgiRoot = "."
)

$ErrorActionPreference = "Stop"

$scriptsDir = Join-Path $PSScriptRoot "scripts"
if (-not (Test-Path $scriptsDir)) {
    Write-Error "找不到 scripts 目录：$scriptsDir"
}

$target = Join-Path $BgiRoot "User\JsScript"
if (-not (Test-Path $target)) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Write-Host "已创建目录：$target"
}

$projects = Get-ChildItem -Path $scriptsDir -Recurse -Filter "manifest.json" -File | ForEach-Object { $_.Directory }
if ($projects.Count -eq 0) {
    Write-Error "scripts 下没有找到任何 manifest.json，请确认仓库完整。"
}

$installed = 0
foreach ($proj in $projects) {
    $dest = Join-Path $target $proj.Name
    if (Test-Path $dest) {
        Remove-Item $dest -Recurse -Force
    }
    Copy-Item $proj.FullName $dest -Recurse -Force
    Write-Host "  ✔ $($proj.Name)"
    $installed++
}

Write-Host ""
Write-Host "已安装 $installed 个脚本到 $target"
Write-Host "打开 BetterGI → 脚本 → 刷新，即可在列表中看到。"
Write-Host ""
Write-Host "路线文件（routes/）如需在「地图追踪」中单独使用，复制到："
Write-Host "  $(Join-Path $BgiRoot 'User\AutoPathing')"
Write-Host "该目录支持多级子目录，可保持篇章/幕的层级。"
