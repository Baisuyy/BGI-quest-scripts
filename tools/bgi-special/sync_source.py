#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用上游快照覆盖当前源码，同时保留特别版自有文件。

用法：python3 tools/bgi-special/sync_source.py <上游目录>

行为：
1. 删除当前目录中「上游没有、且不在保留清单」的文件（含上游已废弃的源码）
2. 用上游文件覆盖当前目录（保留清单内的文件不覆盖）
3. 清理空目录

不处理 .git，也不做 Git 操作。
"""

import pathlib
import shutil
import sys

# 相对仓库根路径；以 / 结尾表示整个目录
KEEP = (
    ".github/workflows/build-special.yml",
    ".github/workflows/sync-upstream.yml",
    ".bgi-upstream",
    "Docs/SPECIAL-API.md",
    "Docs/示例-读任务面板/",
    "BetterGenshinImpact/Core/Script/Dependence/VisionHost.cs",
    "tools/bgi-special/",
)


def kept(rel: str) -> bool:
    return any(rel == k or (k.endswith("/") and rel.startswith(k)) for k in KEEP)


def files_of(root: pathlib.Path):
    for p in root.rglob("*"):
        if p.is_file() and ".git" not in p.relative_to(root).parts:
            yield p.relative_to(root).as_posix()


def main() -> int:
    if len(sys.argv) != 2:
        print("用法: sync_source.py <上游目录>", file=sys.stderr)
        return 1

    upstream = pathlib.Path(sys.argv[1]).resolve()
    if not (upstream / "BetterGenshinImpact").is_dir():
        print(f"[x] {upstream} 不像 BetterGI 源码目录", file=sys.stderr)
        return 2

    local = pathlib.Path.cwd()
    up_files = set(files_of(upstream))
    local_files = set(files_of(local))

    removed = 0
    for rel in sorted(local_files - up_files):
        if kept(rel):
            continue
        (local / rel).unlink()
        removed += 1

    copied = 0
    for rel in sorted(up_files):
        if kept(rel):
            continue
        dst = local / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(upstream / rel, dst)
        copied += 1

    # 清空目录，保留 .git
    for d in sorted((p for p in local.rglob("*") if p.is_dir()), key=lambda p: len(p.parts), reverse=True):
        if ".git" in d.relative_to(local).parts:
            continue
        try:
            d.rmdir()
        except OSError:
            pass

    print(f"[+] 删除 {removed} 个文件，覆盖 {copied} 个文件")
    return 0


if __name__ == "__main__":
    sys.exit(main())
