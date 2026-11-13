#!/usr/bin/env python3
"""把仓库路线 + 官方录制路线拼成一张可查询的路网。

用途：路线文件之间是孤立的，一段 2 公里的直线大概率穿山。把点按网格聚合成图以后，
可以回答「从 A 到 B，沿着别人走过的路怎么走」，而不是两点直连。

两张来源，可信度不同：
  js/**/pathing/*.json   —— 脚本生成的，几何多为两点直连，可能有穿山段
  data/official_routes.json —— 从 BGI 客户端抽出来的真人录制路线（tools/pathing/official_import.py）

所以每条边都记 origin：
  1 = 至少有一条录制路线走过（真实可通行）
  0 = 只有生成路线走过（待实跑验证）
route.py 查路时对 origin=0 的边加代价惩罚，等价于「优先走别人走过的地方」。

按地图分组建图：Teyvat 与 TempleOfSpace 的坐标原点不同，混在一起会连出跨图的长边。

输出：
  tools/pathing/graph.json         主图（Teyvat），顶层 coords/adjacency 保持旧格式
  tools/pathing/graphs/<map>.json  其它地图
  tools/pathing/graph.md           统计报告

用法：
  python3 tools/pathing/build_graph.py                  # 只建 Teyvat，含官方录制
  python3 tools/pathing/build_graph.py --no-official    # 只用脚本路线
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
JS = ROOT / "js"
OFFICIAL = ROOT / "data" / "official_routes.json"
HERE = Path(__file__).resolve().parent
OUT_JSON = HERE / "graph.json"
OUT_DIR = HERE / "graphs"
OUT_MD = HERE / "graph.md"

# 网格边长（米）。太小会让同一条路分成多段，太大则绕行距离失真。
CELL = 15.0
PRIMARY_MAP = "Teyvat"

# 录制路线的相邻点可能隔几百米（玩家一次跑完一段），网格太细会把它拆成一串空洞。
# 这些点仍然直接连边，因为它们本身就是「这一段真的能走」的证据。
TRUSTED = 1
GENERATED = 0


def load_scripts() -> dict[str, list[tuple[str, list[tuple[float, float]]]]]:
    """脚本生成的路线，按 map_name 分组。"""
    out: dict[str, list[tuple[str, list[tuple[float, float]]]]] = defaultdict(list)
    for path in sorted(JS.rglob("pathing/*.json")):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception as e:  # noqa: BLE001
            print(f"[!] 跳过 {path.relative_to(ROOT)}：{e}", file=sys.stderr)
            continue

        points = []
        for item in data.get("positions", []):
            x, y = item.get("x"), item.get("y")
            if isinstance(x, (int, float)) and isinstance(y, (int, float)):
                points.append((float(x), float(y)))
        if len(points) < 2:
            continue

        info = data.get("info") if isinstance(data.get("info"), dict) else {}
        name = info.get("map_name") or data.get("map_name") or PRIMARY_MAP
        out[str(name)].append((str(path.relative_to(ROOT)), points))
    return out


def load_official() -> dict[str, list[tuple[str, list[tuple[float, float]]]]]:
    """官方录制路线，按地图分组。文件不存在时返回空，不报错。"""
    out: dict[str, list[tuple[str, list[tuple[float, float]]]]] = defaultdict(list)
    if not OFFICIAL.exists():
        return out
    try:
        data = json.loads(OFFICIAL.read_text(encoding="utf-8"))
    except Exception as e:  # noqa: BLE001
        print(f"[!] 官方路线解析失败：{e}", file=sys.stderr)
        return out

    for map_name, groups in (data.get("maps") or {}).items():
        for item in groups.get("routes") or []:
            points = [(float(p[0]), float(p[1])) for p in item.get("points") or []
                      if isinstance(p, (list, tuple)) and len(p) >= 2]
            if len(points) >= 2:
                label = f"official:{item.get('name') or item.get('file')}"
                out[str(map_name)].append((label, points))
    return out


def cell_of(x: float, y: float) -> tuple[int, int]:
    return int(math.floor(x / CELL)), int(math.floor(y / CELL))


def build_map(routes: list[tuple[str, list[tuple[float, float]], int]]) -> dict:
    """一张地图建一张图。routes 的第三项是 origin（1=录制，0=生成）。"""
    sums: dict[tuple[int, int], list[float]] = defaultdict(lambda: [0.0, 0.0, 0])
    for _, points, _origin in routes:
        for x, y in points:
            node = sums[cell_of(x, y)]
            node[0] += x
            node[1] += y
            node[2] += 1
    if not sums:
        return {}

    nodes = {key: (v[0] / v[2], v[1] / v[2], v[2]) for key, v in sums.items()}

    # 边：同一文件里相邻点所在格之间连边。重复边保留最短，origin 取两者中更可信的
    edges: dict[tuple[tuple[int, int], tuple[int, int]], list] = {}
    for _, points, origin in routes:
        for (x1, y1), (x2, y2) in zip(points, points[1:]):
            a, b = cell_of(x1, y1), cell_of(x2, y2)
            if a == b:
                continue
            key = (a, b) if a < b else (b, a)
            distance = math.hypot(x2 - x1, y2 - y1)
            cur = edges.get(key)
            if cur is None or distance < cur[0]:
                edges[key] = [distance, origin]
            elif origin > cur[1]:
                cur[1] = origin  # 有录制证据就升级，即使另一条更长

    parent: dict[tuple[int, int], tuple[int, int]] = {key: key for key in nodes}

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[rb] = ra

    for a, b in edges:
        union(a, b)

    components: dict[tuple[int, int], list[tuple[int, int]]] = defaultdict(list)
    for key in nodes:
        components[find(key)].append(key)
    ranked = sorted(components.values(), key=len, reverse=True)

    adjacency: dict[tuple[int, int], list[tuple[tuple[int, int], float, int]]] = defaultdict(list)
    for (a, b), (distance, origin) in edges.items():
        adjacency[a].append((b, round(distance, 1), origin))
        adjacency[b].append((a, round(distance, 1), origin))

    recorded = sum(1 for v in edges.values() if v[1] == TRUSTED)
    return {
        "routes": len(routes),
        "points": sum(len(p) for _, p, _o in routes),
        "nodes": len(nodes),
        "edges": len(edges),
        "recorded_edges": recorded,
        "components": len(ranked),
        "largest_component": len(ranked[0]) if ranked else 0,
        "coords": {f"{k[0]},{k[1]}": [round(v[0], 1), round(v[1], 1)] for k, v in nodes.items()},
        "adjacency": {
            f"{a[0]},{a[1]}": [[f"{b[0]},{b[1]}", d, o] for b, d, o in sorted(adjacency[a], key=lambda t: t[1])]
            for a in adjacency
        },
        "_ranked": ranked,
        "_edge_total": sum(v[0] for v in edges.values()),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="建路网")
    parser.add_argument("--no-official", action="store_true", help="不并入官方录制路线")
    args = parser.parse_args()

    scripts = load_scripts()
    official = {} if args.no_official else load_official()

    maps = sorted(set(scripts) | set(official))
    built: dict[str, dict] = {}
    for map_name in maps:
        merged = [(path, points, GENERATED) for path, points in scripts.get(map_name, [])]
        merged += [(path, points, TRUSTED) for path, points in official.get(map_name, [])]
        if not merged:
            continue
        graph = build_map(merged)
        if graph:
            built[map_name] = graph

    if not built:
        print("没有找到路线数据", file=sys.stderr)
        return 1

    primary = built.get(PRIMARY_MAP) or max(built.values(), key=lambda g: g["nodes"])

    # 顶层保持旧格式（route.py 直接读 coords / adjacency），新增 origin 字段不影响旧解析
    payload = {
        "cell_meters": CELL,
        "generated_from": {
            "scripts": "js/**/pathing/*.json",
            "official": "data/official_routes.json" if official else None,
            "official_files": sum(len(v) for v in official.values()),
        },
        "routes": primary["routes"],
        "points": primary["points"],
        "nodes": primary["nodes"],
        "edges": primary["edges"],
        "recorded_edges": primary["recorded_edges"],
        "components": primary["components"],
        "coords": primary["coords"],
        "adjacency": primary["adjacency"],
        "maps": {
            name: {k: v for k, v in graph.items() if not k.startswith("_")}
            for name, graph in built.items()
        },
    }
    OUT_JSON.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    OUT_DIR.mkdir(exist_ok=True)
    for name, graph in built.items():
        if name == PRIMARY_MAP:
            continue
        (OUT_DIR / f"{name}.json").write_text(
            json.dumps({k: v for k, v in graph.items() if not k.startswith("_")},
                       ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    lines = [
        "# 路网统计",
        "",
        f"由 `tools/pathing/build_graph.py` 生成，网格 {CELL:.0f}m。"
        f"{'已' if official else '未'}并入官方录制路线。",
        "",
        "| 地图 | 路线 | 原始点 | 网格节点 | 边 | 其中录制边 | 连通分量 | 最大分量 |",
        "|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for name, graph in sorted(built.items(), key=lambda kv: -kv[1]["nodes"]):
        lines.append(
            f"| {name} | {graph['routes']} | {graph['points']} | {graph['nodes']} | "
            f"{graph['edges']} | {graph['recorded_edges']} | {graph['components']} | {graph['largest_component']} |"
        )

    lines += ["", "## 边的来源", "",
              "| origin | 含义 | 用途 |", "|---|---|---|",
              "| 1 | 官方录制路线走过 | 真实可通行，A* 正常计价 |",
              "| 0 | 只有脚本生成的直线 | 待实跑验证，A* 加代价惩罚 |"]

    total_km = primary.get("_edge_total", 0) / 1000
    lines += ["", f"主图 Teyvat 总边长 {total_km:.1f} km。", "",
              "> 分量多说明各章节路线彼此不连通：跨区域要走传送锚点，不能指望一条路网连到底。"]
    OUT_MD.write_text("\n".join(lines) + "\n", encoding="utf-8")

    for name, graph in sorted(built.items(), key=lambda kv: -kv[1]["nodes"]):
        print(f"{name:<16} 节点 {graph['nodes']:>5}  边 {graph['edges']:>5}"
              f"（录制 {graph['recorded_edges']:>4}）  分量 {graph['components']:>3}")
    print(f"输出 {OUT_JSON.name}" + (f" + {len(built) - 1} 张分图" if len(built) > 1 else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
