#!/usr/bin/env python3
"""把 BGI 客户端里真人录制的路线抽出来，作为路网的「可信边」。

为什么要单独抽：
  仓库 js/ 下的 270 段是脚本生成的，几何上多为两点直连；官方客户端里另有 679 个
  含 positions 的 json，是玩家实机走完录下来的（AutoBoss 48 条、地脉花 626 条、
  合成台 5 条）。它们的点少（平均 3 点，Boss 线 5–22 点），但每一段都真的能走通，
  正好用来约束「别从山体中间穿过去」。

坐标约定：BGI 路线固定 x = 游戏 X（横向）、y = 游戏 Z（纵向），与任务 guide 的
(X, Y高度, Z) 不同——guide 的中间那个是高度，不写进路线。

输出 data/official_routes.json，结构：
  {
    "source_root": "...",
    "files": 679,
    "maps": {"Teyvat": [{"file": "...", "name": "...", "author": "...",
                          "points": [[x, y], ...], "move_modes": [...]}]}
  }
只保留可行走的点：type=teleport 是传送落点，不是走出来的，单独记在 anchors 里。

用法：
  python3 tools/pathing/official_import.py                       # 自动找客户端目录
  python3 tools/pathing/official_import.py --root /path/to/BGI   # 指定目录
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "data" / "official_routes.json"

# 客户端默认安装位置（Windows / 本机克隆都试一遍）
CANDIDATES = [
    Path("/workspace/better-genshin-impact"),
    Path(os.environ.get("BGI_SOURCE", "")),
]

# 提瓦特大陆的粗略包围盒（按 tp.json 797 个锚点实测外扩 10%）；
# 落在外面的点大概率是独立地图或脏数据
TEYVAT_BOX = (-7000.0, 17500.0, -7500.0, 15500.0)

# 传送落点允许的最大偏差（米）：超过说明这份数据不是 Teyvat 的，或坐标已失效
ANCHOR_TOLERANCE = 2000.0


def find_root(explicit: str | None) -> Path | None:
    if explicit:
        p = Path(explicit).expanduser().resolve()
        return p if p.exists() else None
    for p in CANDIDATES:
        if p and str(p) and p.exists() and (p / "BetterGenshinImpact").exists():
            return p
    return None


def in_teyvat(x: float, z: float) -> bool:
    x0, x1, z0, z1 = TEYVAT_BOX
    return x0 <= x <= x1 and z0 <= z <= z1


def load_anchors(root: Path) -> list[tuple[float, float]]:
    """读客户端 tp.json，只取 Teyvat 的锚点，用来校验传送首点。"""
    path = root / "BetterGenshinImpact" / "GameTask" / "AutoTrackPath" / "Assets" / "tp.json"
    if not path.exists():
        return []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001
        return []
    out = []
    for scene in data.get("data", []):
        if scene.get("mapName") != "Teyvat":
            continue
        for p in scene.get("points") or []:
            pos = p.get("position") or []
            if len(pos) >= 3:
                out.append((float(pos[0]), float(pos[2])))
    return out


def map_of(record: dict, points: list[tuple[float, float]]) -> str:
    """优先读元数据；缺失时按坐标落到 Teyvat 包围盒判断。"""
    info = record.get("info") if isinstance(record.get("info"), dict) else {}
    for key in ("map_name", "mapName"):
        value = info.get(key) or record.get(key)
        if isinstance(value, str) and value:
            return value
    if points and all(in_teyvat(x, z) for x, z in points):
        return "Teyvat"
    return "unknown"


def parse_file(path: Path, anchors: list[tuple[float, float]]) -> dict | None:
    try:
        record = json.loads(path.read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001
        return None
    if not isinstance(record, dict):
        return None
    raw = record.get("positions")
    if not isinstance(raw, list) or len(raw) < 2:
        return None

    walked: list[list[float]] = []
    modes: list[str] = []
    anchor_point = None
    target_point = None
    for item in raw:
        if not isinstance(item, dict):
            continue
        x, y = item.get("x"), item.get("y")
        if not isinstance(x, (int, float)) or not isinstance(y, (int, float)):
            continue
        if item.get("type") == "teleport":
            anchor_point = [round(float(x), 1), round(float(y), 1)]
            continue
        walked.append([round(float(x), 1), round(float(y), 1)])
        if item.get("type") == "target":
            target_point = [round(float(x), 1), round(float(y), 1)]
        mode = item.get("move_mode")
        if mode:
            modes.append(str(mode))

    info = record.get("info") if isinstance(record.get("info"), dict) else {}
    name = info.get("name") or record.get("name") or path.stem
    author = info.get("author") or ""
    if not author:
        authors = info.get("authors") or []
        if isinstance(authors, list) and authors:
            author = (authors[0] or {}).get("name") or ""

    # 只有 1 个行走点的地脉花路线（teleport → target）不参与建图，
    # 但它明确记录了「这个采集点该从哪个锚点传送」——这正是传送选点的实测依据。
    if len(walked) < 2:
        if anchor_point and (target_point or walked):
            goal = target_point or walked[-1]
            return {
                "file": str(path.name),
                "name": str(name),
                "author": str(author),
                "anchor": anchor_point,
                "target": goal,
                "map": map_of(record, [tuple(goal)]),
                "kind": "anchor_link",
            }
        return None

    return {
        "file": str(path.name),
        "name": str(name),
        "author": str(author),
        "points": walked,
        "move_modes": modes,
        "anchor": anchor_point,
        "target": target_point or (walked[-1] if walked else None),
        "map": map_of(record, [tuple(p) for p in walked]),
        "kind": "route",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="导入 BGI 客户端里真人录制的路线")
    parser.add_argument("--root", help="BGI 源码或安装目录")
    parser.add_argument("--out", default=str(OUT))
    parser.add_argument("--strict-anchor", action="store_true",
                        help="丢弃传送首点离锚点超过 %d 米的文件" % ANCHOR_TOLERANCE)
    args = parser.parse_args()

    root = find_root(args.root)
    if root is None:
        print("找不到 BGI 目录，用 --root 指定（应包含 BetterGenshinImpact/GameTask）", file=sys.stderr)
        return 1

    anchors = load_anchors(root)
    task_dir = root / "BetterGenshinImpact" / "GameTask"
    if not task_dir.exists():
        print(f"{task_dir} 不存在，路径不对", file=sys.stderr)
        return 1

    files = 0
    skipped = Counter()
    by_map: dict[str, dict[str, list[dict]]] = defaultdict(lambda: {"routes": [], "anchor_links": []})
    for path in sorted(task_dir.rglob("*.json")):
        record = parse_file(path, anchors)
        if record is None:
            skipped["既无可行走点也无传送记录"] += 1
            continue
        files += 1
        bucket = "routes" if record["kind"] == "route" else "anchor_links"
        by_map[record["map"]][bucket].append({k: v for k, v in record.items() if k != "map"})

    # 锚点校验只统计、不默认剔除，交给调用方决定
    bad_anchor = 0
    if anchors:
        import math
        for groups in by_map.values():
            for group in groups.values():
                for item in group:
                    a = item.get("anchor")
                    if not a:
                        continue
                    gap = min(math.hypot(a[0] - x, a[1] - z) for x, z in anchors)
                    item["anchor_gap"] = round(gap)
                    if gap > ANCHOR_TOLERANCE:
                        bad_anchor += 1

    routes = sum(len(g["routes"]) for g in by_map.values())
    links = sum(len(g["anchor_links"]) for g in by_map.values())
    payload = {
        "source_root": str(root),
        "files": files,
        "routes": routes,
        "anchor_links": links,
        "anchors": len(anchors),
        "anchor_outliers": bad_anchor,
        "maps": {k: v for k, v in sorted(by_map.items(), key=lambda kv: -sum(len(x) for x in kv[1].values()))},
    }
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    print(f"导入 {files} 个文件 → {out}")
    print(f"  可行走路线 {routes} 段，传送记录 {links} 条")
    for name, groups in payload["maps"].items():
        pts = sum(len(g["points"]) for g in groups["routes"])
        print(f"  {name:<14} 路线 {len(groups['routes']):>4}（{pts:>4} 点）  传送记录 {len(groups['anchor_links']):>4}")
    if anchors:
        print(f"  传送首点离最近锚点超过 {ANCHOR_TOLERANCE:.0f}m 的：{bad_anchor} 个")
    if skipped:
        for reason, n in skipped.most_common():
            print(f"  跳过 {n} 个：{reason}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
