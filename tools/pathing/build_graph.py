#!/usr/bin/env python3
"""把仓库里所有 pathing 路线拼成一张可查询的路网。

用途：路线文件之间是孤立的，一段 2 公里的直线大概率穿山。把 291 个文件、9434 个点
按网格聚合成图以后，可以回答「从 A 到 B，沿着别人走过的路怎么走」，而不是两点直连。

输出：
  tools/pathing/graph.json   节点（网格坐标 + 世界坐标）+ 边（相邻点、距离）
  tools/pathing/graph.md     统计报告：节点数、连通分量、覆盖范围

只做离线建图，不改任何路线文件；BGI 端怎么用见 docs/路网与导航.md。
"""

from __future__ import annotations

import json
import math
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
JS = ROOT / "js"
OUT_JSON = Path(__file__).resolve().parent / "graph.json"
OUT_MD = Path(__file__).resolve().parent / "graph.md"

# 网格边长（米）。太小会让同一条路分成多段，太大则绕行距离失真。
CELL = 15.0


def load_routes() -> list[tuple[str, list[tuple[float, float]]]]:
    routes = []
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

        if len(points) >= 2:
            routes.append((str(path.relative_to(ROOT)), points))
    return routes


def cell_of(x: float, y: float) -> tuple[int, int]:
    return int(math.floor(x / CELL)), int(math.floor(y / CELL))


def main() -> int:
    routes = load_routes()
    if not routes:
        print("没有找到路线数据", file=sys.stderr)
        return 1

    # 网格节点：同一格里的点合并，取平均坐标
    sums: dict[tuple[int, int], list[float]] = defaultdict(lambda: [0.0, 0.0, 0])
    for _, points in routes:
        for x, y in points:
            node = sums[cell_of(x, y)]
            node[0] += x
            node[1] += y
            node[2] += 1

    nodes = {key: (v[0] / v[2], v[1] / v[2], v[2]) for key, v in sums.items()}

    # 边：同一文件里相邻点所在格之间连边，权重取实际距离，重复边保留最短
    edges: dict[tuple[tuple[int, int], tuple[int, int]], float] = {}
    for _, points in routes:
        for (x1, y1), (x2, y2) in zip(points, points[1:]):
            a, b = cell_of(x1, y1), cell_of(x2, y2)
            if a == b:
                continue
            key = (a, b) if a < b else (b, a)
            distance = math.hypot(x2 - x1, y2 - y1)
            if key not in edges or distance < edges[key]:
                edges[key] = distance

    # 连通分量
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

    adjacency = defaultdict(list)
    for (a, b), distance in edges.items():
        adjacency[a].append((b, round(distance, 1)))
        adjacency[b].append((a, round(distance, 1)))

    payload = {
        "cell_meters": CELL,
        "generated_from": "js/**/pathing/*.json",
        "routes": len(routes),
        "points": sum(len(p) for _, p in routes),
        "nodes": len(nodes),
        "edges": len(edges),
        "components": len(ranked),
        "nodes_index": sorted(nodes),
        "coords": {f"{k[0]},{k[1]}": [round(v[0], 1), round(v[1], 1)] for k, v in nodes.items()},
        "adjacency": {
            f"{a[0]},{a[1]}": [[f"{b[0]},{b[1]}", d] for b, d in sorted(adjacency[a], key=lambda t: t[1])]
            for a in adjacency
        },
    }
    OUT_JSON.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    total_km = sum(edges.values()) / 1000
    lines = [
        "# 路网统计",
        "",
        f"由 `tools/pathing/build_graph.py` 从 {len(routes)} 段路线生成，网格 {CELL:.0f}m。",
        "",
        "| 指标 | 值 |",
        "|---|---|",
        f"| 路线段 | {len(routes)} |",
        f"| 原始点 | {payload['points']} |",
        f"| 网格节点 | {len(nodes)} |",
        f"| 边 | {len(edges)} |",
        f"| 总边长 | {total_km:.1f} km |",
        f"| 连通分量 | {len(ranked)} |",
        "",
        "## 连通分量（前 10）",
        "",
        "| # | 节点数 | 占比 |",
        "|---:|---:|---:|",
    ]
    for i, comp in enumerate(ranked[:10], 1):
        lines.append(f"| {i} | {len(comp)} | {len(comp) / len(nodes) * 100:.1f}% |")

    lines += [
        "",
        "> 分量多说明各章节的路线彼此不连通：跨区域要走传送锚点，不能指望一条路网连到底。",
    ]
    OUT_MD.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"节点 {len(nodes)}，边 {len(edges)}，分量 {len(ranked)}，输出 {OUT_JSON.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
