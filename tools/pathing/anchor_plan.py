#!/usr/bin/env python3
"""核对每段路线的传送锚点：官方录制数据里玩家实际用的是哪个，够不够近。

为什么需要：
  BGI 的 `genshin.tp(x, y)` 只给目标坐标，锚点是它自己按「最近」挑的。挑到 900m 外的
  七天神像，传送完就得横穿几百米山地，看起来像「传送异常」。
  官方客户端里有 342 段真人录制路线，每条都记着玩家当时从哪个锚点落地
  （`data/official_routes.json` 的 anchor 字段）。这份实测记录可以直接回答：
  这个目标点，真人是从哪个锚点过去的、落地后还要走多远。

判定：
  recorded  300m 内有录制记录 —— 按记录里的锚点坐标传送最稳
  nearby    tp.json 有 150m 内的锚点 —— 正常
  walk      最近锚点 150–500m —— 传送后要走一段
  far       500–1500m —— 大概率要横穿地形，建议实跑确认
  none      > 1500m —— 传送不可用，只能靠前一段路线接续或剧情推进

用法：
  python3 tools/pathing/anchor_plan.py
  python3 tools/pathing/anchor_plan.py --report docs/传送锚点核对.md
"""

from __future__ import annotations

import argparse
import json
import math
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OFFICIAL = ROOT / "data" / "official_routes.json"
BGI_TP = Path("/workspace/BGI-special/BetterGenshinImpact/GameTask/AutoTrackPath/Assets/tp.json")
REPORT = ROOT / "docs" / "传送锚点核对.md"
OUT = ROOT / "data" / "anchor_plan.json"

RECORD_MATCH = 300.0


def load_recorded() -> list[dict]:
    """录制数据里的 (锚点, 目标) 对，只取 Teyvat。"""
    if not OFFICIAL.exists():
        return []
    data = json.loads(OFFICIAL.read_text(encoding="utf-8"))
    groups = (data.get("maps") or {}).get("Teyvat") or {}
    out = []
    for bucket in ("routes", "anchor_links"):
        for item in groups.get(bucket) or []:
            anchor = item.get("anchor")
            target = item.get("target")
            if not anchor or not target:
                continue
            out.append({
                "name": item.get("name") or item.get("file"),
                "anchor": anchor,
                "target": target,
                "kind": bucket,
            })
    return out


def load_tp() -> dict[str, list[tuple[str, float, float]]]:
    """tp.json 的锚点，按地图分组。"""
    out: dict[str, list[tuple[str, float, float]]] = {}
    if not BGI_TP.exists():
        return out
    data = json.loads(BGI_TP.read_text(encoding="utf-8"))
    for scene in data.get("data") or []:
        name = scene.get("mapName")
        pts = []
        for p in scene.get("points") or []:
            pos = p.get("position") or []
            if len(pos) >= 3:
                pts.append((p.get("name") or p.get("type") or "锚点", float(pos[0]), float(pos[2])))
        if pts:
            out[name] = pts
    return out


def verdict_of(gap: float) -> str:
    if gap <= 150:
        return "nearby"
    if gap <= 500:
        return "walk"
    if gap <= 1500:
        return "far"
    return "none"


def main() -> int:
    parser = argparse.ArgumentParser(description="核对路线首点的传送锚点")
    parser.add_argument("--report", default=str(REPORT))
    parser.add_argument("--out", default=str(OUT))
    args = parser.parse_args()

    recorded = load_recorded()
    tp = load_tp()

    rows = []
    for path in sorted(ROOT.glob("js/**/pathing/*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        info = data.get("info") if isinstance(data.get("info"), dict) else {}
        map_name = info.get("map_name") or "Teyvat"
        positions = data.get("positions") or []
        if not positions:
            continue
        head = positions[0]
        x, y = float(head["x"]), float(head["y"])
        anchors = tp.get(map_name) or []

        best_name, best_gap = None, float("inf")
        for name, ax, ay in anchors:
            d = math.hypot(ax - x, ay - y)
            if d < best_gap:
                best_name, best_gap = name, d

        # 录制记录：目标点离本段首点够近，才认为玩家走的是同一个目标
        rec = None
        for item in recorded:
            tx, ty = item["target"]
            d = math.hypot(tx - x, ty - y)
            if d <= RECORD_MATCH and (rec is None or d < rec[0]):
                rec = (d, item)

        pure = str(path.relative_to(ROOT))
        rows.append({
            "file": pure,
            "map": map_name,
            "first": [round(x, 1), round(y, 1)],
            "record_target_gap": round(rec[0]) if rec else None,
            "record_name": rec[1]["name"] if rec else None,
            "record_anchor": rec[1]["anchor"] if rec else None,
            "nearest_anchor": best_name,
            "nearest_gap": round(best_gap) if best_name else None,
            "verdict": verdict_of(rec[0]) if rec else verdict_of(best_gap),
            "source": "recorded" if rec else "tp",
        })

    Path(args.out).write_text(json.dumps({"segments": rows}, ensure_ascii=False, indent=1), encoding="utf-8")

    counts = Counter(r["verdict"] for r in rows)
    decided = [r for r in rows if r["source"] == "recorded"]

    lines = [
        "# 传送锚点核对",
        "",
        f"共 {len(rows)} 段路线，对照 {len(recorded)} 条官方录制（锚点→目标）记录与 "
        f"`tp.json`。目标点 300m 内有录制记录的，直接采用真人用过的锚点。",
        "",
        "| 结论 | 段数 | 含义 |",
        "|---|---:|---|",
        f"| recorded | {len(decided)} | 有录制记录，按记录里的锚点走 |",
        f"| nearby | {counts.get('nearby', 0)} | tp.json 有 150m 内锚点 |",
        f"| walk | {counts.get('walk', 0)} | 落地后要走 150–500m |",
        f"| far | {counts.get('far', 0)} | 500–1500m，可能穿地形 |",
        f"| none | {counts.get('none', 0)} | > 1500m，传送不可用 |",
        "",
        "## 一、有官方录制记录的段（优先按记录传送）",
        "",
        "| 段 | 首点 | 录制路线 | 实测锚点 | 目标偏差 |",
        "|---|---|---|---|---:|",
    ]
    for r in sorted(decided, key=lambda x: x["record_target_gap"] or 0):
        ax, ay = r["record_anchor"]
        lines.append(f"| {r['file'].split('pathing/')[0].split('/')[-2]} / {r['file'].split('/')[-1]} | "
                     f"{r['first'][0]:.0f},{r['first'][1]:.0f} | {r['record_name']} | "
                     f"{ax:.0f},{ay:.0f} | {r['record_target_gap']}m |")

    lines += ["", "## 二、传送不可用 / 需要长距离步行的段", "",
              "这些段最该优先处理：不是脚本写错，是那一带确实没有可用的锚点。", "",
              "| 段 | 首点 | 最近锚点 | 距离 |", "|---|---|---|---:|"]
    for r in sorted([x for x in rows if x["verdict"] in ("far", "none")],
                    key=lambda x: -(x["nearest_gap"] or 0)):
        lines.append(f"| {r['file'].split('pathing/')[0].split('/')[-2]} / {r['file'].split('/')[-1]} | "
                     f"{r['first'][0]:.0f},{r['first'][1]:.0f} | {r['nearest_anchor'] or '无'} | "
                     f"{r['nearest_gap'] if r['nearest_gap'] is not None else '—'}m |")

    Path(args.report).parent.mkdir(parents=True, exist_ok=True)
    Path(args.report).write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"段 {len(rows)}｜录制命中 {len(decided)}｜" +
          "｜".join(f"{k} {counts.get(k, 0)}" for k in ("nearby", "walk", "far", "none")))
    print(f"报告 {args.report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
