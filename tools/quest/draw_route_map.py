#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把剧情脚本的路线画成 SVG 地图。

一张图对应一个坐标系：提瓦特和独立地图（空之神殿）的坐标量级差两个数量级，
画在一起只会变成一堆重叠的点。所以按 map_name 分开出图。

图上保留原始信息，不做平滑：8 米一个采样点，弯到哪画到哪——这样「这条路线
其实是首尾直线」一眼就能看出来。

用法：
    python tools/quest/draw_route_map.py
    python tools/quest/draw_route_map.py --src js/原始 --out docs/路径地图
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent

PALETTE = ["#4cc2ff", "#6ddc8f", "#ffcc66", "#ff8a8a", "#c9a0ff"]
BG = "#12151b"
GRID = "#232a35"
AXIS = "#39414f"
TEXT = "#e7eaf0"
MUTED = "#8b93a1"


def collect(task_dir: Path) -> dict[str, list[dict]]:
    """map_name → 段列表。段信息来自 main.js 的 SEGMENTS，点位来自 pathing/*.json。"""
    import re

    main_js = (task_dir / "main.js").read_text(encoding="utf-8-sig")
    m = re.search(r"const SEGMENTS = (\[.*?\]);", main_js, re.S)
    if not m:
        return {}

    out: dict[str, list[dict]] = {}
    for seg in json.loads(m.group(1)):
        f = task_dir / seg["file"]
        if not f.is_file():
            continue
        data = json.loads(f.read_text(encoding="utf-8-sig"))
        pts = [(p["x"], p["y"]) for p in data.get("positions") or []]
        if not pts:
            continue
        map_name = seg.get("map") or data.get("info", {}).get("map_name") or "Teyvat"
        out.setdefault(map_name, []).append(
            {
                "seq": seg["seq"],
                "label": seg.get("label", ""),
                "points": pts,
                "file": seg["file"],
            }
        )
    return out


def nice_step(span: float) -> int:
    for s in (20, 50, 100, 200, 250, 500, 1000, 2000):
        if span / s <= 10:
            return s
    return 5000


def draw(title: str, subtitle: str, segs: list[dict], out_path: Path) -> None:
    xs = [p[0] for s in segs for p in s["points"]]
    ys = [p[1] for s in segs for p in s["points"]]
    minx, maxx, miny, maxy = min(xs), max(xs), min(ys), max(ys)
    # 留一点边，点不会贴在框线上
    pad_x, pad_y = (maxx - minx) * 0.06 + 4, (maxy - miny) * 0.06 + 4
    minx, maxx, miny, maxy = minx - pad_x, maxx + pad_x, miny - pad_y, maxy + pad_y
    spanx, spany = max(maxx - minx, 1.0), max(maxy - miny, 1.0)

    W = 820
    # 底部依次是：x 轴刻度、图例、比例尺，按段数留够高度
    pad_l, pad_r, pad_t = 64, 32, 92
    pad_b = 78 + 21 * len(segs)
    inner_w = W - pad_l - pad_r
    # 全是单点段时没必要铺一屏高，压扁一点，标签也不至于被拉得老远
    only_points = all(len(x["points"]) == 1 for x in segs)
    inner_h = min(380 if only_points else 1180, max(300, inner_w * spany / spanx))
    H = inner_h + pad_t + pad_b
    k = min(inner_w / spanx, inner_h / spany)

    def px(x: float) -> float:
        return pad_l + (x - minx) * k + (inner_w - spanx * k) / 2

    def py(y: float) -> float:
        # 屏幕 y 向下，地图 y 向上
        return pad_t + (maxy - y) * k + (inner_h - spany * k) / 2

    step = nice_step(max(spanx, spany))
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H:.0f}" '
        f'viewBox="0 0 {W} {H:.0f}" font-family="Microsoft YaHei, Noto Sans CJK SC, sans-serif">',
        f'<rect width="{W}" height="{H:.0f}" fill="{BG}"/>',
        f'<text x="{pad_l}" y="38" fill="{TEXT}" font-size="19" font-weight="700">{title}</text>',
        f'<text x="{pad_l}" y="62" fill="{MUTED}" font-size="12.5">{subtitle}</text>',
    ]

    # 网格
    gx = math.ceil(minx / step) * step
    while gx <= maxx:
        x = px(gx)
        parts.append(f'<line x1="{x:.1f}" y1="{pad_t}" x2="{x:.1f}" y2="{pad_t+inner_h}" stroke="{GRID}" stroke-width="1"/>')
        parts.append(f'<text x="{x:.1f}" y="{pad_t+inner_h+18}" fill="{MUTED}" font-size="10.5" text-anchor="middle">{int(gx)}</text>')
        gx += step
    gy = math.ceil(miny / step) * step
    while gy <= maxy:
        y = py(gy)
        parts.append(f'<line x1="{pad_l}" y1="{y:.1f}" x2="{pad_l+inner_w}" y2="{y:.1f}" stroke="{GRID}" stroke-width="1"/>')
        parts.append(f'<text x="{pad_l-10}" y="{y+3.5:.1f}" fill="{MUTED}" font-size="10.5" text-anchor="end">{int(gy)}</text>')
        gy += step

    # 路线
    for i, seg in enumerate(segs):
        color = PALETTE[i % len(PALETTE)]
        pts = seg["points"]

        if len(pts) == 1:
            x, y = px(pts[0][0]), py(pts[0][1])
            parts.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="9" fill="none" stroke="{color}" stroke-width="1.6" stroke-dasharray="3 3"/>')
            # 两个提示点常常挨在一起，标签左右交替，免得叠成一团
            side = 1 if i % 2 == 0 else -1
            anchor_attr = "start" if side > 0 else "end"
            parts.append(
                f'<text x="{x + 15 * side:.1f}" y="{y + 4:.1f}" fill="{color}" font-size="11.5" '
                f'text-anchor="{anchor_attr}">段 {seg["seq"]} · 单点提示位</text>'
            )
            continue

        d = " ".join(
            ("M" if j == 0 else "L") + f"{px(p[0]):.1f},{py(p[1]):.1f}" for j, p in enumerate(pts)
        )
        parts.append(f'<path d="{d}" fill="none" stroke="{color}" stroke-width="2.4" stroke-linejoin="round" stroke-linecap="round" opacity="0.95"/>')

        # 起终点
        sx, sy = px(pts[0][0]), py(pts[0][1])
        ex, ey = px(pts[-1][0]), py(pts[-1][1])
        parts.append(f'<circle cx="{sx:.1f}" cy="{sy:.1f}" r="5" fill="{BG}" stroke="{color}" stroke-width="2.4"/>')
        parts.append(f'<rect x="{ex-5:.1f}" y="{ey-5:.1f}" width="10" height="10" fill="{color}"/>')

        # 段号贴在中点
        mid = pts[len(pts) // 2]
        mx, my = px(mid[0]), py(mid[1])
        label = f'{seg["seq"]}'
        parts.append(f'<circle cx="{mx:.1f}" cy="{my:.1f}" r="11" fill="{color}" opacity="0.18"/>')
        parts.append(f'<text x="{mx:.1f}" y="{my+4:.1f}" fill="{color}" font-size="11.5" font-weight="700" text-anchor="middle">{label}</text>')

    # 比例尺：挑一个 70–150px 的长度，别缩成一条短线
    meters = 100
    for cand in (100, 200, 500, 1000, 50, 20):
        if 70 <= cand * k <= 160:
            meters = cand
            break
    else:
        meters = max((m for m in (10, 20, 50, 100, 200, 500, 1000) if m * k <= 160), default=10)
    bar = meters * k
    bx, by = pad_l, H - 18
    parts += [
        f'<line x1="{bx}" y1="{by}" x2="{bx+bar:.1f}" y2="{by}" stroke="{TEXT}" stroke-width="2"/>',
        f'<line x1="{bx}" y1="{by-4}" x2="{bx}" y2="{by+4}" stroke="{TEXT}" stroke-width="2"/>',
        f'<line x1="{bx+bar:.1f}" y1="{by-4}" x2="{bx+bar:.1f}" y2="{by+4}" stroke="{TEXT}" stroke-width="2"/>',
        f'<text x="{bx+bar/2:.1f}" y="{by-9}" fill="{MUTED}" font-size="11" text-anchor="middle">{meters} m</text>',
    ]

    # 图例
    lx, ly = pad_l, pad_t + inner_h + 48
    for i, seg in enumerate(segs):
        color = PALETTE[i % len(PALETTE)]
        n = len(seg["points"])
        parts.append(f'<rect x="{lx}" y="{ly-8}" width="16" height="3" fill="{color}"/>')
        parts.append(
            f'<text x="{lx+22}" y="{ly}" fill="{MUTED}" font-size="11.5">'
            f'段 {seg["seq"]} · {n} 点 · {seg["label"][:26]}</text>'
        )
        ly += 20

    parts.append("</svg>")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text("\n".join(parts), encoding="utf-8")
    print(f"{out_path.relative_to(ROOT)}  {W}x{H:.0f}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", type=Path, default=ROOT / "js" / "原始")
    ap.add_argument("--out", type=Path, default=ROOT / "docs" / "路径地图")
    args = ap.parse_args()

    tasks = sorted(p for p in args.src.rglob("main.js"))
    if not tasks:
        print(f"{args.src} 下没有脚本")
        return 1

    for main_js in tasks:
        task_dir = main_js.parent
        by_map = collect(task_dir)
        for map_name, segs in by_map.items():
            segs.sort(key=lambda s: s["seq"])
            total = sum(len(s["points"]) for s in segs)
            title = f"{task_dir.name} · {map_name}"
            subtitle = (
                f"{len(segs)} 段 · {total} 点 · 坐标单位：米 · "
                f"空心圈是任务提示点，不是路线"
            )
            draw(title, subtitle, segs, args.out / f"{task_dir.name.split('-')[0]}-{map_name}.svg")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
