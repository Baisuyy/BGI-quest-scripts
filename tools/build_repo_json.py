#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 BetterGI 脚本仓库索引 repo.json。

BetterGI 的「脚本仓库」不是扫 manifest.json，而是读仓库根的 repo.json：
  indexes 里必须有名为 js 的根节点，其下每个目录节点若带 version（或 children 为空、
  或 children 里含 manifest.json）即视为一个脚本项目。
订阅后 Pulonia 按索引路径直接读仓库快照运行脚本，因此 js 下可以保留多级目录。

用法（仓库根目录）：
    python tools/build_repo_json.py

新增任务后重跑本脚本即可，repo.json 会按当前 js/ 目录重建。
"""

import json
import re
import subprocess
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
JS_DIR = ROOT / "js"
OUT = ROOT / "repo.json"

# 篇章显示顺序（未列出的排在后面，按名称排序）
CHAPTER_ORDER = [
    "序章", "第一章", "第二章", "第三章", "第四章-枫丹", "第四章-旅人章",
    "第五章-纳塔", "第五章-旅人章", "空月之歌-挪德卡莱", "第六章", "第七章-至冬",
]
CHINESE_NUM = {"序奏": 0, "序章": 0, "幕间": 99,
               "一": 1, "二": 2, "三": 3, "四": 4, "五": 5,
               "六": 6, "七": 7, "八": 8, "九": 9, "十": 10, "十一": 11}


def chapter_rank(name: str) -> int:
    return CHAPTER_ORDER.index(name) if name in CHAPTER_ORDER else len(CHAPTER_ORDER)


def act_rank(name: str) -> tuple:
    m = re.match(r"^(序奏|序章|幕间|第)([一二三四五六七八九十]+)?", name)
    if not m:
        return (50, name)
    head, num = m.group(1), m.group(2)
    if num and num in CHINESE_NUM:
        return (CHINESE_NUM[num], name)
    return (CHINESE_NUM.get(head, 50), name)


def task_rank(name: str) -> tuple:
    m = re.match(r"^(\d+)", name)
    if name.startswith("全流程"):
        return (10**9, name)          # 全流程排在幕内最后
    return (int(m.group(1)) if m else 10**8, name)


def sort_key(path: Path, depth: int):
    name = path.name
    if depth == 0:
        return (chapter_rank(name), name)
    if depth == 1:
        return act_rank(name)
    return task_rank(name)


def git(*args: str) -> str:
    try:
        return subprocess.run(["git", *args], cwd=ROOT, capture_output=True,
                              text=True, check=True).stdout.strip()
    except Exception:
        return ""


def build_node(directory: Path, depth: int) -> dict:
    """目录节点；含 manifest.json 的目录是项目节点，不再挂 children。"""
    manifest_path = directory / "manifest.json"
    if manifest_path.is_file():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        files = [p for p in directory.rglob("*") if p.is_file()]
        mtime = max((p.stat().st_mtime for p in files), default=directory.stat().st_mtime)
        authors = manifest.get("authors") or (
            [{"name": manifest["author"]}] if manifest.get("author") else [])
        return {
            "name": directory.name,
            "type": "directory",
            "version": manifest.get("version", "1.0.0"),
            "author": authors[0]["name"] if authors else "",
            "authors": authors,
            "description": manifest.get("description", ""),
            "tags": [],
            "lastUpdated": datetime.fromtimestamp(mtime).strftime("%Y-%m-%d %H:%M:%S"),
        }

    children = []
    for child in sorted((p for p in directory.iterdir() if p.is_dir()),
                        key=lambda p: sort_key(p, depth)):
        if child.name.startswith("."):
            continue
        children.append(build_node(child, depth + 1))

    return {"name": directory.name, "type": "directory", "children": children}


def main() -> None:
    if not JS_DIR.is_dir():
        raise SystemExit(f"找不到脚本目录：{JS_DIR}")

    node = build_node(JS_DIR, 0)
    node["name"] = "js"          # BetterGI 硬编码读取 js 根节点

    index = {
        "time": datetime.now().strftime("%Y%m%d%H%M%S"),
        "url": "https://github.com/Baisuyy/bettergi-quest-scripts/archive/refs/heads/main.zip",
        "file": "repo.json",
        "build": {
            "version": 1,
            "sourceCommit": git("rev-parse", "HEAD"),
        },
        "indexes": [node],
    }

    OUT.write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    projects = sum(1 for _ in JS_DIR.rglob("manifest.json"))
    depth = max(len(p.relative_to(JS_DIR).parts) for p in JS_DIR.rglob("manifest.json"))
    print(f"已生成 {OUT.relative_to(ROOT)}：{projects} 个项目，最大层级 {depth}，"
          f"{OUT.stat().st_size / 1024:.0f} KB")


if __name__ == "__main__":
    main()
