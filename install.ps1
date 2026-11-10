# 安装 BetterGI 脚本与路线
#
# 用法（在仓库根目录打开 PowerShell）：
#   .\install.ps1 -BgiRoot "D:\BetterGI"
#   .\install.ps1                       # 默认当前目录，适合把仓库放在 BetterGI 目录内
#   .\install.ps1 -BgiRoot "D:\BetterGI" -NoPathing    # 只装脚本，不装路线
#
# 做两件事：
#   1. 把 scripts/ 下每个脚本项目（含 manifest.json 的目录）平铺复制到 User\JsScript
#   2. 把每个项目的 pathing\*.json 复制到 User\AutoPathing\<篇章>\<幕>\<任务>\
#
# 为什么脚本要平铺：BetterGI 的 JsListViewModel.LoadScriptFolder 只枚举
# User\JsScript 的直接子目录，且每个子目录必须是含 manifest.json 的脚本项目。
# 仓库按「篇章/幕」分了层，整目录拷贝会因多一层而被当成无效项目。
# 路线不受此限：AutoPathing 是递归加载的，可以保留层级。

param(
    [string]$BgiRoot = ".",
    [switch]$NoPathing
)

$ErrorActionPreference = "Stop"

$scriptsDir = Join-Path $PSScriptRoot "scripts"
if (-not (Test-Path $scriptsDir)) {
    Write-Error "找不到 scripts 目录：$scriptsDir"
}

$jsTarget = Join-Path $BgiRoot "User\JsScript"
if (-not (Test-Path $jsTarget)) {
    New-Item -ItemType Directory -Path $jsTarget -Force | Out-Null
    Write-Host "已创建目录：$jsTarget"
}

$pathingRoot = Join-Path $BgiRoot "User\AutoPathing"
if (-not $NoPathing -and -not (Test-Path $pathingRoot)) {
    New-Item -ItemType Directory -Path $pathingRoot -Force | Out-Null
    Write-Host "已创建目录：$pathingRoot"
}

$projects = Get-ChildItem -Path $scriptsDir -Recurse -Filter "manifest.json" -File | ForEach-Object { $_.Directory }
if ($projects.Count -eq 0) {
    Write-Error "scripts 下没有找到任何 manifest.json，请确认仓库完整。"
}

$installed = 0
$copiedRoutes = 0
foreach ($proj in $projects) {
    $dest = Join-Path $jsTarget $proj.Name
    if (Test-Path $dest) {
        Remove-Item $dest -Recurse -Force
    }
    Copy-Item $proj.FullName $dest -Recurse -Force
    Write-Host "  ✔ 脚本 $($proj.Name)"
    $installed++

    # 全流程项目里的 pathing 是其它任务的聚合副本，不重复安装
    if ($NoPathing -or $proj.Name -like "全流程*") { continue }

    $pathingDir = Join-Path $proj.FullName "pathing"
    if (-not (Test-Path $pathingDir)) { continue }

    $rel = $proj.FullName.Substring($scriptsDir.Length).TrimStart("\", "/")
    $routeDest = Join-Path $pathingRoot $rel
    if (-not (Test-Path $routeDest)) {
        New-Item -ItemType Directory -Path $routeDest -Force | Out-Null
    }
    Copy-Item (Join-Path $pathingDir "*.json") $routeDest -Force
    $n = (Get-ChildItem -Path $pathingDir -Filter "*.json" -File).Count
    Write-Host "    路线 $rel ($n 段)"
    $copiedRoutes += $n
}

Write-Host ""
Write-Host "已安装 $installed 个脚本到 $jsTarget"
if (-not $NoPathing) {
    Write-Host "已复制 $copiedRoutes 段路线到 $pathingRoot"
}
Write-Host "打开 BetterGI → 脚本 → 刷新；路线在「地图追踪」里按篇章/幕浏览。"
