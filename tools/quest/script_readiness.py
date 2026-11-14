#!/usr/bin/env python3
"""统计每个任务脚本的可跑性。

判据只有一条：这一段在 pathing/*.json 里有几个点。
  - 0/1 个点：任务提示坐标，不是路线。脚本会跳过（硬跑会直冲目标，常停在副本门口）。
  - 2 个点以上：能生成行走路线，是否顺畅另说。

输出 docs/脚本可跑性报告.md，并在终端打印汇总。
"""
from __future__ import annotations

import json
import re
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
JS = ROOT / "js"
OUT = ROOT / "docs" / "脚本可跑性报告.md"

SEG_RE = re.compile(r"const SEGMENTS = (\[.*?\]);", re.S)
SKIP_RE = re.compile(r"skipSegs:\s*\[([^\]]*)\]")


def parse_script(main: Path) -> list[dict]:
    text = main.read_text(encoding="utf-8-sig")
    m = SEG_RE.search(text)
    if not m:
        return []

    try:
        segs = json.loads(m.group(1))
    except json.JSONDecodeError:
        return []

    skip_raw = SKIP_RE.search(text)
    skipped = {int(x) for x in re.findall(r"\d+", skip_raw.group(1))} if skip_raw else set()

    base = main.parent
    rows = []
    for seg in segs:
        path = base / seg.get("file", "")
        points = 0
        if path.exists():
            try:
                points = len(json.loads(path.read_text(encoding="utf-8-sig")).get("positions") or [])
            except json.JSONDecodeError:
                points = -1
        rows.append({
            "seq": seg.get("seq"),
            "label": seg.get("label", ""),
            "points": points,
            "skipped": seg.get("seq") in skipped,
        })
    return rows


def status_of(rows: list[dict]) -> str:
    if not rows:
        return "无法解析"

    active = [r for r in rows if not r["skipped"]]
    if not active:
        return "全部跳过"

    runnable = [r for r in active if r["points"] >= 2]
    if not runnable:
        return "跑不了"
    if len(runnable) < len(active):
        return "部分可用"
    return "可用"


def main() -> None:
    tasks = []
    for main_js in sorted(JS.rglob("main.js")):
        if "_tools" in main_js.parts:
            continue
        rows = parse_script(main_js)
        rel = main_js.relative_to(JS)
        tasks.append({
            "path": str(rel.parent),
            "name": rel.parent.name,
            "rows": rows,
            "status": status_of(rows),
        })

    counter = Counter(t["status"] for t in tasks)
    total_segs = sum(len(t["rows"]) for t in tasks)
    single = sum(1 for t in tasks for r in t["rows"] if r["points"] < 2)

    lines = [
        "# 任务脚本可跑性报告",
        "",
        "由 `tools/quest/script_readiness.py` 生成。判据：一段路线里有几个点。",
        "",
        "- **0/1 个点** = 任务提示坐标，不是能走的路线。脚本运行时会跳过并打警告，"
        "不会再把角色直冲过去（旧版会跑到副本门口这类地方，看起来像「脚本错位」）。",
        "- **2 个点以上** = 能生成行走路线。地形是否顺畅、锚点远不远另说。",
        "",
        "## 汇总",
        "",
        "| 指标 | 数值 |",
        "| --- | --- |",
        f"| 脚本总数 | {len(tasks)} |",
        f"| 路线段总数 | {total_segs} |",
        f"| 单点/空段 | {single} |",
        f"| 可用 | {counter.get('可用', 0)} |",
        f"| 部分可用 | {counter.get('部分可用', 0)} |",
        f"| 跑不了 | {counter.get('跑不了', 0)} |",
        f"| 全部跳过 | {counter.get('全部跳过', 0)} |",
        "",
        "## 跑不了的任务",
        "",
        "这些脚本的每一段都只有 1 个点，运行后只会打印「已跳过」，等于没有自动化。"
        "需要人工录制路线，或换用更好的坐标来源。",
        "",
        "| 任务 | 段数 | 单点段 |",
        "| --- | --- | --- |",
    ]

    for t in tasks:
        if t["status"] != "跑不了":
            continue
        lines.append(f"| {t['name']} | {len(t['rows'])} | {sum(1 for r in t['rows'] if r['points'] < 2)} |")

    lines += ["", "## 部分可用", "", "| 任务 | 可用段 | 总段 | 说明 |", "| --- | --- | --- | --- |"]
    for t in tasks:
        if t["status"] != "部分可用":
            continue
        runnable = sum(1 for r in t["rows"] if not r["skipped"] and r["points"] >= 2)
        skipped = sum(1 for r in t["rows"] if r["skipped"])
        note = f"跳过 {skipped} 段" if skipped else "含单点段"
        lines.append(f"| {t['name']} | {runnable} | {len(t['rows'])} | {note} |")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"脚本 {len(tasks)} 个 / 段 {total_segs} / 单点段 {single}")
    print(dict(counter))
    print("写入", OUT)


if __name__ == "__main__":
    main()
