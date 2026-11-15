#!/usr/bin/env python3
"""通过 WebDAV 把构建产物传到 123 云盘。

用法::

    PAN123_WEBDAV_USER=13212422786 PAN123_WEBDAV_PASS=应用密码 \
        python tools/bgi-special/upload_123pan_webdav.py \
        --dir dist --remote /BetterGI/0.67.0-special.7.2 \
        --file a.7z --file b.exe

凭据说明（重要）：
    用户名 = 123 云盘登录手机号
    密码   = 「应用密码」，不是登录密码，也不是网页 token。
             在 123 云盘 App：我的 → 第三方挂载 → 添加应用（会员权益），
             或在网页端 工具中心 → 第三方挂载 生成。
    服务器 = https://webdav.123pan.cn/webdav

设计要点：
    * 只做「把文件原样放上去」这一件事，不删不改已有文件；
    * 目录用 MKCOL 逐级创建，已存在（405/301）视为正常；
    * 上传用流式 body，内存占用与文件大小无关；
    * 任何一步失败都以非 0 退出，交给调用方决定是否让整轮构建失败。
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path
from urllib.parse import quote, urljoin

import requests
from requests.auth import HTTPBasicAuth

DEFAULT_URL = "https://webdav.123pan.cn/webdav"
TIMEOUT = (20, 1800)  # 连接 20s，单次读写最长 30 分钟
CHUNK = 1 << 20  # 1 MiB


def log(msg: str) -> None:
    print(f"[webdav] {msg}", flush=True)


def build_path(base: str, remote: str) -> str:
    """把 base 与远程路径拼成规范 URL，逐段做百分号编码（中文/空格安全）。"""
    remote = "/" + remote.strip("/")
    parts = [quote(p, safe="") for p in remote.split("/") if p]
    return base.rstrip("/") + "/" + "/".join(parts)


def ensure_dir(session: requests.Session, base: str, remote: str) -> None:
    """逐级 MKCOL，已存在不报错。"""
    cur = base.rstrip("/")
    for seg in [p for p in remote.strip("/").split("/") if p]:
        cur = cur + "/" + quote(seg, safe="")
        r = session.request("MKCOL", cur + "/", timeout=TIMEOUT)
        # 201 新建成功；405 已存在；301/302 部分实现会重定向；409 父目录缺失（理论上不会）
        if r.status_code in (201, 405, 301, 302):
            continue
        if r.status_code in (401, 403):
            raise SystemExit(f"MKCOL {cur} 鉴权失败（{r.status_code}），检查用户名/应用密码")
        log(f"MKCOL {cur} -> {r.status_code}（忽略，继续尝试上传）")


def put_file(session: requests.Session, base: str, remote_dir: str, path: Path) -> None:
    url = build_path(base, f"{remote_dir}/{path.name}")
    size = path.stat().st_size
    log(f"上传 {path.name} ({size / 1048576:.1f} MiB) -> {url}")
    started = time.time()
    with path.open("rb") as fh:
        r = session.put(url, data=fh, timeout=TIMEOUT)
    if r.status_code not in (200, 201, 204):
        raise SystemExit(f"PUT {path.name} 失败：HTTP {r.status_code} {r.text[:200]}")
    cost = time.time() - started
    speed = size / 1048576 / cost if cost > 0 else 0
    log(f"完成 {path.name}：HTTP {r.status_code}，{cost:.1f}s，{speed:.1f} MiB/s")


def main() -> int:
    ap = argparse.ArgumentParser(description="用 WebDAV 上传构建产物到 123 云盘")
    ap.add_argument("--dir", default="dist", help="本地待上传目录")
    ap.add_argument("--remote", required=True, help="云盘目标目录，如 /BetterGI/0.67.0-special.7.2")
    ap.add_argument("--file", action="append", default=[], help="文件名，可重复；缺省则上传目录下全部文件")
    ap.add_argument("--url", default=os.environ.get("PAN123_WEBDAV_URL", DEFAULT_URL))
    ap.add_argument("--dry-run", action="store_true", help="只打印计划，不发请求")
    args = ap.parse_args()

    user = os.environ.get("PAN123_WEBDAV_USER", "").strip()
    password = os.environ.get("PAN123_WEBDAV_PASS", "").strip()
    if not (user and password):
        log("缺少 PAN123_WEBDAV_USER / PAN123_WEBDAV_PASS，跳过上传")
        return 2

    src = Path(args.dir)
    if not src.is_dir():
        log(f"目录不存在：{src}")
        return 3

    names = args.file or sorted(p.name for p in src.iterdir() if p.is_file())
    targets = [src / n for n in names]
    missing = [p.name for p in targets if not p.is_file()]
    if missing:
        log(f"找不到文件：{', '.join(missing)}")
        return 4

    log(f"目标 {args.url} {args.remote}，共 {len(targets)} 个文件")
    for p in targets:
        log(f"  - {p.name} ({p.stat().st_size / 1048576:.1f} MiB)")
    if args.dry_run:
        return 0

    session = requests.Session()
    session.auth = HTTPBasicAuth(user, password)
    session.headers["User-Agent"] = "BGI-special-release/1.0"

    # 先探一下根目录，明确区分「密码错」和「目录不存在」
    probe = session.request("PROPFIND", args.url.rstrip("/") + "/", headers={"Depth": "0"}, timeout=TIMEOUT)
    if probe.status_code == 401:
        log("PROPFIND 401：用户名或应用密码不对（应用密码不是登录密码）")
        return 5
    log(f"PROPFIND 根目录 -> {probe.status_code}")

    ensure_dir(session, args.url, args.remote)
    failed = 0
    for p in targets:
        try:
            put_file(session, args.url, args.remote, p)
        except SystemExit as e:
            log(str(e))
            failed += 1
    if failed:
        log(f"{failed} 个文件失败")
        return 6
    log("全部上传完成")
    return 0


if __name__ == "__main__":
    sys.exit(main())
