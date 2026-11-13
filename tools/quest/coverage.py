#!/usr/bin/env python3
"""对照 data/quest_steps.json，检查现有路线覆盖了哪些「真的需要跑图」的步骤。

只统计 kind=path 的步骤：对话、剧情、脚本事件本来就不该有路线，
把它们算进缺口的会得出「脚本大量缺失」的错误结论。

用法：
  python3 tools/quest/coverage.py --threshold 50
"""

import argparse
import collections
import json
import math
from pathlib import Path


def build_index(root: Path):
    """路线点按 100m 网格分桶，查询时只看邻域，避免 5000 万次距离计算。"""
    grid = collections.defaultdict(list)
    total = 0
    for f in root.glob("js/**/pathing/*.json"):
        try:
            data = json.load(f.open(encoding="utf-8"))
        except Exception:
            continue
        for p in data.get("positions") or []:
            x, y = p.get("x"), p.get("y")
            if x is None or y is None:
                continue
            grid[(math.floor(x / 100), math.floor(y / 100))].append((x, y))
            total += 1
    return grid, total


def covered(grid, x, z, lim) -> bool:
    gx, gz = math.floor(x / 100), math.floor(z / 100)
    lim2 = lim * lim
    for dx in (-1, 0, 1):
        for dz in (-1, 0, 1):
            for a, b in grid.get((gx + dx, gz + dz), ()):
                if (a - x) ** 2 + (b - z) ** 2 <= lim2:
                    return True
    return False


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--steps", default="data/quest_steps.json")
    ap.add_argument("--threshold", type=float, default=50.0, help="多少米内算覆盖")
    ap.add_argument("--out-doc", default="docs/任务脚本覆盖率明细.md")
    ap.add_argument("--out-json", default="data/coverage.json")
    args = ap.parse_args()

    root = Path(".")
    qs = json.load(Path(args.steps).open(encoding="utf-8"))
    grid, pts = build_index(root)

    rows = []
    for qid, q in qs["quests"].items():
        steps = [s for s in q["steps"] if s["kind"] == "path"]
        if not steps:
            continue
        hit = [s for s in steps if covered(grid, s["xyz"][0], s["xyz"][2], args.threshold)]
        rows.append({
            "quest": qid, "title": q["title"],
            "chapter": (f"{q['chapter_no']} {q['chapter_title']}".strip() if q["chapter"] else "世界任务"),
            "path_steps": len(steps), "covered": len(hit),
            "missing": [{"order": s["order"], "name": s["name"], "xyz": s["xyz"]}
                        for s in steps if s not in hit],
        })

    total_steps = sum(r["path_steps"] for r in rows)
    total_hit = sum(r["covered"] for r in rows)
    full = [r for r in rows if r["covered"] == r["path_steps"]]
    zero = [r for r in rows if r["covered"] == 0]

    lines = [
        "# 任务脚本覆盖率明细",
        "",
        f"路线点共 **{pts}** 个；可跑图步骤 **{total_steps}**，其中 **{total_hit}** 步在 "
        f"{args.threshold:.0f} 米内能找到路线点（{total_hit * 100 / total_steps:.1f}%）。",
        "",
        "只统计 `kind=path`。对话、剧情、脚本事件本来就不需要路线，",
        "把它们算进缺口会得出「脚本大量缺失」的错误结论。",
        "",
        "## 一、完全覆盖的任务",
        "",
        f"共 {len(full)} 个任务的每一个可跑图步骤都有路线。",
        "",
        "## 二、完全没覆盖的任务（按步骤数排序）",
        "",
        "| 任务 | 章节 | 可跑图步 |",
        "| --- | --- | ---: |",
    ]
    for r in sorted(zero, key=lambda r: -r["path_steps"])[:40]:
        lines.append(f"| {r['quest']} {r['title'][:18]} | {r['chapter'][:24]} | {r['path_steps']} |")

    lines += ["", "## 三、部分覆盖（按缺失步骤数排序）", "",
              "| 任务 | 章节 | 覆盖 | 缺的步骤 |", "| --- | --- | ---: | --- |"]
    partial = [r for r in rows if 0 < r["covered"] < r["path_steps"]]
    for r in sorted(partial, key=lambda r: -(r["path_steps"] - r["covered"]))[:40]:
        miss = "、".join(f"{m['order']}.{m['name'][:10]}" for m in r["missing"][:4])
        more = f" 等 {len(r['missing'])} 步" if len(r["missing"]) > 4 else ""
        lines.append(f"| {r['quest']} {r['title'][:14]} | {r['chapter'][:18]} | {r['covered']}/{r['path_steps']} | {miss}{more} |")

    out_doc = Path(args.out_doc)
    out_doc.parent.mkdir(parents=True, exist_ok=True)
    out_doc.write_text("\n".join(lines), encoding="utf-8")

    out_json = Path(args.out_json)
    out_json.write_text(json.dumps({
        "threshold_m": args.threshold,
        "route_points": pts,
        "path_steps": total_steps,
        "covered_steps": total_hit,
        "coverage": round(total_hit / total_steps, 3) if total_steps else 0,
        "quests_full": len(full), "quests_zero": len(zero), "quests_partial": len(partial),
        "detail": rows,
    }, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    print(json.dumps({
        "route_points": pts, "path_steps": total_steps, "covered": total_hit,
        "coverage": f"{total_hit * 100 / total_steps:.1f}%",
        "full": len(full), "zero": len(zero), "partial": len(partial),
        "doc": str(out_doc), "json": str(out_json),
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
