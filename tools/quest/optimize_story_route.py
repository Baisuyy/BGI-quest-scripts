#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成「路径优化」版剧情脚本。

和「原始」版的区别只有三处，每处都能逐条核对：

1. 单点段不再进路线清单。那种段只有一个坐标，是任务提示点，不是能走的路；
   硬跑会朝目标直冲，通常停在副本门口或墙边。
2. 每段补一个 anchor：最近传送锚点的名字和距离。运行时打印出来，
   传送之后会落在哪、还要走多远，一眼能看到。
3. 去掉相邻重合点（<0.5m）。路点是按 8 米贴地采样的，其余点一个不动——
   抽稀会让角色穿墙或掉坡，那种「优化」是负收益。

不做的事：不重算坐标、不替换路线、不缩短路径。没有实测数据的地方不假装优化。

用法：
    python tools/quest/optimize_story_route.py
    python tools/quest/optimize_story_route.py --tp <path/to/tp.json>
"""

from __future__ import annotations

import argparse
import json
import math
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SRC = ROOT / "js" / "原始-第七章-至冬"
DST = ROOT / "js" / "路径优化-第七章-至冬"
REPORT = ROOT / "docs" / "路径优化对比.md"

# 相邻点近到这个距离就当作重复
DUP_EPS = 0.5
# 总长 / 首末直线距离低于该值，视为「首尾直线插值」，没有绕障
STRAIGHT_RATIO = 1.02


# 能真正落地的点。副本入口（Domain 系列）不算：tp 到那里会进副本，不是传送落点。
TP_TYPES = {"TeleportWaypoint", "Goddess", "NodKraiMeetingPoint", "NatlanObsidianTotemPole"}


def load_tp(path: Path | None) -> dict[str, list[tuple[float, float, str]]]:
    """地图名 → [(x, y, 锚点名)]。BGI 的平面坐标取 position[2], position[0]。"""
    if path is None or not path.is_file():
        return {}

    data = json.loads(path.read_text(encoding="utf-8-sig"))
    out: dict[str, list[tuple[float, float, str]]] = {}
    for m in data.get("data", []):
        name = m.get("mapName")
        pts = []
        for p in m.get("points", []):
            pos = p.get("position") or []
            if len(pos) < 3 or p.get("type") not in TP_TYPES:
                continue
            pts.append((pos[2], pos[0], p.get("name", "")))
        if name:
            out[name] = pts
    return out


def nearest_anchor(tp: dict, map_name: str, x: float, y: float) -> tuple[str, int] | None:
    pts = tp.get(map_name)
    if not pts:
        return None
    best = min(pts, key=lambda p: math.hypot(p[0] - x, p[1] - y))
    return best[2], round(math.hypot(best[0] - x, best[1] - y))


def path_stats(positions: list[dict]) -> tuple[float, float]:
    """(总长, 首末直线距离)。"""
    total = 0.0
    for a, b in zip(positions, positions[1:]):
        total += math.hypot(b["x"] - a["x"], b["y"] - a["y"])
    if len(positions) < 2:
        return 0.0, 0.0
    first, last = positions[0], positions[-1]
    return total, math.hypot(last["x"] - first["x"], last["y"] - first["y"])


def dedupe(positions: list[dict]) -> list[dict]:
    out = [positions[0]]
    for p in positions[1:]:
        prev = out[-1]
        if math.hypot(p["x"] - prev["x"], p["y"] - prev["y"]) >= DUP_EPS:
            out.append(p)
    return out


def rewrite_segments(main_js: str, segs: list[dict]) -> str:
    body = json.dumps(segs, ensure_ascii=False, indent=4)
    return re.sub(r"const SEGMENTS = \[.*?\];", f"const SEGMENTS = {body};", main_js, count=1, flags=re.S)


def patch_main(main_js: str, segs: list[dict], dropped: list[int], skipped: list[int], total: int) -> str:
    """重写 SEGMENTS 并补上说明。不动 skipSegs：那是作者的运行策略，不是路线问题。"""
    s = rewrite_segments(main_js, segs)

    # 原注释里写的锚点距离来自另一套坐标口径，与本脚本实测不一致；既然要发优化版，就改成原因说明
    s = re.sub(
        r"skipSegs:\s*\[((?:\s*\d+\s*,?)+)\],[^\n]*\n(?:\s*//[^\n]*\n)?",
        lambda m: "skipSegs: [" + m.group(1).strip() + "],        // 跳过的段在火车上或剧情演出里，沿线没有可落地点\n",
        s,
        count=1,
    )

    removed_note = ""
    if dropped:
        removed_note = "，本脚本移除了段 " + "、".join(str(i) for i in dropped)

    runnable = [x for x in segs if x["seq"] not in skipped]
    dead_note = ""
    if total > 0 and not runnable:
        dead_note = (
            "//\n"
            "// 注意：本任务没有可自动跑的路线段。段 "
            + "、".join(str(i) for i in sorted(set(dropped) | set(skipped)))
            + " 要么是任务提示点，要么在火车/剧情演出里，脚本会直接结束。\n"
            "// 这一段只能手动完成，或等官方录制路线补齐后重新生成。\n"
        )

    banner = (
        "//\n"
        "// 【路径优化版】与「原始」版的差别，逐条可核对：\n"
        "//   1. 单点段（只有 1 个坐标的任务提示点）已从路线清单移除"
        + removed_note
        + "\n"
        "//   2. 每段带 anchor，运行时打印最近传送锚点与距离，方便判断传送后要补走多远\n"
        "//   3. 相邻重合点已合并（<0.5m），其余点位未改动——路点是贴地采样，抽稀会穿墙\n"
        "// 点位坐标没有重算，也没换路线：没有实测数据的地方不做替换。\n"
        + dead_note
        + "//\n"
    )
    s = s.replace("// ============================================================\n\nconst CONFIG", "// ============================================================\n" + banner + "\nconst CONFIG", 1)

    # 每段开始时打印锚点信息
    s = s.replace(
        "        log.info('  ' + seg.label);",
        "        log.info('  ' + seg.label);\n"
        "        if (seg.anchor) {\n"
        "            log.info('  最近锚点：' + seg.anchor.name + '（' + seg.anchor.distance + ' m）');\n"
        "        } else {\n"
        "            log.warn('  这一段附近没有已知传送锚点，会从当前位置导航过去');\n"
        "        }",
        1,
    )
    return s


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tp", type=Path, default=None, help="BGI 的 tp.json，用于算最近锚点")
    args = ap.parse_args()

    if not SRC.is_dir():
        print(f"找不到原始目录：{SRC}")
        return 1

    tp = load_tp(args.tp)
    if not tp:
        print("未提供 tp.json，anchor 字段留空")

    rows: list[tuple] = []
    count = 0

    for manifest_path in sorted(SRC.rglob("manifest.json")):
        task_dir = manifest_path.parent
        rel = task_dir.relative_to(SRC)
        out_dir = DST / rel

        if out_dir.exists():
            shutil.rmtree(out_dir)
        out_dir.mkdir(parents=True, exist_ok=True)

        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        main_js = (task_dir / "main.js").read_text(encoding="utf-8-sig")

        m = re.search(r"const SEGMENTS = (\[.*?\]);", main_js, re.S)
        if not m:
            print(f"跳过（没有 SEGMENTS 数组）：{rel}")
            continue
        segs = json.loads(m.group(1))
        skip_m = re.search(r"skipSegs:\s*\[([^\]]*)\]", main_js)
        skip_segs = [int(x) for x in re.findall(r"\d+", skip_m.group(1))] if skip_m else []

        kept: list[dict] = []
        dropped: list[int] = []
        point_total = 0
        (out_dir / "pathing").mkdir(exist_ok=True)

        for seg in segs:
            src_file = task_dir / seg["file"]
            if not src_file.is_file():
                dropped.append(seg["seq"])
                continue

            data = json.loads(src_file.read_text(encoding="utf-8-sig"))
            positions = data.get("positions") or []
            if len(positions) < 2:
                dropped.append(seg["seq"])
                rows.append((str(rel), seg["seq"], seg["label"], len(positions), 0, "单点段，移除", "", ""))
                continue

            before = len(positions)
            positions = dedupe(positions)
            total, direct = path_stats(positions)
            straight = direct > 0 and total / direct < STRAIGHT_RATIO

            data["positions"] = positions
            info = data.setdefault("info", {})
            info["description"] = info.get("description", "") + "（路径优化）"
            (out_dir / seg["file"]).write_text(
                json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
            )

            map_name = seg.get("map") or info.get("map_name") or "Teyvat"
            anchor = nearest_anchor(tp, map_name, positions[0]["x"], positions[0]["y"])

            item = dict(seg)
            if anchor:
                item["anchor"] = {"name": anchor[0], "distance": anchor[1]}
            kept.append(item)

            point_total += len(positions)
            rows.append((
                str(rel), seg["seq"], seg["label"], before, len(positions),
                "首尾直线插值，无绕障" if straight else "有转折",
                f"{anchor[0]} / {anchor[1]}m" if anchor else "无数据",
                f"{total:.0f}m",
            ))

        (out_dir / "main.js").write_text(
            patch_main(main_js, kept, dropped, skip_segs, len(segs)), encoding="utf-8"
        )

        prefix = manifest.get("description", "").split(" · ")[0].strip()
        manifest["description"] = f"{prefix} · {len(kept)} 段 {point_total} 点（路径优化）"
        (out_dir / "manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        count += 1
        print(f"{rel}: 保留 {len(kept)} 段，移除 {len(dropped)} 段")

    write_report(rows)
    print(f"\n共 {count} 个脚本，报告见 {REPORT.relative_to(ROOT)}")
    return 0


def write_report(rows: list[tuple]) -> None:
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    lines = [
        "# 路径优化对比",
        "",
        "`js/路径优化` 由 `tools/quest/optimize_story_route.py` 从 `js/原始` 生成，可重复执行。",
        "",
        "改动只有三类：移除单点段、补最近锚点、合并重合点。**坐标没有重算**。",
        "",
        "| 脚本 | 段 | 说明 | 原始点 | 优化点 | 形态 | 最近锚点 | 长度 |",
        "|---|---|---|---|---|---|---|---|",
    ]
    for r in rows:
        lines.append("| " + " | ".join(str(x) for x in r) + " |")

    lines += [
        "",
        "## 怎么读「形态」这一列",
        "",
        "- **首尾直线插值，无绕障**：整段几乎是一条直线，说明它不是真人走过的路线，",
        "  而是从起点到终点插值出来的。这种段遇到山体、建筑、河流就会卡住，",
        "  只能靠人守着，或者重新录制一条。",
        "- **有转折**：形态上有绕行，可能是采样得到的，也可能只是插了几段直线，",
        "  仍然需要实跑验证。",
        "",
        "## 为什么不抽稀",
        "",
        "路点按约 8 米间隔贴地采样。抽稀到 16 米以上，角色在坡地和台阶处会悬空或穿模；",
        "按几何形状做 Douglas-Peucker 更糟——这批路线本来就是直线，简化后只剩几个拐点，",
        "相当于把整段换成两点连线。所以只合并 0.5 米内的重合点。",
    ]
    REPORT.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
