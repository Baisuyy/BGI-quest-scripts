#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用上游快照覆盖当前源码，同时保留特别版自有文件。

用法：python3 tools/bgi-special/sync_source.py <上游目录> [上次上游目录]

行为：
0. 核心文件（CORE）做三方比较；我们的改动与上游改动重叠时不覆盖、不提交，
   diff 写到 sync-review/，退出码 4
1. 删除当前目录中「上游没有、且不在保留清单」的文件（含上游已废弃的源码）
2. 用上游文件覆盖当前目录（保留清单内的文件不覆盖）
3. 清理空目录

不处理 .git，也不做 Git 操作。第二个参数由 CI 提供（.bgi-upstream 指向的上次快照），
本地手动跑可以省略。
"""

import difflib
import pathlib
import re
import shutil
import sys

# 相对仓库根路径；以 / 结尾表示整个目录
KEEP = (
    # 整目录保留，不跟上游。两个原因：
    # 1) GITHUB_TOKEN 推不了 workflow 文件（GitHub 硬限制，permissions 里没有对应开关）。
    #    上游一旦改了 publish.yml，同步提交就带上 workflow 变更，push 会被
    #    「refusing to allow a GitHub App to create or update workflow」直接拒掉，
    #    而日志里只看到 exit code 1。2026-11-25 就是这么失效的。
    # 2) 上游那套 publish.yml / mirrorchyan 发布流程对特别版没有意义，进来只会
    #    在 Actions 列表里多几个手滑就能触发上游发版的按钮。
    # 特别版自己的 CI 全部手写在这个目录里，由本文件自己的提交维护。
    ".github/workflows/",
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
    # 设置页的特别版卡片（日志 / 远程任务 / 米游社绑定）
    "BetterGenshinImpact/View/Controls/SpecialSettingsCards.xaml",
    "BetterGenshinImpact/View/Controls/SpecialSettingsCards.xaml.cs",
    "BetterGenshinImpact/ViewModel/Pages/CommonSettingsPageViewModel.Special.cs",
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
    # 全局搜索：顶栏输入框 + 独立结果页，上游没有
    "BetterGenshinImpact/Service/Search/",
    "BetterGenshinImpact/View/Pages/SearchResultPage.xaml",
    "BetterGenshinImpact/View/Pages/SearchResultPage.xaml.cs",
    "BetterGenshinImpact/ViewModel/Pages/SearchResultPageViewModel.cs",
    # 插件市场
    "BetterGenshinImpact/Core/Plugins/Market/",
    "BetterGenshinImpact/Core/Config/PluginMarketConfig.cs",
    "BetterGenshinImpact/ViewModel/Pages/Plugins/PluginMarketItemViewModel.cs",
    # 插件页的界面与 VM 由特别版自己维护：页签、市场卡片改动太大，
    # 做不成锚点补丁。上游改这两个文件时不会自动跟进来，同步会打印冲突提醒，
    # 需要人工比对一次再决定要不要合并。
    "BetterGenshinImpact/View/Pages/PluginsPage.xaml",
    "BetterGenshinImpact/ViewModel/Pages/Plugins/PluginsPageViewModel.cs",
    "plugin-market/",
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
    "plugin-market/",
    "AGENTS.md",
    "Build/kachina.special.config.json",
    ".github/workflows/upload-123pan.yml",
    "Test/BetterGenshinImpact.UnitTest/ServiceTests/",
    # 特别版新增的单测：上游没有，不登记就在同步时被当「废弃文件」删掉，
    # 而且删了不会编译失败，只是悄悄少了一层保护。
    "Test/BetterGenshinImpact.UnitTest/HelperTests/RepeatSuppressorSinkTests.cs",
    "tools/bgi-special/",
)


# 上游有、但特别版明确不要的文件。同步时既不从上游拷贝进来，本地残存的也照删。
# 与 KEEP 相反：KEEP 是「上游没有也要留着」，DROP 是「上游有也不许进来」。
#
# 注意：别往这里放 .github/workflows/ 下的东西。KEEP 整个目录之后，上游的 workflow
# 本来就进不来；而「删本地残存」这一步会让提交里出现 workflow 删除，一样触发
# GITHUB_TOKEN 的 workflow 写限制，被我们自己的闸门挡住。要清就手工 git rm 一次。
DROP = ()


# ---------------------------------------------------------------- 核心文件标记
#
# 「上游文件，但特别版改过」的核心文件：同步时先整份覆盖，再由 patch_special.py
# 按锚点把补丁贴回去。锚点匹配成功不等于语义正确——上游若在同一处也改了，
# 补丁可能贴到一个已经变形的上下文里，编译照过，功能静默错位。
# 2026-11-26 的 QQ 机器人注册、11-27 的插件版本比较都是这样丢的。
#
# 标记的含义：同步前对它们做三方比较
#     base   = 上次同步时的上游版本（.bgi-upstream 指向的 commit）
#     ours   = 本地（上游 + 我们的补丁）
#     theirs = 本次上游
# 只有「我们改过的地方」与「上游改过的地方」重叠才算冲突。不重叠时上游覆盖、
# 补丁重贴是安全的，照常同步——否则上游随手改一行 App.xaml.cs 就会天天卡住。
#
# 有冲突：不覆盖、不提交，diff 写到 sync-review/，退出码 4，等人看完再处理。
#
# csproj 不在这里：它的 <Version> 由 restore_version 单独保护，整份覆盖是预期行为。
CORE = (
    "BetterGenshinImpact/App.xaml.cs",
    "BetterGenshinImpact/Core/Config/Global.cs",
    "BetterGenshinImpact/Core/Plugins/Hosting/PluginCompatibility.cs",
    "BetterGenshinImpact/Core/Script/EngineExtend.cs",
    "BetterGenshinImpact/Core/Script/Project/Manifest.cs",
    "BetterGenshinImpact/GameTask/AutoPathing/Model/PathingTask.cs",
    "BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.cs",
    "BetterGenshinImpact/Infrastructure/Network/Download/DownloadCategory.cs",
    "BetterGenshinImpact/Service/ApplicationHostService.cs",
    "BetterGenshinImpact/Service/UpdateService.cs",
    "BetterGenshinImpact/View/MainWindow.xaml",
    "BetterGenshinImpact/View/MainWindow.xaml.cs",
    "BetterGenshinImpact/View/Pages/CommonSettingsPage.xaml",
    "BetterGenshinImpact/View/Pages/TaskSettingsPage.xaml",
    "BetterGenshinImpact/ViewModel/Pages/CommonSettingsPageViewModel.cs",
    "BetterGenshinImpact/ViewModel/Pages/TaskSettingsPageViewModel.cs",
)


def kept(rel: str) -> bool:
    # *.Special.cs 是「特别版寄存在上游类型上的 partial 文件」的固定约定。
    # 这类文件专门用来避开一个坑：CommonConfig / AllConfig 上游也有，同步时会被整份覆盖，
    # 字段写在里面就等于每轮被删一次（2026-11-20 因此让远端分支编译不过）。
    # 用后缀自动识别，省得每加一个就得记得回来改清单——漏登记的代价是编译失败。
    if rel.endswith(".Special.cs"):
        return True
    return any(rel == k or (k.endswith("/") and rel.startswith(k)) for k in KEEP)


def dropped(rel: str) -> bool:
    return any(rel == d or (d.endswith("/") and rel.startswith(d)) for d in DROP)


# 只属于特别版、上游永远没有的目录。它们本来该在 KEEP 里，但 KEEP 是手写清单，
# 新功能加进来时很容易漏登记——2026-11-19 那次同步就把 Service/RemoteTask 整个删了，
# 代码在远端躺了一夜编译不过。这里当最后一道闸：真要删，先报错让人确认。
PROTECTED = (
    "BetterGenshinImpact/Service/RemoteTask/",
    "BetterGenshinImpact/Service/Search/",
    "BetterGenshinImpact/Core/Ai/",
    "BetterGenshinImpact/Core/Special/",
    "BetterGenshinImpact/Core/Mys/",
    "BetterGenshinImpact/GameTask/GenshinAccountSwitch/",
)


def protected(rel: str) -> bool:
    return any(rel == p or (p.endswith("/") and rel.startswith(p)) for p in PROTECTED)


# csproj 整份跟随上游，但 <Version> 是特别版自己的版本线，不能跟着变。
# 上游写着 0.67.2-alpha.2 时直接覆盖，下次本地构建出来的版本号就悄悄换了，
# 而特别版的更新检查、发版说明、产物命名全挂在这串上。版本号只由人改。
CSPROJ = "BetterGenshinImpact/BetterGenshinImpact.csproj"
VERSION_RE = re.compile(r"<Version>[^<]*</Version>")


def read_version() -> str | None:
    path = pathlib.Path.cwd() / CSPROJ
    if not path.is_file():
        return None
    match = VERSION_RE.search(path.read_text(encoding="utf-8"))
    return match.group(0) if match else None


def restore_version(version: str | None) -> None:
    if version is None:
        print("[!] 本地 csproj 没有 <Version>，跳过版本号保护", file=sys.stderr)
        return
    path = pathlib.Path.cwd() / CSPROJ
    if not path.is_file():
        print(f"[!] 覆盖后找不到 {CSPROJ}", file=sys.stderr)
        return
    text = path.read_text(encoding="utf-8")
    if not VERSION_RE.search(text):
        print(f"[!] {CSPROJ} 里没有 <Version>，跳过版本号保护", file=sys.stderr)
        return
    if VERSION_RE.search(text).group(0) == version:
        return
    path.write_text(VERSION_RE.sub(version, text, count=1), encoding="utf-8")
    print(f"[+] 保留特别版版本号 {version[9:-10]}")


def files_of(root: pathlib.Path):
    for p in root.rglob("*"):
        if p.is_file() and ".git" not in p.relative_to(root).parts:
            yield p.relative_to(root).as_posix()


def read_text_of(path: pathlib.Path) -> str | None:
    if not path.is_file():
        return None
    return path.read_text(encoding="utf-8", errors="replace")


def changed_ranges(base: str, other: str) -> list[tuple[int, int]]:
    """把 base → other 的改动映射回 base 的行号区间（左闭右开，前后各放宽一行）。"""
    matcher = difflib.SequenceMatcher(
        None, base.splitlines(), other.splitlines(), autojunk=False)
    ranges = []
    for tag, i1, i2, _j1, _j2 in matcher.get_opcodes():
        if tag == "equal":
            continue
        # 纯插入时 i1 == i2；再向两侧各放一行——补丁锚点通常带上下文，
        # 上游改在紧邻的一行，锚点同样会失配，算冲突更安全。
        ranges.append((max(0, i1 - 1), i2 + 1))
    return ranges


def ranges_overlap(left, right) -> bool:
    return any(s1 < e2 and s2 < e1 for s1, e1 in left for s2, e2 in right)


def write_review(local: pathlib.Path, rel: str, base: str, ours: str,
                 theirs: str, reason: str) -> None:
    """把一处冲突的两份改动写成 diff，供人工看。"""
    folder = local / "sync-review"
    folder.mkdir(exist_ok=True)
    out = folder / (rel.replace("/", "__") + ".diff")

    chunks = [
        f"# {rel}\n# {reason}\n#\n"
        f"# 先看「我们的改动」是否仍然必要，再决定：把补丁挪到上游的新位置，还是放弃这一处。\n"
        f"# 改完重跑同步；确认无误后删除 sync-review/。\n\n",
        "=== 我们的改动（base → 本地）===\n",
    ]
    chunks.extend(difflib.unified_diff(
        base.splitlines(keepends=True), ours.splitlines(keepends=True),
        fromfile=f"{rel} (base)", tofile=f"{rel} (ours)", n=3))
    chunks.append("\n=== 本次上游的改动（base → 上游）===\n")
    chunks.extend(difflib.unified_diff(
        base.splitlines(keepends=True), theirs.splitlines(keepends=True),
        fromfile=f"{rel} (base)", tofile=f"{rel} (theirs)", n=3))
    out.write_text("".join(chunks), encoding="utf-8")


def review_core(upstream: pathlib.Path, base_dir: pathlib.Path | None,
                local: pathlib.Path) -> list[tuple[str, str]]:
    """核心文件三方比较。返回 [(文件, 原因)]，冲突的 diff 已写到 sync-review/。"""
    if base_dir is None or not base_dir.is_dir():
        print("[i] 没有上次上游快照，本轮跳过核心文件比对（下次起生效）")
        return []

    review_dir = local / "sync-review"
    if review_dir.is_dir():
        shutil.rmtree(review_dir)

    clashes: list[tuple[str, str]] = []
    for rel in CORE:
        ours = read_text_of(local / rel)
        theirs = read_text_of(upstream / rel)
        base = read_text_of(base_dir / rel)

        if theirs is None:
            if ours is not None:
                clashes.append((rel, "上游删除了这个文件，补丁会失去落点"))
            continue
        if ours is None:
            continue
        if base is None:
            # 上一版上游还没有这个文件：本次是新增。本地若已有同名内容，需要人看
            if ours != theirs:
                clashes.append((rel, "上游新增同名文件，本地已有内容"))
                write_review(local, rel, ours, ours, theirs, "上游新增同名文件")
            continue
        if ours == base:
            continue  # 我们没改过，覆盖即可
        if theirs == base:
            continue  # 上游没改过，保留我们的版本

        mine = changed_ranges(base, ours)
        incoming = changed_ranges(base, theirs)
        if ranges_overlap(mine, incoming):
            clashes.append(
                (rel, f"双方改了同一区域（我们 {len(mine)} 处 / 上游 {len(incoming)} 处）"))
            write_review(local, rel, base, ours, theirs, "双方改动区间重叠")
        else:
            print(f"[=] 核心文件 {rel}：改动不重叠，可自动合并")

    return clashes


def main() -> int:
    if len(sys.argv) not in (2, 3):
        print("用法: sync_source.py <上游目录> [上次上游目录]", file=sys.stderr)
        return 1

    upstream = pathlib.Path(sys.argv[1]).resolve()
    if not (upstream / "BetterGenshinImpact").is_dir():
        print(f"[x] {upstream} 不像 BetterGI 源码目录", file=sys.stderr)
        return 2
    base_dir = pathlib.Path(sys.argv[2]).resolve() if len(sys.argv) == 3 else None

    local = pathlib.Path.cwd()
    up_files = set(files_of(upstream))
    local_files = set(files_of(local))
    saved_version = read_version()

    # 核心文件先过一遍：撞上了就什么都不改，交给人看
    clashes = review_core(upstream, base_dir, local)
    if clashes:
        print(f"[x] {len(clashes)} 个核心文件的改动与上游撞车，本次不覆盖、不提交：",
              file=sys.stderr)
        for rel, reason in clashes:
            print(f"    ! {rel} —— {reason}", file=sys.stderr)
        print("    diff 已写到 sync-review/，逐个看完再处理；处理完重跑同步。",
              file=sys.stderr)
        return 4

    removed = 0
    removed_names = []
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
        removed_names.append(rel)

    if blocked:
        print(f"[x] 有 {len(blocked)} 个只属于特别版的文件即将被删，已中止：", file=sys.stderr)
        for rel in blocked[:20]:
            print(f"    {rel}", file=sys.stderr)
        print("    处理办法：确认后加进 sync_source.py 的 KEEP 清单再重跑。", file=sys.stderr)
        return 3

    # 上游有也不许进来的文件：本地若还残留，一并清掉
    for rel in sorted(f for f in local_files if dropped(f) and f in up_files):
        (local / rel).unlink(missing_ok=True)
        removed += 1

    copied = 0
    conflicts = []
    for rel in sorted(up_files):
        if dropped(rel):
            continue
        if kept(rel):
            # 保留清单里的文件不覆盖；但上游也改了同一个文件时要提醒——
            # 这类文件（如插件页）长期由特别版维护，上游的结构改动会悄悄错过
            src = upstream / rel
            dst = local / rel
            if dst.is_file() and src.read_bytes() != dst.read_bytes():
                conflicts.append(rel)
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
    restore_version(saved_version)
    # 删了什么要能看见：只报个数字，事后想确认「是不是误删」只能去翻 diff。
    for rel in removed_names[:20]:
        print(f"    - {rel}")
    if len(removed_names) > 20:
        print(f"    …… 另有 {len(removed_names) - 20} 个")
    if conflicts:
        print(f"[!] {len(conflicts)} 个文件上游也改了，但保留清单里是本地版本，需要人工比对：")
        for rel in conflicts[:15]:
            print(f"    ~ {rel}")
        if len(conflicts) > 15:
            print(f"    …… 另有 {len(conflicts) - 15} 个")
    return 0


if __name__ == "__main__":
    sys.exit(main())
