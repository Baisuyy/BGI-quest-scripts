#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""修正 pathing/*.json 的 X/Y 颠倒问题。

背景：任务 guide 是 (X, Y=高度, Z)，BGI 路线文件应为 (x=X, y=Z)。
历史生成把两者写反了，导致路线落点整体镜像。

用法：
    python3 tools/pathing/fix_pathing_xy.py            # 只报告
    python3 tools/pathing/fix_pathing_xy.py --apply    # 真正改写
"""
import argparse
import csv
import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
JS = os.path.join(ROOT, "js")


def files():
    for dirpath, _dirnames, names in os.walk(JS):
        if os.path.basename(dirpath) != "pathing":
            continue
        for n in sorted(names):
            if n.endswith(".json"):
                yield os.path.join(dirpath, n)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="真正写回文件")
    ap.add_argument("--report", default="", help="输出 CSV 报告路径")
    args = ap.parse_args()

    rows, changed = [], 0
    for path in files():
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        pos = data.get("positions") or []
        if not pos:
            continue
        first = pos[0]
        rows.append({
            "file": os.path.relpath(path, ROOT),
            "points": len(pos),
            "old_x": round(first.get("x", 0), 1),
            "old_y": round(first.get("y", 0), 1),
            "new_x": round(first.get("y", 0), 1),
            "new_y": round(first.get("x", 0), 1),
        })
        if not args.apply:
            continue
        for p in pos:
            if "x" in p and "y" in p:
                p["x"], p["y"] = p["y"], p["x"]
        for t in data.get("teleport_points") or []:
            if "x" in t and "y" in t:
                t["x"], t["y"] = t["y"], t["x"]
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(data, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        changed += 1

    out = args.report or os.path.join(ROOT, "docs", "pathing_xy_report.csv")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8-sig", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()) if rows else ["file"])
        w.writeheader()
        w.writerows(rows)

    print(f"扫描 {len(rows)} 个路线文件，{'改写' if args.apply else '待改写'} {changed if args.apply else len(rows)} 个")
    print(f"报告：{out}")
    if not args.apply:
        print("确认无误后加 --apply 执行。")


if __name__ == "__main__":
    sys.exit(main())
