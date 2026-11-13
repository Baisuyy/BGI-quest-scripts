#!/usr/bin/env python3
"""用「真人录制路线」校验并优化脚本生成的路线。

脚本里的 270 段路线是两点/多点直连生成的，几何上最短，但经常穿山过河。
BGI 客户端里另有 342 段真人录制路线（`data/official_routes.json`），每段都真的走通过。
这个工具做三件事：

1. 逐段体检：原段多长、直线多长、离最近的录制路线多远（穿山嫌疑）
2. 给出候选：在「录制边优先」的路网上用 A* 重算，若绕行可接受就作为候选
3. 只改有把握的：`--apply` 时仅替换满足全部安全条件的段，其余只报告

安全条件（任一不满足就不动原文件）：
  · 起终点都能吸附到路网，且距离 ≤ 60m
  · 新路线长度 ≤ 原长 × 1.3（绕太多说明路网把它带偏了）
  · 新旧路线最大偏离 ≥ 25m（否则改了也没意义）
  · 原段 ≥ 150m（短段本来就直，不值得动）

用法：
  python3 tools/pathing/optimize_routes.py                       # 只报告
  python3 tools/pathing/optimize_routes.py --apply               # 写回候选段
  python3 tools/pathing/optimize_routes.py --report docs/x.md    # 指定报告路径
"""

from __future__ import annotations

import argparse
import heapq
import json
import math
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GRAPH = Path(__file__).resolve().parent / "graph.json"
OFFICIAL = ROOT / "data" / "official_routes.json"
REPORT = ROOT / "docs" / "路线优化报告.md"

SNAP_LIMIT = 60.0
MAX_DETOUR = 1.30
# 新路线不应比原来的短太多：任务路线绕一点通常是有原因的（绕开营地、沿路走），
# 短了 40% 以上更像是路网把它带到别的任务的近路上去了。
MIN_KEEP = 0.60
MIN_SHIFT = 25.0
MIN_LENGTH = 150.0
# 首末点距离小于「段长 5%」或 50m，视为闭环（采集环线），不能按 A→B 重算
LOOP_TOLERANCE = 0.05
# 生成边（origin=0）的代价放大：同等距离下优先走录制边
GENERATED_PENALTY = 1.35
# 新路线至少要有一半长度走在录制边上，否则只是换了一条生成直线
MIN_RECORD_RATIO = 0.50
# 新路线离录制线要比原来近这么多米，才算「更贴近真人路径」
MIN_GAP_GAIN = 20.0


def load_graph():
    data = json.loads(GRAPH.read_text(encoding="utf-8"))
    coords, adjacency = {}, {}
    for key, xy in data["coords"].items():
        gx, gy = key.split(",")
        coords[(int(gx), int(gy))] = (xy[0], xy[1])
    for key, neighbours in data["adjacency"].items():
        gx, gy = key.split(",")
        node = (int(gx), int(gy))
        adjacency[node] = []
        for item in neighbours:
            nx, ny = item[0].split(",")
            origin = item[2] if len(item) > 2 else 0
            adjacency[node].append(((int(nx), int(ny)), item[1], origin))
    return coords, adjacency


def nearest(coords, x, y):
    best, gap = None, float("inf")
    for node, (nx, ny) in coords.items():
        d = math.hypot(nx - x, ny - y)
        if d < gap:
            best, gap = node, d
    return best, gap


def astar(coords, adjacency, start, goal):
    def h(node):
        x1, y1 = coords[node]
        x2, y2 = coords[goal]
        return math.hypot(x1 - x2, y1 - y2)

    heap = [(h(start), 0.0, start)]
    came = {start: None}
    cost = {start: 0.0}
    while heap:
        _, c, cur = heapq.heappop(heap)
        if cur == goal:
            break
        if c > cost.get(cur, float("inf")):
            continue
        for nxt, weight, origin in adjacency.get(cur, []):
            step = weight * (1.0 if origin == 1 else GENERATED_PENALTY)
            nc = c + step
            if nc < cost.get(nxt, float("inf")):
                cost[nxt] = nc
                came[nxt] = cur
                heapq.heappush(heap, (nc + h(nxt), nc, nxt))
    if goal not in came:
        return None
    path = [goal]
    while path[-1] != start:
        path.append(came[path[-1]])
    return list(reversed(path))


def simplify(points, tolerance=2.5):
    if len(points) < 3:
        return points
    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]
    while stack:
        first, last = stack.pop()
        if last <= first + 1:
            continue
        ax, ay = points[first]
        bx, by = points[last]
        dx, dy = bx - ax, by - ay
        norm = dx * dx + dy * dy
        worst, index = 0.0, -1
        for i in range(first + 1, last):
            px, py = points[i]
            if norm < 1e-9:
                d = math.hypot(px - ax, py - ay)
            else:
                t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / norm))
                d = math.hypot(px - (ax + t * dx), py - (ay + t * dy))
            if d > worst:
                worst, index = d, i
        if worst > tolerance and index > 0:
            keep[index] = True
            stack += [(first, index), (index, last)]
    return [p for p, k in zip(points, keep) if k]


def length_of(points):
    return sum(math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(points, points[1:]))


def max_deviation(old, new):
    """旧路线上的点到新路线的最短距离，取最大值。"""
    if not new:
        return 0.0
    worst = 0.0
    for px, py in old:
        best = min(math.hypot(px - nx, py - ny) for nx, ny in new)
        worst = max(worst, best)
    return worst


def edge_stats(adjacency, nodes):
    """沿节点串统计：总长、录制边长度占比。"""
    total = recorded = 0.0
    for a, b in zip(nodes, nodes[1:]):
        best = None
        for nxt, weight, origin in adjacency.get(a, []):
            if nxt == b and (best is None or weight < best[0]):
                best = (weight, origin)
        if best is None:
            continue
        total += best[0]
        if best[1] == 1:
            recorded += best[0]
    return total, (recorded / total if total > 0 else 0.0)


def load_official_points():
    """所有录制点，用来算「离最近的真人路线多远」。"""
    pts = []
    if OFFICIAL.exists():
        data = json.loads(OFFICIAL.read_text(encoding="utf-8"))
        for groups in (data.get("maps") or {}).values():
            for item in groups.get("routes") or []:
                for x, y in item.get("points") or []:
                    pts.append((float(x), float(y)))
    return pts


def nearest_record_gap(points, recorded):
    """一段路线离录制路线的最大「无人走过」距离：取段上每个点到最近录制点的距离的最大值。"""
    if not recorded:
        return float("inf")
    if len(points) > 40:
        step = max(1, len(points) // 40)
        sample = points[::step]
    else:
        sample = points
    worst = 0.0
    for x, y in sample:
        d = min((x - rx) ** 2 + (y - ry) ** 2 for rx, ry in recorded)
        worst = max(worst, math.sqrt(d))
    return worst


def main() -> int:
    parser = argparse.ArgumentParser(description="用录制路线优化脚本路线")
    parser.add_argument("--apply", action="store_true", help="把候选写回 pathing/*.json")
    parser.add_argument("--report", default=str(REPORT))
    parser.add_argument("--limit", type=int, default=0, help="只处理前 N 段（调试用）")
    args = parser.parse_args()

    if not GRAPH.exists():
        print("先跑 build_graph.py", flush=True)
        return 1

    coords, adjacency = load_graph()
    recorded = load_official_points()
    files = sorted(ROOT.glob("js/**/pathing/*.json"))
    if args.limit:
        files = files[:args.limit]

    rows = []
    applied = 0
    for path in files:
        data = json.loads(path.read_text(encoding="utf-8"))
        info = data.get("info") if isinstance(data.get("info"), dict) else {}
        map_name = info.get("map_name") or "Teyvat"
        positions = data.get("positions") or []
        pts = [(float(p["x"]), float(p["y"])) for p in positions
               if isinstance(p.get("x"), (int, float)) and isinstance(p.get("y"), (int, float))]
        if len(pts) < 2:
            if len(pts) == 1 and map_name == "Teyvat":
                rows.append({
                    "file": str(path.relative_to(ROOT)), "map": map_name, "points": 1,
                    "length": 0.0, "direct": 0.0, "ratio": 1.0,
                    "record_gap": nearest_record_gap(pts, recorded),
                    "status": "single", "reason": "单点段（只标目标位置），无路径可优化",
                    "new_length": None, "shift": None,
                })
            continue

        old_len = length_of(pts)
        direct = math.hypot(pts[-1][0] - pts[0][0], pts[-1][1] - pts[0][1])
        record_gap = nearest_record_gap(pts, recorded) if map_name == "Teyvat" else float("inf")

        row = {
            "file": str(path.relative_to(ROOT)),
            "map": map_name,
            "points": len(pts),
            "length": old_len,
            "direct": direct,
            "ratio": old_len / direct if direct > 1 else 1.0,
            "record_gap": record_gap,
            "status": "skip",
            "reason": "",
            "new_length": None,
            "shift": None,
        }

        if map_name != "Teyvat":
            row["reason"] = "独立地图，坐标原点不同，路网不适用"
            rows.append(row)
            continue
        if old_len < MIN_LENGTH:
            row["reason"] = f"段太短（{old_len:.0f}m < {MIN_LENGTH:.0f}m）"
            rows.append(row)
            continue
        # 采集环线：首末重合，A* 会直接返回 0 长度，必须单独处理
        if direct < max(50.0, old_len * LOOP_TOLERANCE):
            row["status"] = "loop"
            row["reason"] = f"闭环路线（首末相距 {direct:.0f}m / 全长 {old_len:.0f}m）"
            rows.append(row)
            continue

        sn, sg = nearest(coords, *pts[0])
        gn, gg = nearest(coords, *pts[-1])
        if sg > SNAP_LIMIT or gg > SNAP_LIMIT:
            row["status"] = "no-coverage"
            row["reason"] = f"路网覆盖不足（起 {sg:.0f}m / 终 {gg:.0f}m）"
            rows.append(row)
            continue

        node_path = astar(coords, adjacency, sn, gn)
        if node_path is None:
            row["status"] = "no-coverage"
            row["reason"] = "起终点不同连通分量，需要传送锚点"
            rows.append(row)
            continue

        candidate = simplify([pts[0]] + [coords[n] for n in node_path] + [pts[-1]])
        new_len = length_of(candidate)
        shift = max_deviation(pts, candidate)
        new_gap = nearest_record_gap(candidate, recorded)
        _, record_ratio = edge_stats(adjacency, node_path)
        row["new_length"] = new_len
        row["shift"] = shift
        row["record_ratio"] = round(record_ratio, 2)
        row["new_gap"] = new_gap

        if new_len > old_len * MAX_DETOUR:
            row["reason"] = f"绕行过多（{new_len:.0f}m vs 原 {old_len:.0f}m）"
            rows.append(row)
            continue
        if new_len < old_len * MIN_KEEP:
            row["reason"] = f"新路线过短（{new_len:.0f}m vs 原 {old_len:.0f}m），疑似走上别的任务近路"
            rows.append(row)
            continue
        # 核心判据：新路线必须真的更贴近真人走过的路，否则只是横向平移了一条直线
        if record_ratio < MIN_RECORD_RATIO:
            row["reason"] = f"录制边只占 {record_ratio * 100:.0f}%，换的是另一条生成路线"
            rows.append(row)
            continue
        if new_gap > record_gap - MIN_GAP_GAIN:
            row["reason"] = f"离录制线没有更近（{record_gap:.0f}m → {new_gap:.0f}m）"
            rows.append(row)
            continue
        if shift < MIN_SHIFT:
            row["reason"] = f"差异太小（{shift:.0f}m）"
            rows.append(row)
            continue

        row["status"] = "candidate"
        row["reason"] = (f"路网 {new_len:.0f}m / 原 {old_len:.0f}m · 偏移 {shift:.0f}m · "
                         f"录制边 {record_ratio * 100:.0f}% · 离录制线 {record_gap:.0f}→{new_gap:.0f}m")

        if args.apply:
            # 只换中间点：首末点保持原样，type/action 等字段沿用原文件的约定
            head, tail = positions[0], positions[-1]
            mesh = candidate[1:-1]
            rebuilt = [dict(head)]
            for i, (x, y) in enumerate(mesh, start=2):
                rebuilt.append({
                    "id": i, "x": round(x, 1), "y": round(y, 1),
                    "type": "path", "move_mode": "walk", "action": "", "action_params": "",
                })
            tail = dict(tail)
            tail["id"] = len(rebuilt) + 1
            rebuilt.append(tail)
            data["positions"] = rebuilt
            meta = data.get("special_meta") if isinstance(data.get("special_meta"), dict) else {}
            meta.update({
                "source": "tools/pathing/optimize_routes.py",
                "old_length": round(old_len, 1),
                "new_length": round(new_len, 1),
                "shift": round(shift, 1),
                "record_gap": round(record_gap, 1) if record_gap != float("inf") else None,
            })
            data["special_meta"] = meta
            path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            applied += 1

        rows.append(row)

    # 报告
    candidates = [r for r in rows if r["status"] == "candidate"]
    no_cov = [r for r in rows if r["status"] == "no-coverage"]
    loops = [r for r in rows if r["status"] == "loop"]
    singles = [r for r in rows if r["status"] == "single"]
    skipped = [r for r in rows if r["status"] == "skip"]

    lines = [
        "# 路线优化报告",
        "",
        f"用 `data/official_routes.json` 里 {len(recorded)} 个真人录制点校验仓库路线，"
        f"共 {len(rows)} 段。",
        "",
    ]

    # 各距离档：解释「为什么可能一段都改不了」
    tiers = [(100, 0), (200, 0), (500, 0), (1000, 0)]
    for r in rows:
        gap = r["record_gap"]
        if gap == float("inf"):
            continue
        for i, (limit, _n) in enumerate(tiers):
            if gap <= limit:
                tiers[i] = (limit, tiers[i][1] + 1)
                break
    reachable = sum(n for _, n in tiers)
    lines += [
        "## 〇、结论先说",
        "",
        f"**本轮替换 {len(candidates)} 段。** 录制数据与任务路线在空间上几乎不重叠："
        f"{len(rows)} 段里只有 {tiers[0][1]} 段离录制线 100m 以内、{tiers[1][1]} 段在 200m 以内，"
        f"其余 {len(rows) - reachable} 段在 500m 开外。",
        "",
        "录制路线集中在 Boss 点和地脉花采集点，任务是主线剧情点，两者不在同一片区域。"
        "所以「沿录制路径重算」这条路走不通——不是算法不行，是样本不在场。",
        "",
        "录制数据改用在两处更实在的地方：",
        "",
        "| 用途 | 产物 | 效果 |",
        "|---|---|---|",
        "| 路网可信边 | `tools/pathing/graph.json` 的 `origin=1` 边 | 709 条边、28 km，占路网总长 45%，A* 优先走这些 |",
        "| 实测传送锚点 | `data/anchor_plan.json` | 38 段找到真人用过的锚点，另 2 段确认无锚点可用 |",
        "",
        "| 离录制线 | 段数 |",
        "|---|---:|",
        f"| ≤ 100m | {tiers[0][1]} |",
        f"| ≤ 200m | {tiers[1][1]} |",
        f"| ≤ 500m | {tiers[2][1]} |",
        f"| ≤ 1000m | {tiers[3][1]} |",
        f"| > 1000m | {len(rows) - tiers[3][1]} |",
        "",
        "## 一、判定结果",
        "",
        "| 结果 | 段数 |",
        "|---|---:|",
        f"| 可优化（已{'替换' if args.apply else '生成候选，未写回'}） | {len(candidates)} |",
        f"| 闭环路线（采集环线，不适用 A→B） | {len(loops)} |",
        f"| 路网无覆盖 | {len(no_cov)} |",
        f"| 单点段 | {len(singles)} |",
        f"| 不值得动 | {len(skipped)} |",
        "",
        "判据：起终点吸附 ≤ 60m、新长度在原来的 0.60–1.30 倍之间、几何偏移 ≥ 25m、"
        "新路线录制边占比 ≥ 50%、离录制线至少近 20m。"
        "首末相距不足全长 5%（或 50m）的按闭环处理——那是采集环线，绕一圈回到起点，"
        "用 A→B 重算只会得到 0 长度。",
        "",
        "## 二、可优化段",
        "",
        "| 文件 | 点 | 原长 | 新长 | 偏移 | 录制边 | 离录制线 |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    for r in sorted(candidates, key=lambda x: -(x["shift"] or 0)):
        gap = "—" if r["record_gap"] == float("inf") else f"{r['record_gap']:.0f}m"
        lines.append(f"| {r['file'].split('pathing/')[0].split('/')[-2]} / {r['file'].split('/')[-1]} | "
                     f"{r['points']} | {r['length']:.0f}m | {r['new_length']:.0f}m | {r['shift']:.0f}m | "
                     f"{(r.get('record_ratio') or 0) * 100:.0f}% | {gap} |")

    lines += ["", "## 三、闭环路线（采集环线）", "",
              "首末重合，路线本身就是「绕一圈采完」。这类段不能按 A→B 重算，"
              "优化只能靠录一圈替换，工具不自动改。", "",
              "| 文件 | 点 | 全长 | 首末相距 |", "|---|---:|---:|---:|"]
    for r in sorted(loops, key=lambda x: -x["length"])[:20]:
        lines.append(f"| {r['file'].split('pathing/')[0].split('/')[-2]} / {r['file'].split('/')[-1]} | "
                     f"{r['points']} | {r['length']:.0f}m | {r['direct']:.0f}m |")
    if len(loops) > 20:
        lines.append(f"| …共 {len(loops)} 段 | | | |")

    lines += ["", "## 四、路网覆盖不到的段（靠传送 + 直连，必须实跑录制）", "",
              "| 文件 | 段长 | 原因 |", "|---|---:|---|"]
    for r in sorted(no_cov, key=lambda x: -x["length"])[:40]:
        lines.append(f"| {r['file'].split('pathing/')[0].split('/')[-2]} / {r['file'].split('/')[-1]} | "
                     f"{r['length']:.0f}m | {r['reason']} |")
    if len(no_cov) > 40:
        lines.append(f"| …共 {len(no_cov)} 段 | | |")

    lines += ["", "## 五、穿山嫌疑（离最近录制点 > 100m）", "",
              "没有录制参考不代表一定错，但这类段最该优先实跑验证。", "",
              "| 文件 | 段长 | 离录制线 |", "|---|---:|---:|"]
    far = [r for r in rows if r["record_gap"] != float("inf") and r["record_gap"] > 100]
    for r in sorted(far, key=lambda x: -x["record_gap"])[:40]:
        lines.append(f"| {r['file'].split('pathing/')[0].split('/')[-2]} / {r['file'].split('/')[-1]} | "
                     f"{r['length']:.0f}m | {r['record_gap']:.0f}m |")
    if len(far) > 40:
        lines.append(f"| …共 {len(far)} 段 | | |")

    Path(args.report).parent.mkdir(parents=True, exist_ok=True)
    Path(args.report).write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"段 {len(rows)}｜可优化 {len(candidates)}｜无覆盖 {len(no_cov)}｜不动 {len(skipped)}")
    if args.apply:
        print(f"已写回 {applied} 段")
    print(f"报告 {args.report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
