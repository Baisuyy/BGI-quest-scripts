#!/usr/bin/env python3
"""把「上游 main 有、我们跟随的 refactor 分支没有」的增量重新贴回去。

背景：bgi-special 跟随上游 refactor/20260930（.NET 10 重构版），而 0.67.0 的功能
（浪涛锚定者、蕴光月幻蝶改自动寻路）只进了 main。每日同步会用 refactor 的树整体覆盖，
这些增量会被冲掉，所以在这里固化成可重复执行的补丁。

顺序：sync_source.py 覆盖源码 → patch_special.py 注入特别版改动 → 本脚本贴 0.67 增量。

幂等：重复执行结果一致。锚点缺失时返回非零，让同步 workflow 失败并开 issue，
而不是静默产出一个少了内容却"看起来成功"的版本。
"""

from __future__ import annotations

import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXTRA = Path(__file__).resolve().parent / "extra" / "067"
PATHING = ROOT / "BetterGenshinImpact/GameTask/AutoBoss/Assets/Pathing"
AUTO_BOSS = ROOT / "BetterGenshinImpact/GameTask/AutoBoss/AutoBossData.cs"
CSPROJ = ROOT / "BetterGenshinImpact/BetterGenshinImpact.csproj"

# 0.67.0 里下架的两条旧路线：强制传送 + 键鼠宏
RETIRED_PATHS = ["蕴光月幻蝶强制传送.json", "蕴光月幻蝶键鼠前往.json"]

TARGET_VERSION = "0.67.0"

failed = False


def note(text: str) -> None:
    print(f"[+] {text}")


def warn(text: str) -> None:
    global failed
    failed = True
    print(f"[!] {text}", file=sys.stderr)


def copy_paths() -> None:
    PATHING.mkdir(parents=True, exist_ok=True)
    for src in sorted((EXTRA / "Pathing").glob("*.json")):
        dst = PATHING / src.name
        if dst.exists() and dst.read_bytes() == src.read_bytes():
            continue
        shutil.copyfile(src, dst)
        note(f"路线 {src.name}")


def remove_retired() -> None:
    for name in RETIRED_PATHS:
        path = PATHING / name
        if path.exists():
            path.unlink()
            note(f"下架 {name}")


def patch_auto_boss() -> None:
    if not AUTO_BOSS.exists():
        warn(f"找不到 {AUTO_BOSS.relative_to(ROOT)}")
        return

    text = AUTO_BOSS.read_text(encoding="utf-8")
    original = text

    # 1) 至冬首领列表补「浪涛锚定者」
    winter = re.search(r'\["至冬"\]\s*=\s*\[([^\]]*)\]', text)
    if not winter:
        warn("AutoBossData 里找不到「至冬」首领列表")
    elif "浪涛锚定者" not in winter.group(1):
        items = [item.strip() for item in winter.group(1).split(",") if item.strip()]
        items.append('"浪涛锚定者"')
        text = text[: winter.start(1)] + ", ".join(items) + text[winter.end(1):]
        note("至冬首领 + 浪涛锚定者")

    # 2) 蕴光月幻蝶已支持自动寻路，从「无路线」名单里移除
    block = re.search(r"NoPathingSupportBosses\s*=\s*\[(.*?)\];", text, re.S)
    if not block:
        warn("AutoBossData 里找不到 NoPathingSupportBosses")
    elif '"蕴光月幻蝶"' in block.group(1):
        # 按行删，保留缩进与括号位置；顺手去掉最后一项的尾逗号
        lines = [line for line in block.group(1).splitlines() if '"蕴光月幻蝶"' not in line]
        for i in range(len(lines) - 1, -1, -1):
            if lines[i].strip():
                lines[i] = lines[i].rstrip().rstrip(",")
                break
        text = text[: block.start(1)] + "\n".join(lines) + text[block.end(1):]
        note("蕴光月幻蝶 移出无路线名单")

    if text != original:
        AUTO_BOSS.write_text(text, encoding="utf-8")


def bump_version() -> None:
    if not CSPROJ.exists():
        warn("找不到 csproj")
        return

    text = CSPROJ.read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", text)
    if not match:
        warn("csproj 里找不到 <Version>")
        return

    current = match.group(1).strip()
    if current == TARGET_VERSION:
        return

    if _less_than(current, TARGET_VERSION):
        text = text[: match.start(1)] + TARGET_VERSION + text[match.end(1):]
        CSPROJ.write_text(text, encoding="utf-8")
        note(f"版本 {current} → {TARGET_VERSION}")
    else:
        note(f"版本已是 {current}，不降级")


def _less_than(left: str, right: str) -> bool:
    """只比较数字段，够用即可（0.66.3-alpha.3 vs 0.67.0）。"""
    def numbers(value: str) -> list[int]:
        head = value.split("-", 1)[0]
        return [int(part) for part in head.split(".") if part.isdigit()]

    a, b = numbers(left), numbers(right)
    for i in range(max(len(a), len(b))):
        x = a[i] if i < len(a) else 0
        y = b[i] if i < len(b) else 0
        if x != y:
            return x < y
    return False


def main() -> int:
    copy_paths()
    remove_retired()
    patch_auto_boss()
    bump_version()
    if failed:
        print("0.67 增量回填失败：锚点变了，按上面的提示修脚本", file=sys.stderr)
        return 1
    print("完成")
    return 0


if __name__ == "__main__":
    sys.exit(main())
