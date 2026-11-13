#!/usr/bin/env python3
"""生成路线前的守卫：先判断这一步该不该有路线，再谈怎么生成。

背景：仓库里 270 段路线是对着「任务步骤坐标」批量生成的，没有区分步骤类型。
结果是大量对话/剧情步骤也被配了路线，而真正的室内步骤又套了大地图寻路——
前者白跑，后者跑飞。解包数据 `data/quest_steps.json` 里每步都有 kind 与 flags，
足够在生成前把不该生成的全部拦下来。

规则（按顺序，命中即停）：

| 条件 | 结论 | 理由 |
|---|---|---|
| kind != path | 不生成 | 对话、剧情、计数等步骤没有可走的目标 |
| 坐标缺失 | 不生成 | 没有目标点，路线无从谈起 |
| flags 含 local_coords | 不生成 | 局部坐标，交给大地图寻路会跑到几公里外 |
| flags 含 requires_unlock | 待解锁 | 该步要求先开传送点，没开就是黑雾，生成也跑不了 |
| scene 3 或已确认地图名 | 生成 | 大世界 / 已核对过的独立地图 |
| 其余 scene，坐标在原点 1500m 内 | 不生成 | 室内或独立地图的局部坐标，地图名未知 |
| 其余 scene，坐标是大世界量级 | 生成 | 挂在独立场景上、但用的是世界坐标（20276 试验设计局等） |

最后一条是踩过坑才加的：`20276` 潜入试验设计局、`20372` 曙光车站都挂在非 3 的
scene 上，坐标却是标准世界坐标（3094,9513 / 8478,7792）。早先按「scene != 3 一律拦」
会把 100 段合法路线误判成违规。

用法：
  python3 tools/quest/guard_generator.py --plan           # 输出可生成清单
  python3 tools/quest/guard_generator.py --check          # 校验现有路线是否踩线
  python3 tools/quest/guard_generator.py --plan --check   # 两件事都做
"""

from __future__ import annotations

import argparse
import json
import math
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STEPS = ROOT / "data" / "quest_steps.json"
OVERRIDES = ROOT / "data" / "scene_overrides.json"
PLAN_OUT = ROOT / "data" / "generation_plan.json"
REPORT = ROOT / "docs" / "生成器守卫报告.md"

# 路线首点落在某步骤这个半径内，就算这段是给该步骤生成的
MATCH_RADIUS = 80.0
# 判定「这段路线附近最近的步骤是不是跑图步骤」时用的网格边长
GRID = 100.0


def should_generate(step: dict, overrides: dict) -> tuple[bool, str]:
    """返回 (是否生成, 原因)。原因同时用于报告与日志。"""
    scene = str(step.get("scene"))
    has_xyz = bool(step.get("xyz"))

    # 已确认的独立地图（目前只有 20378 → TempleOfSpace）：它有自己的锚点表，
    # tp(x, y, mapName) 能直接传进去，所以即使 analyze_steps 把它归成 indoor，
    # 这里也按「可寻路的独立地图」放行。
    if scene in overrides and has_xyz:
        if "requires_unlock" in set(step.get("flags") or []):
            return False, "要求先解锁传送点，未解锁时是黑雾"
        return True, f"独立地图 {(overrides[scene] or {}).get('map') or '已确认'}，按该地图寻路"

    if step.get("kind") != "path":
        return False, f"不是跑图步骤（{step.get('kind')}）"
    if not has_xyz:
        return False, "没有坐标"
    flags = set(step.get("flags") or [])
    if "local_coords" in flags:
        return False, "局部坐标，不能交给大地图寻路"
    if "requires_unlock" in flags:
        return False, "要求先解锁传送点，未解锁时是黑雾"

    if scene == "3":
        return True, "可生成"

    # scene 不是 3 又没有人工核对的地图名：只有坐标是大世界量级时才敢生成，
    # 原点附近的小坐标（|x|,|z| < 1500）几乎都是室内或独立地图的局部坐标。
    x, _, z = step["xyz"]
    if abs(x) <= 1500 and abs(z) <= 1500:
        return False, f"场景 {scene} 无地图名，坐标在原点附近（局部坐标）"
    return True, f"场景 {scene} 用世界坐标，按 Teyvat 生成"


def map_of(step: dict, overrides: dict) -> str | None:
    scene = str(step.get("scene"))
    if scene in overrides:
        return (overrides[scene] or {}).get("map")
    if scene == "3":
        return "Teyvat"
    x, _, z = step.get("xyz") or (0, 0, 0)
    return "Teyvat" if (abs(x) > 1500 or abs(z) > 1500) else None


def build_plan(data: dict, overrides: dict) -> dict:
    generate, skip = [], []
    for qid, quest in data["quests"].items():
        for step in quest.get("steps") or []:
            ok, reason = should_generate(step, overrides)
            item = {
                "quest": qid,
                "title": quest.get("title"),
                "order": step.get("order"),
                "name": step.get("name"),
                "kind": step.get("kind"),
                "scene": step.get("scene"),
                "xyz": step.get("xyz"),
                "map": map_of(step, overrides),
            }
            if ok:
                generate.append(item)
            else:
                item["reason"] = reason
                skip.append(item)
    return {"generate": generate, "skip": skip}


def index_steps(data: dict) -> dict[tuple[int, int], list[dict]]:
    """所有步骤按网格索引，用于反查「这段路线最近对应哪一步」。"""
    grid: dict[tuple[int, int], list[dict]] = defaultdict(list)
    for qid, quest in data["quests"].items():
        for step in quest.get("steps") or []:
            xyz = step.get("xyz")
            if not xyz:
                continue
            key = (int(math.floor(xyz[0] / GRID)), int(math.floor(xyz[2] / GRID)))
            grid[key].append({**step, "quest": qid, "title": quest.get("title")})
    return grid


def nearest_step(grid: dict, x: float, y: float) -> tuple[dict | None, float]:
    gx, gy = int(math.floor(x / GRID)), int(math.floor(y / GRID))
    best, best_d = None, float("inf")
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for step in grid.get((gx + dx, gy + dy), ()):
                sx, sz = step["xyz"][0], step["xyz"][2]
                d = math.hypot(sx - x, sz - y)
                if d < best_d:
                    best, best_d = step, d
    return best, best_d


def check_routes(data: dict, overrides: dict) -> list[dict]:
    """现有路线逐段反查：它对应的步骤该不该有路线。"""
    grid = index_steps(data)
    rows = []
    for path in sorted(ROOT.glob("js/**/pathing/*.json")):
        raw = json.loads(path.read_text(encoding="utf-8"))
        info = raw.get("info") if isinstance(raw.get("info"), dict) else {}
        positions = raw.get("positions") or []
        if not positions:
            continue
        x, y = float(positions[0]["x"]), float(positions[0]["y"])
        step, gap = nearest_step(grid, x, y)
        row = {
            "file": str(path.relative_to(ROOT)),
            "map": info.get("map_name") or "Teyvat",
            "first": [round(x), round(y)],
            "gap": round(gap) if step else None,
            "step": step,
            "verdict": "ok",
            "reason": "",
        }
        if step is None or gap > MATCH_RADIUS:
            row["verdict"] = "unmatched"
            row["reason"] = "附近没有任务步骤（跨段接续点或旧数据）"
        else:
            ok, reason = should_generate(step, overrides)
            if not ok:
                row["verdict"] = "violation"
                row["reason"] = reason
        rows.append(row)
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description="生成路线前的守卫")
    parser.add_argument("--plan", action="store_true", help="输出可生成清单")
    parser.add_argument("--check", action="store_true", help="校验现有路线")
    parser.add_argument("--report", default=str(REPORT))
    args = parser.parse_args()
    if not (args.plan or args.check):
        args.plan = args.check = True

    data = json.loads(STEPS.read_text(encoding="utf-8"))
    overrides = json.loads(OVERRIDES.read_text(encoding="utf-8")) if OVERRIDES.exists() else {}

    lines = ["# 生成器守卫报告", ""]

    if args.plan:
        plan = build_plan(data, overrides)
        PLAN_OUT.write_text(json.dumps(plan, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        reasons = Counter(item["reason"] for item in plan["skip"])
        total = len(plan["generate"]) + len(plan["skip"])
        lines += [
            "## 一、可生成清单",
            "",
            f"全部 {total} 步里，**{len(plan['generate'])} 步**应当生成路线，"
            f"其余 {len(plan['skip'])} 步拦下。清单在 `data/generation_plan.json`。",
            "",
            "| 拦截原因 | 步骤数 |",
            "|---|---:|",
        ]
        for reason, n in reasons.most_common():
            lines.append(f"| {reason} | {n} |")
        lines += ["", "| 任务 | 可生成步数 | 任务名 |", "|---|---:|---|"]
        per_quest = Counter(item["quest"] for item in plan["generate"])
        for qid, n in per_quest.most_common(15):
            lines.append(f"| {qid} | {n} | {data['quests'][qid]['title'][:24]} |")
        print(f"计划：可生成 {len(plan['generate'])} 步，拦下 {len(plan['skip'])} 步")

    if args.check:
        rows = check_routes(data, overrides)
        counts = Counter(row["verdict"] for row in rows)
        violations = [r for r in rows if r["verdict"] == "violation"]
        unmatched = [r for r in rows if r["verdict"] == "unmatched"]
        lines += [
            "",
            "## 二、现有路线的守卫检查",
            "",
            f"共 {len(rows)} 段。",
            "",
            "| 结论 | 段数 | 含义 |",
            "|---|---:|---|",
            f"| ok | {counts.get('ok', 0)} | 对应的是跑图步骤，该有路线 |",
            f"| violation | {len(violations)} | 对应步骤不该有路线 |",
            f"| unmatched | {len(unmatched)} | 80m 内没有任务步骤 |",
            "",
            "### 违规段",
            "",
            "| 段 | 首点 | 对应步骤 | 原因 |",
            "|---|---|---|---|",
        ]
        for r in violations[:40]:
            step = r["step"]
            lines.append(f"| {r['file'].split('pathing/')[0].split('/')[-2]} / {r['file'].split('/')[-1]} | "
                         f"{r['first'][0]},{r['first'][1]} | {step['quest']} #{step['order']} "
                         f"{str(step.get('name'))[:14]} | {r['reason']} |")
        if len(violations) > 40:
            lines.append(f"| …共 {len(violations)} 段 | | | |")
        print(f"检查：合规 {counts.get('ok', 0)}，违规 {len(violations)}，未匹配 {len(unmatched)}")

    Path(args.report).write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"报告 {args.report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
