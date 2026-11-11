#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按模板重写已发布 Release 的说明正文。

用法：
    GH_TOKEN=xxx python3 tools/bgi-special/update_release_notes.py [tag]

不传 tag 时处理所有以 special-v 开头的 Release。基线版本与源码提交从前一版正文的第一行里取，
取不到就跳过该 Release（避免把版本信息写成空值）。
"""

import json
import os
import re
import sys
import urllib.error
import urllib.request
import pathlib

API = "https://api.github.com"
REPO = os.environ.get("REPO", "Baisuyy/BGI-quest-scripts")
TOKEN = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
TEMPLATE = pathlib.Path(__file__).resolve().parents[2] / ".github" / "release-notes-template.md"

# BetterGI 特别版，基于上游 **0.66.3-alpha.3** 构建，源码 bgi-special @ 532f400。
HEADER = re.compile(r"基于上游\s*\*\*(?P<base>[^*]+)\*\*\s*构建，源码\s*(?P<ref>\S+)\s*@\s*(?P<sha>[0-9a-fA-F]{4,40})")


def request(method: str, path: str, payload=None):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(f"{API}{path}", data=data, method=method)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("Authorization", f"Bearer {TOKEN}")
    if data:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main() -> int:
    if not TOKEN:
        print("[x] 缺少 GH_TOKEN", file=sys.stderr)
        return 1
    if not TEMPLATE.is_file():
        print(f"[x] 找不到模板 {TEMPLATE}", file=sys.stderr)
        return 2

    template = TEMPLATE.read_text(encoding="utf-8")
    only = (sys.argv[1] if len(sys.argv) > 1 else os.environ.get("ONLY_TAG", "")).strip()

    releases = request("GET", f"/repos/{REPO}/releases?per_page=100")
    targets = [r for r in releases if r["tag_name"].startswith("special-v")]
    if only:
        targets = [r for r in targets if r["tag_name"] == only]
        if not targets:
            print(f"[x] 没有找到 Release {only}", file=sys.stderr)
            return 3

    changed = 0
    for rel in targets:
        tag = rel["tag_name"]
        head = (rel.get("body") or "").strip().splitlines()
        m = HEADER.search(head[0]) if head else None
        if not m:
            print(f"[!] {tag}：正文里没有版本/源码信息，跳过（避免写空）")
            continue

        body = (template
                .replace("{{VERSION}}", tag.removeprefix("special-v"))
                .replace("{{BASE}}", m.group("base").strip())
                .replace("{{REF}}", m.group("ref"))
                .replace("{{SHA}}", m.group("sha")))
        if body.strip() == (rel.get("body") or "").strip():
            print(f"[=] {tag}：已是新说明")
            continue

        try:
            request("PATCH", f"/repos/{REPO}/releases/{rel['id']}", {"body": body})
        except urllib.error.HTTPError as e:
            print(f"[x] {tag}：写入失败 {e.code} {e.read().decode('utf-8', 'ignore')[:200]}", file=sys.stderr)
            continue
        changed += 1
        print(f"[+] {tag}：已更新（基线 {m.group('base')} @ {m.group('sha')}）")

    print(f"完成：{changed} 个 Release 更新，共 {len(targets)} 个候选")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
