#!/usr/bin/env python3
"""在路网上查一条从 A 到 B 的走法，输出可直接跑的 pathing json。

思路跟导航软件一样：
  1. 起点/终点吸附到最近的路网节点（超过 300m 视为没有路网覆盖，直接拒绝）
  2. 在路网上跑 A*，代价是走过的米数
  3. 首尾用直线接驳，中间沿别人走过的点走

用法：
  python3 tools/pathing/route.py --from 1200,3400 --to 2400,3900
  python3 tools/pathing/route.py --from 1200,3400 --to 2400,3900 --out /tmp/route.json
"""

from __future__ import annotations

import argparse
import heapq
import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
GRAPH = HERE / "graph.json"

SNAP_LIMIT = 300.0
# origin=0 的边只有脚本生成的直线做背书，加价让 A* 优先走官方录制的真实路径
GENERATED_PENALTY = 1.35


def load_graph() -> tuple[dict[tuple[int, int], tuple[float, float]], dict[tuple[int, int], list[tuple[tuple[int, int], float]]]]:
    data = json.loads(GRAPH.read_text(encoding="utf-8"))
    coords = {}
    for key, xy in data["coords"].items():
        gx, gy = key.split(",")
        coords[(int(gx), int(gy))] = (xy[0], xy[1])

    adjacency = {}
    for key, neighbours in data["adjacency"].items():
        gx, gy = key.split(",")
        node = (int(gx), int(gy))
        adjacency[node] = []
        for item in neighbours:
            nx, ny = item[0].split(",")
            origin = item[2] if len(item) > 2 else 0
            adjacency[node].append(((int(nx), int(ny)), item[1], origin))
    return coords, adjacency


def nearest(coords: dict, x: float, y: float) -> tuple[tuple[int, int], float]:
    best, best_distance = None, float("inf")
    for node, (nx, ny) in coords.items():
        distance = math.hypot(nx - x, ny - y)
        if distance < best_distance:
            best, best_distance = node, distance
    return best, best_distance


def astar(coords, adjacency, start, goal):
    def heuristic(node):
        x1, y1 = coords[node]
        x2, y2 = coords[goal]
        return math.hypot(x1 - x2, y1 - y2)

    open_heap = [(heuristic(start), 0.0, start)]
    came_from: dict[tuple[int, int], tuple[int, int] | None] = {start: None}
    cost_so_far = {start: 0.0}

    while open_heap:
        _, cost, current = heapq.heappop(open_heap)
        if current == goal:
            break
        if cost > cost_so_far.get(current, float("inf")):
            continue

        for neighbour, weight, origin in adjacency.get(current, []):
            new_cost = cost + weight * (1.0 if origin == 1 else GENERATED_PENALTY)
            if new_cost < cost_so_far.get(neighbour, float("inf")):
                cost_so_far[neighbour] = new_cost
                came_from[neighbour] = current
                heapq.heappush(open_heap, (new_cost + heuristic(neighbour), new_cost, neighbour))

    if goal not in came_from:
        return None, float("inf")

    path = [goal]
    while path[-1] != start:
        path.append(came_from[path[-1]])
    path.reverse()
    return path, cost_so_far[goal]


def simplify(points: list[tuple[float, float]], tolerance: float = 2.0) -> list[tuple[float, float]]:
    """Douglas-Peucker，路网节点本身较密，压一下避免导出几百个点。"""
    if len(points) < 3:
        return points

    def point_to_line(p, a, b):
        dx, dy = b[0] - a[0], b[1] - a[1]
        norm = dx * dx + dy * dy
        if norm < 1e-9:
            return math.hypot(p[0] - a[0], p[1] - a[1])
        t = max(0.0, min(1.0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / norm))
        return math.hypot(p[0] - (a[0] + t * dx), p[1] - (a[1] + t * dy))

    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]
    while stack:
        first, last = stack.pop()
        if last <= first + 1:
            continue
        worst, index = 0.0, -1
        for i in range(first + 1, last):
            distance = point_to_line(points[i], points[first], points[last])
            if distance > worst:
                worst, index = distance, i
        if worst > tolerance and index > 0:
            keep[index] = True
            stack.append((first, index))
            stack.append((index, last))
    return [p for p, k in zip(points, keep) if k]


def main() -> int:
    parser = argparse.ArgumentParser(description="在已建路网上查路径")
    parser.add_argument("--from", dest="start", required=True, help="起点 x,y")
    parser.add_argument("--to", dest="goal", required=True, help="终点 x,y")
    parser.add_argument("--out", help="导出 pathing json 的路径")
    parser.add_argument("--name", default="路网生成路线", help="导出路线的名字")
    # 坐标可能是负数（蒙德、须弥一片都在原点西南），argparse 会把 "-1393,7249"
    # 当成选项，必须掰成 --from=-1393,7249 才能用
    argv = []
    raw = sys.argv[1:]
    i = 0
    while i < len(raw):
        if raw[i] in ("--from", "--to", "--out", "--name") and i + 1 < len(raw):
            argv.append(f"{raw[i]}={raw[i + 1]}")
            i += 2
        else:
            argv.append(raw[i])
            i += 1
    args = parser.parse_args(argv)

    if not GRAPH.exists():
        print("先跑 build_graph.py 生成 graph.json", file=sys.stderr)
        return 1

    sx, sy = (float(v) for v in args.start.split(","))
    gx, gy = (float(v) for v in args.goal.split(","))

    coords, adjacency = load_graph()
    start_node, start_gap = nearest(coords, sx, sy)
    goal_node, goal_gap = nearest(coords, gx, gy)
    print(f"起点吸附 {start_gap:.0f}m，终点吸附 {goal_gap:.0f}m")

    if start_gap > SNAP_LIMIT or goal_gap > SNAP_LIMIT:
        print(f"超出路网覆盖（吸附距离 > {SNAP_LIMIT:.0f}m），这一段只能直连或补录制", file=sys.stderr)
        return 2

    path, length = astar(coords, adjacency, start_node, goal_node)
    if path is None:
        print("两点不在同一连通分量：中间需要传送锚点，不能靠走", file=sys.stderr)
        return 3

    points = [(sx, sy)] + [coords[node] for node in path] + [(gx, gy)]
    points = simplify(points)
    direct = math.hypot(gx - sx, gy - sy)
    walked = sum(math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(points, points[1:]))

    print(f"路网点 {len(path)} 个 → 简化后 {len(points)} 点")
    print(f"直线 {direct:.0f}m，沿路网 {walked:.0f}m，绕行系数 {walked / direct:.2f}")

    if args.out:
        payload = {
            "info": {
                "name": args.name,
                "description": f"路网 A* 生成，直线 {direct:.0f}m / 路网 {walked:.0f}m",
                "author": "bgi-special",
                "version": "1.0",
                "bgi_version": "0.67.0",
                "type": "collect",
                "map_name": "Teyvat",
                "map_match_method": "TemplateMatch",
                "tags": [],
                "enable_monster_loot_split": False,
            },
            "positions": [
                {
                    "id": i + 1,
                    "x": round(x, 1),
                    "y": round(y, 1),
                    "type": "path",
                    "move_mode": "walk",
                    "action": "",
                    "action_params": "",
                }
                for i, (x, y) in enumerate(points)
            ],
            "special_meta": {
                "source": "tools/pathing/route.py",
                "direct_meters": round(direct, 1),
                "walked_meters": round(walked, 1),
                "nodes": len(path),
            },
        }
        Path(args.out).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"已写出 {args.out}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
