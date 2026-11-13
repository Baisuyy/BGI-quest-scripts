#!/usr/bin/env python3
"""把解包数据里的任务步骤拆成「能不能自动跑」，并统计现有脚本的覆盖情况。

数据来源（DimbreathBot/AnimeGameData，CNRELWin 分支）：
  BinOutput/Quest/*.json          任务定义，含 subQuests[].guide 与 finishCond
  ExcelBinOutput/ChapterExcelConfigData.json  章节 → 任务区间、城市
  TextMap/TextMapCHS.json         文本

关键结论来自 finishCond：它把「这一步怎么才算完成」写成了枚举。
  COMPLETE_TALK      过对话，不需要走位
  FINISH_PLOT        剧情标记，游戏自己推进
  LUA_NOTIFY         脚本事件，外部无法触发
  UNLOCK_ANY_TRANS_POINT  需要先解锁传送点（未解锁区域就是这里卡住）
所以「每一步都要一段路线」这个前提本来就不成立，这也是任务脚本看起来大量缺失的原因。

用法：
  python3 tools/quest/analyze_steps.py --data /workspace/AnimeGameData \
      --out-json data/quest_steps.json --out-doc docs/任务脚本覆盖与步骤可执行性.md
"""

import argparse
import collections
import datetime
import json
import os
import re
import subprocess
from pathlib import Path

# guide.param 形如 [3_99:9654.501,395.007,6606.910]
COORD = re.compile(r"\[(\d+)_(-?\d+):(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)")

CITY = {1: "蒙德", 2: "璃月", 3: "稻妻", 4: "须弥", 5: "枫丹", 6: "纳塔", 7: "挪德卡莱", 8: "至冬"}

# 大世界统一 scene=3，其余多是室内或副本。
# 但 guideScene 不可靠：至冬宫外庭（20410）既可能是室内局部坐标（0,3.5,-54），
# 也可能是大世界坐标（9644,395,6615）。所以再叠一层坐标量级判断。
OVERWORLD_SCENE = 3
LOCAL_COORD_LIMIT = 1500.0

COND_KIND = {
    "QUEST_CONTENT_COMPLETE_TALK": "talk",
    "QUEST_CONTENT_LUA_NOTIFY": "scripted",
    "QUEST_CONTENT_FINISH_PLOT": "plot",
    "QUEST_CONTENT_MAIN_COOP_ENTER_SAVE_POINT": "plot",
    "QUEST_CONTENT_ENTER_DUNGEON": "dungeon",
    "QUEST_CONTENT_LEAVE_SCENE": "dungeon",
    "QUEST_CONTENT_GAME_TIME_TICK": "wait",
    "QUEST_CONTENT_TIME_VAR_PASS_DAY": "wait",
    "QUEST_CONTENT_INTERACT_GADGET": "interact",
    "QUEST_CONTENT_FINISH_ITEM_GIVING": "interact",
    "QUEST_CONTENT_OBTAIN_ITEM": "item",
    "QUEST_CONTENT_ADD_QUEST_PROGRESS": "progress",
    "QUEST_CONTENT_GADGET_STATE_CHANGE": "gadget",
    "QUEST_CONTENT_PRESENT_AT_SPECIFIC_SCENE": "scene",
    "QUEST_CONTENT_QUEST_STATE_EQUAL": "chain",
    "QUEST_CONTENT_QUEST_VAR_EQUAL": "chain",
    "QUEST_CONTENT_QUEST_GLOBAL_VAR_EQUAL": "chain",
    "QUEST_CONTENT_QUEST_GLOBAL_VAR_GREAT": "chain",
    "QUEST_CONTENT_TRIGGER_FIRE": "gadget",
}

KIND_LABEL = {
    "path": "跑图",
    "indoor": "室内点位",
    "talk": "对话",
    "scripted": "脚本事件",
    "plot": "剧情推进",
    "wait": "等待",
    "dungeon": "副本",
    "interact": "交互",
    "item": "交物",
    "progress": "计数",
    "gadget": "机关",
    "scene": "场景条件",
    "chain": "任务链",
    "event": "其它",
}


def load_textmap(data: Path) -> dict:
    """合并主文本表与补充表，取不到的返回空串。"""
    merged = {}
    for name in ("TextMapCHS.json", "TextMap_MediumCHS.json"):
        p = data / "TextMap" / name
        if not p.exists():
            continue
        try:
            merged.update(json.load(p.open(encoding="utf-8")))
        except Exception as exc:  # 单个文本表损坏不影响整体
            print(f"[warn] {name} 读取失败：{exc}")
    return merged


def is_local(scene, xyz) -> bool:
    """判断坐标是不是室内局部坐标。

    scene=3 一律是大世界；其余场景再看量级——外庭这类「差分场景」里
    同时存在局部坐标（几十米内）和大世界坐标（上万），只看 scene 会误判。
    """
    if scene == OVERWORLD_SCENE:
        return False
    if not xyz:
        return True
    return max(abs(xyz[0]), abs(xyz[2])) < LOCAL_COORD_LIMIT


def classify(has_coord: bool, local: bool, conds) -> str:
    """先看有没有可走的坐标，再看完成条件；坐标优先，因为那决定要不要寻路。"""
    if has_coord:
        return "indoor" if local else "path"
    kinds = [COND_KIND.get(c.get("type"), "event") for c in conds]
    for want in ("talk", "scripted", "dungeon", "interact", "wait", "plot"):
        if want in kinds:
            return want
    return kinds[0] if kinds else "event"


def scene_stats(quests: dict) -> dict:
    """场景 → 这个场景的点位是大世界还是局部。

    客户端拿这张表就能判断：路线首点落在哪个场景，能不能交给大地图寻路。
    """
    stats = collections.defaultdict(lambda: {"steps": 0, "local": 0, "overworld": 0})
    for q in quests.values():
        for s in q["steps"]:
            if s["xyz"] is None:
                continue
            key = str(s["scene"])
            stats[key]["steps"] += 1
            stats[key]["local" if "local_coords" in s["flags"] else "overworld"] += 1
    out = {}
    for k, v in stats.items():
        # scene=3 是提瓦特大地图；其余场景即使坐标量级像世界坐标，
        # 也要先进入该场景才有意义（例：20276 试验设计局、20372 曙光车站）。
        if k == str(OVERWORLD_SCENE):
            kind = "teyvat"
        elif v["overworld"] > v["local"]:
            kind = "world_coords"
        else:
            kind = "local"
        out[k] = {
            "steps": v["steps"],
            "local": v["local"],
            "overworld": v["overworld"],
            "kind": kind,
        }
    return dict(sorted(out.items(), key=lambda kv: -kv[1]["steps"]))


def unlock_gated(quests: dict) -> list:
    """列出了「必须先解锁传送点」的步骤，这些点位在未开图时会把小地图遮成黑雾。"""
    out = []
    for qid, q in quests.items():
        for s in q["steps"]:
            if "requires_unlock" in s["flags"]:
                out.append({
                    "quest": qid, "title": q["title"], "order": s["order"],
                    "name": s["name"], "scene": s["scene"], "xyz": s["xyz"],
                })
    return out


def scene_maps(quests: dict, overrides: dict) -> dict:
    """场景 → 客户端地图名。只认人工核对过的映射。

    试过用「点位是否落在各独立地图包围盒内」自动推断，不可靠：
    19 个场景会同时命中多张地图的盒子（室内局部坐标都挤在原点附近），
    20378 会被推成 TheChasm，而它的剧情是「前往空之神殿」，实际是 TempleOfSpace。
    错的地图名比没有地图名更糟——传送会把角色丢到几百公里外。
    所以这里只输出 overrides 里人工确认过的条目，其余留空，交给实机验证补。
    """
    by_scene = collections.Counter()
    for q in quests.values():
        for s in q["steps"]:
            if s["xyz"]:
                by_scene[str(s["scene"])] += 1

    out = {str(OVERWORLD_SCENE): {"map": "Teyvat", "confidence": "confirmed",
                                  "points": by_scene[str(OVERWORLD_SCENE)]}}
    for sid, item in overrides.items():
        out[sid] = {"map": item["map"], "confidence": item.get("confidence", "candidate"),
                    "points": by_scene.get(sid, 0), "evidence": item.get("evidence", "")}
    return dict(sorted(out.items(), key=lambda kv: -kv[1]["points"]))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="/workspace/AnimeGameData")
    ap.add_argument("--scripts", default="js")
    ap.add_argument("--out-json", default="data/quest_steps.json")
    ap.add_argument("--out-doc", default="docs/任务脚本覆盖与步骤可执行性.md")
    args = ap.parse_args()

    data = Path(args.data)
    text = load_textmap(data)
    T = lambda h: text.get(str(h), "") if h else ""

    chapters = {}
    for c in json.load((data / "ExcelBinOutput/ChapterExcelConfigData.json").open(encoding="utf-8")):
        chapters[c.get("id")] = c

    quests = {}
    for f in sorted((data / "BinOutput/Quest").glob("*.json")):
        try:
            q = json.load(f.open(encoding="utf-8"))
        except Exception:
            continue
        qid = q.get("id")
        if not qid:
            continue
        ch = chapters.get(q.get("chapterId")) or {}
        steps = []
        for s in sorted(q.get("subQuests") or [], key=lambda x: x.get("order", 0)):
            name = T(s.get("descTextMapHash")).replace("$HIDDEN", "").strip()
            if not name or "(test)" in name:
                continue
            g = s.get("guide") or {}
            params = g.get("param") or [""]
            m = COORD.search(params[0] if params else "")
            scene = g.get("guideScene")
            xyz = None
            if m:
                scene = int(m.group(1))
                xyz = [round(float(m.group(i)), 1) for i in (3, 4, 5)]
            conds = s.get("finishCond") or []
            types = [c.get("type") for c in conds]
            movable = xyz is not None and (abs(xyz[0]) > 0.01 or abs(xyz[2]) > 0.01)
            local = is_local(scene, xyz)
            kind = classify(movable, local, conds)
            flags = []
            if "QUEST_CONTENT_UNLOCK_ANY_TRANS_POINT" in types:
                flags.append("requires_unlock")
            if local:
                flags.append("local_coords")
            steps.append({
                "order": s.get("order"),
                "sub_id": s.get("subId"),
                "name": name,
                "kind": kind,
                "scene": scene,
                "xyz": xyz,
                "style": g.get("guideStyle"),
                "conds": types,
                "flags": flags,
            })
        if not steps:
            continue
        quests[str(qid)] = {
            "title": T(q.get("titleTextMapHash")),
            "chapter": q.get("chapterId"),
            "chapter_title": T(ch.get("chapterTitleTextMapHash")),
            "chapter_no": T(ch.get("chapterNumTextMapHash")),
            "city": CITY.get(ch.get("cityId"), ch.get("cityId")),
            "steps": steps,
        }

    # 现有脚本：js/<章节>/<幕>/<任务ID>-<名>/main.js
    scripts = {}
    for p in Path(args.scripts).glob("*/*/*/main.js"):
        m = re.match(r"(\d+)", p.parent.name)
        if m:
            scripts[m.group(1)] = str(p.parent.relative_to(args.scripts))

    src = "unknown"
    try:
        src = subprocess.run(
            ["git", "-C", str(data), "log", "-1", "--format=%s"],
            capture_output=True, text=True, timeout=15,
        ).stdout.strip() or "unknown"
    except Exception:
        pass

    ov_path = Path("data/scene_overrides.json")
    overrides = json.load(ov_path.open(encoding="utf-8")) if ov_path.exists() else {}

    payload = {
        "source": src,
        "generated_at": datetime.date.today().isoformat(),
        "overworld_scene": OVERWORLD_SCENE,
        "local_coord_limit": LOCAL_COORD_LIMIT,
        "kind_label": KIND_LABEL,
        "scenes": scene_stats(quests),
        "scene_maps": scene_maps(quests, overrides),
        "unlock_gated": unlock_gated(quests),
        "quests": quests,
    }
    out = Path(args.out_json)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    # ---- 统计 ----
    kind_all = collections.Counter()
    for q in quests.values():
        for s in q["steps"]:
            kind_all[s["kind"]] += 1

    total_steps = sum(kind_all.values())
    runnable = kind_all["path"]
    by_chapter = collections.defaultdict(lambda: collections.Counter())
    for q in quests.values():
        # 世界任务与传说任务没有 chapterId，单独归一类，不混进章节统计里
        key = (f"{q['chapter_no']} {q['chapter_title']}".strip() or f"章节 {q['chapter']}") \
            if q["chapter"] else "世界任务 / 传说任务"
        for s in q["steps"]:
            by_chapter[key][s["kind"]] += 1
            by_chapter[key]["_n"] += 1

    have = [q for q in quests if q in scripts]
    miss = [q for q in quests if q not in scripts]
    miss_with_path = [q for q in miss if any(s["kind"] == "path" for s in quests[q]["steps"])]
    covered_steps = sum(len(quests[q]["steps"]) for q in have)
    covered_path = sum(1 for q in have for s in quests[q]["steps"] if s["kind"] == "path")

    def pct(a, b):
        return f"{a * 100 / b:.1f}%" if b else "—"

    lines = [
        "# 任务脚本覆盖与步骤可执行性",
        "",
        f"数据源：`{src}`，生成于 {payload['generated_at']}。",
        f"共 **{len(quests)}** 个带步骤的任务、**{total_steps}** 个可执行步骤。",
        "",
        "## 一、步骤类型分布",
        "",
        "| 类型 | 含义 | 步骤数 | 占比 |",
        "| --- | --- | ---: | ---: |",
    ]
    for k, v in kind_all.most_common():
        lines.append(f"| `{k}` | {KIND_LABEL.get(k, k)} | {v} | {pct(v, total_steps)} |")
    lines += [
        "",
        f"真正需要走到点位的只有 **{runnable}** 步（{pct(runnable, total_steps)}）。"
        "其余步骤由对话、剧情、脚本事件推进，写路线没有意义——"
        "这是「任务脚本看起来大量缺失」的根本原因，不是生成时漏了。",
        "",
        "## 二、现有脚本覆盖",
        "",
        "| 指标 | 数值 |",
        "| --- | ---: |",
        f"| 已有脚本的任务 | {len(have)} |",
        f"| 缺脚本的任务 | {len(miss)} |",
        f"| 缺脚本但含可跑图步骤 | {len(miss_with_path)} |",
        f"| 已覆盖步骤 | {covered_steps} / {total_steps}（{pct(covered_steps, total_steps)}）|",
        f"| 已覆盖的可跑图步骤 | {covered_path} / {runnable}（{pct(covered_path, runnable)}）|",
        "",
        "按可跑图步骤数排序的缺口清单（前 40 个）：",
        "",
        "| 任务 | 章节 | 可跑图步 | 对话步 | 其它 | 总步 |",
        "| --- | --- | ---: | ---: | ---: | ---: |",
    ]
    ranked = sorted(
        miss_with_path,
        key=lambda q: (-sum(1 for s in quests[q]["steps"] if s["kind"] == "path"), q),
    )
    for q in ranked[:40]:
        st = quests[q]["steps"]
        c = collections.Counter(s["kind"] for s in st)
        ch = quests[q]
        chname = (f"{ch['chapter_no']} {ch['chapter_title']}"[:20].strip() or "世界任务") if ch["chapter"] else "世界任务"
        lines.append(
            f"| {q} {ch['title'][:16]} | {chname} "
            f"| {c['path']} | {c['talk']} | {len(st) - c['path'] - c['talk']} | {len(st)} |"
        )

    lines += ["", "## 三、按章节的步骤构成", "", "| 章节 | 总步 | 跑图 | 对话 | 室内 | 脚本/剧情 | 其它 |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for key in sorted(by_chapter, key=lambda k: (-by_chapter[k]["_n"], k))[:30]:
        c = by_chapter[key]
        other = c["_n"] - c["path"] - c["talk"] - c["indoor"] - c["scripted"] - c["plot"]
        lines.append(
            f"| {key[:26]} | {c['_n']} | {c['path']} | {c['talk']} | {c['indoor']} "
            f"| {c['scripted'] + c['plot']} | {other} |"
        )

    lines += ["", "## 四、场景与坐标空间", "",
              "坐标是不是「能直接交给大地图寻路」，由**量级**决定，`guideScene` 只作参考：",
              "",
              "| kind | 含义 | 怎么用 |",
              "| --- | --- | --- |",
              "| `teyvat` | scene=3，提瓦特大地图 | 可直接寻路 |",
              "| `world_coords` | 独立场景但坐标是世界坐标（如 20372 曙光车站 8478,224,7792） | 先确认已进入该场景，再用同一套坐标寻路 |",
              "| `local` | 场景内局部坐标（如 1162 至冬宫 0,0,66） | **不能**交给大地图，只能场景内小移动或跳过 |",
              "",
              "| 场景 | 点位数 | 大世界坐标 | 局部坐标 | kind |", "| ---: | ---: | ---: | ---: | --- |"]
    for sid, v in list(payload["scenes"].items())[:20]:
        lines.append(f"| {sid} | {v['steps']} | {v['overworld']} | {v['local']} | `{v['kind']}` |")

    cand = {k: v for k, v in payload["scene_maps"].items() if v["confidence"] != "confirmed"}
    lines += ["", "## 五、场景 → 客户端地图名", "",
              "客户端 `genshin.tp(x, y, mapName)` 需要地图名，解包里只有 scene id。"
              "自动推断做过，不可靠——室内局部坐标都挤在原点附近，会同时命中好几张独立地图的包围盒，"
              "`20378` 就被推成 `TheChasm`，而它的剧情是「前往空之神殿」，实际是 `TempleOfSpace`。"
              "错的地图名比没有更糟，所以只保留人工核对过的条目（`data/scene_overrides.json`）。", ""]
    if cand:
        lines += ["| scene | 地图 | 置信度 | 依据 | 点位数 |", "| ---: | --- | --- | --- | ---: |"]
        for sid, v in cand.items():
            lines.append(f"| {sid} | `{v['map']}` | {v['confidence']} | {v.get('evidence', '')} | {v['points']} |")
    else:
        lines.append("（暂无人工确认的映射）")

    gated = payload["unlock_gated"]
    lines += ["", "## 六、解锁门槛步骤", "",
              f"共 **{len(gated)}** 步的完成条件里带 `UNLOCK_ANY_TRANS_POINT`，也就是必须先开图。"
              "这类点位在未解锁区域会遮出黑雾、地图匹配失败，脚本应当直接跳过而不是硬跑。", ""]
    if gated:
        lines += ["| 任务 | 步骤 | 场景 | 坐标 |", "| --- | --- | ---: | --- |"]
        for g in gated[:30]:
            xyz = ",".join(f"{v:.0f}" for v in g["xyz"]) if g["xyz"] else "—"
            lines.append(f"| {g['quest']} {g['title'][:12]} | {g['name'][:16]} | {g['scene']} | {xyz} |")

    lines += [
        "",
        "## 七、用这份数据能做的事",
        "",
        "| 用法 | 字段 |",
        "| --- | --- |",
        "| 只给可跑图的步骤生成路线，其余标 `skip` | `steps[].kind == \"path\"` |",
        "| 室内点位不交给大地图寻路（否则必然跑飞） | `steps[].flags` 含 `local_coords` |",
        "| 未解锁区域提前跳过，别等黑雾吞掉小地图 | `steps[].flags` 含 `requires_unlock` |",
        "| 对话步骤交给自动跳过，不生成路线 | `steps[].kind == \"talk\"` |",
        "| 按章节挑值得补脚本的任务 | 第二节的缺口清单 |",
        "",
    ]
    doc = Path(args.out_doc)
    doc.parent.mkdir(parents=True, exist_ok=True)
    doc.write_text("\n".join(lines), encoding="utf-8")

    print(json.dumps({
        "quests": len(quests), "steps": total_steps,
        "runnable": runnable, "runnable_pct": pct(runnable, total_steps),
        "scripts_have": len(have), "scripts_missing": len(miss),
        "missing_with_path": len(miss_with_path),
        "json": str(out), "doc": str(doc),
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
