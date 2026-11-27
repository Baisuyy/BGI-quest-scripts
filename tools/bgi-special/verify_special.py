#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""同步后的静态闸门：确认特别版的东西都还在。

用法：python3 tools/bgi-special/verify_special.py

为什么需要这个：sync_source.py 用上游源码整份覆盖，patch_special.py 再按锚点贴回改动。
两者都可能「跑成功但结果不对」——

* 上游删了某个文件，而它不在 KEEP 里 → 文件没了，编译报错（还好，能发现）
* 上游改了某处结构，patch 锚点恰好还能匹配但语义变了 → 编译过了，功能没了（最难发现）
* 有人把特别版字段写回上游文件 → 当次没事，下次同步被覆盖

第三种最阴，2026-11-20 就是这么坏的：CommonConfig.cs 里放着 LogUploadMode，
同步一覆盖，远端分支连编译都过不去，而推送那一步是成功的。

本脚本只做静态检查，跑在同步 workflow 里、推送之前。任一项缺失就返回非 0，
workflow 会中止并开 issue——宁可这次不同步，也别推一个坏的分支上去。
"""

import pathlib
import sys

ROOT = pathlib.Path.cwd()

# 必须存在的文件/目录。这些是特别版的能力层，上游永远不会有。
MUST_EXIST = (
    "BetterGenshinImpact/Core/Special/",
    "BetterGenshinImpact/Core/Ai/",
    "BetterGenshinImpact/Core/Mys/",
    "BetterGenshinImpact/Service/RemoteTask/",
    "BetterGenshinImpact/GameTask/GenshinAccountSwitch/",
    "BetterGenshinImpact/Core/Config/CommonConfig.Special.cs",
    "BetterGenshinImpact/Core/Config/AllConfig.Special.cs",
    "BetterGenshinImpact/Core/Pathing/",
    "BetterGenshinImpact/GameTask/AutoPathing/PathProgressStore.cs",
    "Test/BetterGenshinImpact.UnitTest/HelperTests/RepeatSuppressorSinkTests.cs",
    # 插件市场：索引模型、下载校验、卡片 VM，以及索引文件本身
    "BetterGenshinImpact/Core/Plugins/Market/",
    "BetterGenshinImpact/Core/Config/PluginMarketConfig.cs",
    "BetterGenshinImpact/ViewModel/Pages/Plugins/PluginMarketItemViewModel.cs",
    "plugin-market/index.json",
    # 设置页的特别版卡片与日志策略（2026-11-26 同步把这两块从上游文件里吃掉了）
    "BetterGenshinImpact/View/Controls/SpecialSettingsCards.xaml",
    "BetterGenshinImpact/View/Controls/SpecialSettingsCards.xaml.cs",
    "BetterGenshinImpact/ViewModel/Pages/CommonSettingsPageViewModel.Special.cs",
    "BetterGenshinImpact/Core/Special/SpecialLogging.cs",
    # 特别版 CI 不跟上游。这两个文件上游没有，靠 KEEP 的 .github/workflows/ 保留；
    # 一旦 KEEP 被误删，它们会被当成「上游已废弃文件」清掉，这里立刻报出来。
    ".github/workflows/build-special.yml",
    ".github/workflows/sync-upstream.yml",
)

# (文件, 必须出现的一段文本, 说明)。逐条对应 patch_special.py 里的一个补丁，
# 以及 Special 文件里的关键成员——漏一条就是补丁没贴上或字段被覆盖了。
MUST_CONTAIN = (
    ("BetterGenshinImpact/Core/Script/EngineExtend.cs",
     'AddHostObject("ocr"', "脚本宿主 ocr/quest/ai"),
    ("BetterGenshinImpact/App.xaml.cs",
     "AddSingleton<AiKernel>", "AiKernel 单例注册"),
    ("BetterGenshinImpact/App.xaml.cs",
     "AddView<AiSettingsPage", "AI 助手页注册"),
    ("BetterGenshinImpact/Service/ApplicationHostService.cs",
     "AgentPanelWindow.EnsureGlobalHotkey", "助手小面板快捷键"),
    # 远程任务 / QQ 机器人 / 日志增强：注册与日志链路都在上游文件里，靠补丁贴回
    ("BetterGenshinImpact/App.xaml.cs",
     "AddSingleton<QqBotService>", "QQ 机器人注册"),
    ("BetterGenshinImpact/App.xaml.cs",
     "AddSingleton<RemoteScheduleService>", "定时任务注册"),
    ("BetterGenshinImpact/App.xaml.cs",
     "SpecialLogging.ArchiveLegacyLog", "旧日志归档"),
    ("BetterGenshinImpact/App.xaml.cs",
     "RepeatSuppressorSink", "日志重复抑制"),
    ("BetterGenshinImpact/App.xaml.cs",
     "ErrorDiagnosticsSink", "出错自动攒诊断包"),
    ("BetterGenshinImpact/View/Pages/CommonSettingsPage.xaml",
     "controls:SpecialSettingsCards", "设置页引用特别版卡片"),
    ("BetterGenshinImpact/View/Controls/SpecialSettingsCards.xaml",
     "QqBotAppId", "机器人设置界面"),
    ("BetterGenshinImpact/ViewModel/Pages/CommonSettingsPageViewModel.Special.cs",
     "OnUploadLog", "日志上传命令"),
    # 特别版版本号在 SemVer 里低于同版本正式版，插件兼容性比较前必须剥掉 -special 后缀
    ("BetterGenshinImpact/Core/Config/Global.cs",
     "public static string StripSpecialSuffix", "版本后缀剥离已公开"),
    ("BetterGenshinImpact/Core/Plugins/Hosting/PluginCompatibility.cs",
     "StripSpecialSuffix(Global.Version)", "插件版本比较剥后缀"),
    # 同步闸门本身：核心文件三方比较 + 基线传入，缺一条这道闸就形同虚设
    ("tools/bgi-special/sync_source.py",
     "def review_core", "核心文件三方比较"),
    (".github/workflows/sync-upstream.yml",
     "/tmp/upstream-base", "同步传入上次上游快照"),
    ("BetterGenshinImpact/Service/UpdateService.cs",
     "SpecialUpdater.TryHandleAsync", "更新检查改道"),
    ("BetterGenshinImpact/View/MainWindow.xaml",
     "pages:AiSettingsPage", "侧边栏 AI 助手入口"),
    ("BetterGenshinImpact/View/Pages/TaskSettingsPage.xaml",
     "controls:AiSettingsCard", "设置页 AI 卡片"),
    ("BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.cs",
     "TryAiRescueAsync", "跑图卡死 AI 接管"),
    ("BetterGenshinImpact/Core/Config/CommonConfig.Special.cs",
     "_logUploadMode", "日志上传档位"),
    ("BetterGenshinImpact/Core/Config/CommonConfig.Special.cs",
     "_pathingResumeEnabled", "断点续跑开关"),
    ("BetterGenshinImpact/Core/Config/AllConfig.Special.cs",
     "AiConfig AiConfig", "内置 AI 配置节点"),
    ("BetterGenshinImpact/Core/Config/AllConfig.Special.cs",
     "RemoteTaskConfig RemoteTaskConfig", "远程任务配置节点"),
    # 全局搜索：新增文件靠 KEEP，改到的上游文件靠 patch_special
    ("BetterGenshinImpact/View/MainWindow.xaml",
     "GlobalSearchBox", "顶栏搜索框"),
    ("BetterGenshinImpact/App.xaml.cs",
     "AddView<SearchResultPage", "搜索结果页注册"),
    # 版本需求比较：上游文件，改一次被冲一次
    ("BetterGenshinImpact/Core/Config/Global.cs",
     "IsRequirementMet", "版本需求比较"),
    # 插件市场：PluginsPage 的两个文件由特别版自己维护（见 KEEP 注释）
    ("BetterGenshinImpact/App.xaml.cs",
     "AddSingleton<PluginMarketClient>", "插件市场注册"),
    ("BetterGenshinImpact/View/Pages/PluginsPage.xaml",
     "插件市场", "插件页市场页签"),
    ("BetterGenshinImpact/ViewModel/Pages/Plugins/PluginsPageViewModel.cs",
     "MarketItems", "插件市场列表"),
)

# 反向检查：这些上游文件里不该出现特别版字段。
# 一旦出现，说明有人把字段写回了会被覆盖的地方，下次同步必然丢失。
MUST_NOT_CONTAIN = (
    ("BetterGenshinImpact/Core/Config/CommonConfig.cs",
     ("_logUploadMode", "_logFileLevel", "_pathingResumeEnabled", "_lastNotifiedSpecialVersion"),
     "特别版字段请写在 CommonConfig.Special.cs"),
    ("BetterGenshinImpact/Core/Config/AllConfig.cs",
     ("AiConfig AiConfig", "RemoteTaskConfig RemoteTaskConfig"),
     "特别版节点请写在 AllConfig.Special.cs"),
)


def main() -> int:
    problems = []

    for rel in MUST_EXIST:
        path = ROOT / rel.rstrip("/")
        if not path.exists():
            problems.append(f"缺少 {rel}（特别版能力层被同步删掉了）")

    for rel, needle, label in MUST_CONTAIN:
        path = ROOT / rel
        if not path.is_file():
            problems.append(f"{label}：找不到 {rel}")
            continue
        if needle not in path.read_text(encoding="utf-8", errors="replace"):
            problems.append(f"{label}：{rel} 里没有「{needle}」")

    for rel, needles, hint in MUST_NOT_CONTAIN:
        path = ROOT / rel
        if not path.is_file():
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for needle in needles:
            if needle in text:
                problems.append(f"{rel} 里出现了「{needle}」——{hint}")

    if problems:
        print(f"[x] 特别版完整性检查未通过，共 {len(problems)} 项：", file=sys.stderr)
        for item in problems:
            print(f"    - {item}", file=sys.stderr)
        print("", file=sys.stderr)
        print("    处理办法：", file=sys.stderr)
        print("    · 能力层文件缺失 → 加进 sync_source.py 的 KEEP", file=sys.stderr)
        print("    · 补丁标记缺失 → 看 patch_special.py 对应那条，锚点可能被上游改了", file=sys.stderr)
        print("    · 上游文件里有特别版字段 → 搬到同名的 *.Special.cs", file=sys.stderr)
        return 1

    print(f"[+] 完整性检查通过：{len(MUST_EXIST)} 项存在性、"
          f"{len(MUST_CONTAIN)} 项标记、{len(MUST_NOT_CONTAIN)} 项反向检查")
    return 0


if __name__ == "__main__":
    sys.exit(main())
