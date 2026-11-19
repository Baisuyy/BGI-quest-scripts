#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用上游快照覆盖当前源码，同时保留特别版自有文件。

用法：python3 tools/bgi-special/sync_source.py <上游目录>

行为：
1. 删除当前目录中「上游没有、且不在保留清单」的文件（含上游已废弃的源码）
2. 用上游文件覆盖当前目录（保留清单内的文件不覆盖）
3. 清理空目录

不处理 .git，也不做 Git 操作。
"""

import pathlib
import shutil
import sys

# 相对仓库根路径；以 / 结尾表示整个目录
KEEP = (
    ".github/workflows/build-special.yml",
    ".github/workflows/sync-upstream.yml",
    ".github/workflows/update-release-notes.yml",
    ".github/release-notes-template.md",
    ".bgi-upstream",
    "README-SPECIAL.md",
    "Docs/SPECIAL-API.md",
    "Docs/AI-内置决策.md",
    "Docs/上机测试-内置AI.md",
    "Docs/示例-读任务面板/",
    # 识图与脚本宿主
    "BetterGenshinImpact/Core/Script/Dependence/VisionHost.cs",
    "BetterGenshinImpact/Core/Script/Dependence/AiAgentHost.cs",
    # 内置大模型内核
    "BetterGenshinImpact/Core/Ai/",
    # 路径录制（特别版新增）
    "BetterGenshinImpact/Core/Pathing/",
    # 特别版更新检查（改走自己的 Release）
    "BetterGenshinImpact/Core/Special/",
    "BetterGenshinImpact/Core/Config/AiConfig.cs",
    # 跑图 AI 接管与设置卡片
    "BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.Ai.cs",
    "BetterGenshinImpact/View/Controls/AiSettingsCard.xaml",
    "BetterGenshinImpact/View/Controls/AiSettingsCard.xaml.cs",
    # AI 助手页（模型配置 + 本地助手）
    "BetterGenshinImpact/View/Pages/AiSettingsPage.xaml",
    "BetterGenshinImpact/View/Pages/AiSettingsPage.xaml.cs",
    "BetterGenshinImpact/ViewModel/Pages/AiSettingsPageViewModel.cs",
    # 游戏内助手小面板（热键唤起，下发任务/脚本）
    "BetterGenshinImpact/View/Windows/AgentPanelWindow.xaml",
    "BetterGenshinImpact/View/Windows/AgentPanelWindow.xaml.cs",
    # QQ 机器人 / 远程任务：上游没有的整套能力，删掉就编译不过
    "BetterGenshinImpact/Service/RemoteTask/",
    "BetterGenshinImpact/Core/Config/RemoteTaskConfig.cs",
    "BetterGenshinImpact/Core/Config/RemoteScheduleConfig.cs",
    # 米游社绑定与每日便笺
    "BetterGenshinImpact/Core/Mys/",
    "BetterGenshinImpact/View/Windows/MysLoginWindow.xaml",
    "BetterGenshinImpact/View/Windows/MysLoginWindow.xaml.cs",
    # 多账号切换
    "BetterGenshinImpact/GameTask/GenshinAccountSwitch/",
    # 路径进度（断点续跑）
    "BetterGenshinImpact/GameTask/AutoPathing/PathProgressStore.cs",
    "BetterGenshinImpact/GameTask/AutoPathing/AutoPathingPathTask.cs",
    # 日志链路：重复抑制与异常栈去重
    "BetterGenshinImpact/Helpers/RepeatSuppressorSink.cs",
    "BetterGenshinImpact/Helpers/ExceptionDedupTextFormatter.cs",
    # 地图点位图标渲染
    "BetterGenshinImpact/View/Controls/MaskMapPointIconRenderer.cs",
    # 特别版文档、发版说明与安装器配置
    "Docs/",
    "docs/",
    "AGENTS.md",
    "Build/kachina.special.config.json",
    ".github/workflows/upload-123pan.yml",
    "Test/BetterGenshinImpact.UnitTest/ServiceTests/",
    "tools/bgi-special/",
)


def kept(rel: str) -> bool:
    return any(rel == k or (k.endswith("/") and rel.startswith(k)) for k in KEEP)


# 只属于特别版、上游永远没有的目录。它们本来该在 KEEP 里，但 KEEP 是手写清单，
# 新功能加进来时很容易漏登记——2026-11-19 那次同步就把 Service/RemoteTask 整个删了，
# 代码在远端躺了一夜编译不过。这里当最后一道闸：真要删，先报错让人确认。
PROTECTED = (
    "BetterGenshinImpact/Service/RemoteTask/",
    "BetterGenshinImpact/Core/Ai/",
    "BetterGenshinImpact/Core/Special/",
    "BetterGenshinImpact/Core/Mys/",
    "BetterGenshinImpact/GameTask/GenshinAccountSwitch/",
)


def protected(rel: str) -> bool:
    return any(rel == p or (p.endswith("/") and rel.startswith(p)) for p in PROTECTED)


def files_of(root: pathlib.Path):
    for p in root.rglob("*"):
        if p.is_file() and ".git" not in p.relative_to(root).parts:
            yield p.relative_to(root).as_posix()


def main() -> int:
    if len(sys.argv) != 2:
        print("用法: sync_source.py <上游目录>", file=sys.stderr)
        return 1

    upstream = pathlib.Path(sys.argv[1]).resolve()
    if not (upstream / "BetterGenshinImpact").is_dir():
        print(f"[x] {upstream} 不像 BetterGI 源码目录", file=sys.stderr)
        return 2

    local = pathlib.Path.cwd()
    up_files = set(files_of(upstream))
    local_files = set(files_of(local))

    removed = 0
    blocked = []
    for rel in sorted(local_files - up_files):
        if kept(rel):
            continue
        if protected(rel):
            # 不删，记下来，最后整体失败：宁可这次同步不推，也别把能力层删掉
            blocked.append(rel)
            continue
        (local / rel).unlink()
        removed += 1

    if blocked:
        print(f"[x] 有 {len(blocked)} 个只属于特别版的文件即将被删，已中止：", file=sys.stderr)
        for rel in blocked[:20]:
            print(f"    {rel}", file=sys.stderr)
        print("    处理办法：确认后加进 sync_source.py 的 KEEP 清单再重跑。", file=sys.stderr)
        return 3

    copied = 0
    for rel in sorted(up_files):
        if kept(rel):
            continue
        dst = local / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(upstream / rel, dst)
        copied += 1

    # 清空目录，保留 .git
    for d in sorted((p for p in local.rglob("*") if p.is_dir()), key=lambda p: len(p.parts), reverse=True):
        if ".git" in d.relative_to(local).parts:
            continue
        try:
            d.rmdir()
        except OSError:
            pass

    print(f"[+] 删除 {removed} 个文件，覆盖 {copied} 个文件")
    return 0


if __name__ == "__main__":
    sys.exit(main())
