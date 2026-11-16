#!/usr/bin/env python3
"""把构建产物传到 123 云盘开放平台。

用法::

    PAN123_CLIENT_ID=xxx PAN123_CLIENT_SECRET=yyy \
        python tools/bgi-special/upload_123pan.py --dir dist \
        --parent-id 0 --file a.7z --file b.exe

凭据来自 123 云盘开放平台（https://www.123pan.com/developer）创建的应用，
不是网页登录拿到的 token：网页 token 只能读账号信息，文件接口会回「未登录」。

接口流程（官方 v1）：
    1. POST /api/v1/access_token          换取 access_token
    2. POST /upload/v1/file/create        建单，拿 preuploadID 与分片大小
    3. POST /upload/v1/file/get_upload_url 取每个分片的预签名地址
       PUT <presignedURL>                  直接传分片（不带鉴权头）
    4. POST /upload/v1/file/upload_complete 通知完成
    5. POST /upload/v1/file/upload_async_result 异步任务轮询结果

脚本只做一件事：把给定文件原样传上去。任何一步失败都以非 0 退出，
由调用方决定是不是要让整轮构建失败。
"""

from __future__ import annotations

import argparse
import hashlib
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any

import requests

BASE = "https://open-api.123pan.com"

# 平台标识，开放平台的每个请求都要带，缺了会返回「请在请求头中携带 platform 平台字段」
PLATFORM = "open_platform"

# 云盘上特别版的归档目录，安装包默认落在这里
DEFAULT_PARENT_NAME = "BGI.special"

# 分片并发。官方说明接口只支持两个同时请求，这里保守取 2。
CONCURRENCY = 2

# 单次请求超时：分片上传可能很慢，读写分开给
TIMEOUT = (30, 600)

# 异步上传结果最多轮询多久
ASYNC_TIMEOUT_SECONDS = 1800


def log(message: str) -> None:
    """打一行带时间戳的日志，刷 stdout，便于在 Actions 里实时看到进度。"""
    print(f"[{time.strftime('%H:%M:%S')}] {message}", flush=True)


def md5_of(path: Path) -> str:
    """算文件 MD5（接口用它判秒传，必须与文件内容一致）。"""
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def get_access_token(client_id: str, client_secret: str) -> str:
    """用 clientID + clientSecret 换一个短期 access_token。"""
    response = requests.post(
        f"{BASE}/api/v1/access_token",
        headers={"Platform": PLATFORM, "Content-Type": "application/json"},
        json={"clientID": client_id, "clientSecret": client_secret},
        timeout=TIMEOUT,
    )
    payload = response.json()
    if payload.get("code") != 0:
        raise RuntimeError(f"获取 access_token 失败：{payload.get('message')}（code={payload.get('code')}）")

    token = payload["data"]["accessToken"]
    log(f"access_token 已获取，有效期至 {payload['data'].get('expiredAt', '未知')}")
    return token


def auth_headers(token: str) -> dict[str, str]:
    """带鉴权的请求头。"""
    return {
        "Authorization": f"Bearer {token}",
        "Platform": PLATFORM,
        "Content-Type": "application/json",
    }


def resolve_parent(token: str, name: str, fallback: int = 0) -> int:
    """在根目录按名字找文件夹 ID。

    目录 ID 各账号不同，写死在配置里换个账号就失效，所以默认按名字找；
    找不到时退回 fallback（根目录），只提示不失败。
    """
    name = (name or "").strip().strip("/")
    if not name:
        return fallback
    try:
        response = requests.post(
            f"{BASE}/api/v1/file/list",
            headers=auth_headers(token),
            json={
                "parentFileId": 0,
                "limit": 100,
                "next": 0,
                "orderBy": "file_name",
                "orderDirection": "asc",
                "trashed": False,
                "searchData": "",
                "Page": 1,
                "onlyLookAbnormalFile": 0,
            },
            timeout=TIMEOUT,
        )
        payload = response.json()
        if payload.get("code") != 0:
            log(f"列目录失败：{payload.get('message')}，退回根目录")
            return fallback
        data = payload.get("data") or {}
        # 开放平台不同版本字段名不一致，两种都认
        infos = data.get("InfoList") or data.get("fileList") or []
        for item in infos:
            if str(item.get("FileName")) == name and int(item.get("Type") or 0) == 1:
                file_id = int(item.get("FileId") or 0)
                log(f"目标目录 {name} → parentFileId={file_id}")
                return file_id
    except Exception as error:  # noqa: BLE001 - 只是找目录，失败不该阻断上传
        log(f"列目录异常：{error}，退回根目录")
        return fallback
    log(f"根目录下没有名为 {name} 的文件夹，先传到根目录")
    return fallback


def create_upload(token: str, parent_id: int, path: Path, size: int, etag: str) -> dict[str, Any]:
    """建一个上传任务。已在云盘里的文件会直接秒传（reuse=true）。"""
    response = requests.post(
        f"{BASE}/upload/v1/file/create",
        headers=auth_headers(token),
        json={
            "parentFileID": parent_id,
            "filename": path.name,
            "etag": etag,
            "size": size,
            "duplicate": 1,
        },
        timeout=TIMEOUT,
    )
    payload = response.json()
    if payload.get("code") != 0:
        raise RuntimeError(f"创建上传任务失败：{payload.get('message')}（code={payload.get('code')}）")
    return payload["data"]


def upload_slice(token: str, preupload_id: str, slice_no: int, data: bytes) -> None:
    """取预签名地址并 PUT 一个分片。预签名请求不能带鉴权头，否则签名对不上。"""
    response = requests.post(
        f"{BASE}/upload/v1/file/get_upload_url",
        headers=auth_headers(token),
        json={"preuploadID": preupload_id, "sliceNo": slice_no},
        timeout=TIMEOUT,
    )
    payload = response.json()
    if payload.get("code") != 0:
        raise RuntimeError(f"取分片地址失败（slice {slice_no}）：{payload.get('message')}")

    url = payload["data"]["presignedURL"]
    put = requests.put(url, data=data, timeout=TIMEOUT)
    if put.status_code >= 400:
        raise RuntimeError(f"上传分片 {slice_no} 失败：HTTP {put.status_code}")


def complete_upload(token: str, preupload_id: str) -> dict[str, Any]:
    """通知服务端所有分片已传完。可能返回异步任务，需要再轮询。"""
    response = requests.post(
        f"{BASE}/upload/v1/file/upload_complete",
        headers=auth_headers(token),
        json={"preuploadID": preupload_id},
        timeout=TIMEOUT,
    )
    payload = response.json()
    if payload.get("code") != 0:
        raise RuntimeError(f"完成上传失败：{payload.get('message')}")
    return payload["data"]


def wait_async_result(token: str, preupload_id: str) -> dict[str, Any]:
    """服务端合并分片是异步的，这里轮询到完成为止。"""
    deadline = time.time() + ASYNC_TIMEOUT_SECONDS
    while time.time() < deadline:
        response = requests.post(
            f"{BASE}/upload/v1/file/upload_async_result",
            headers=auth_headers(token),
            json={"preuploadID": preupload_id},
            timeout=TIMEOUT,
        )
        payload = response.json()
        if payload.get("code") != 0:
            raise RuntimeError(f"查询上传结果失败：{payload.get('message')}")

        data = payload.get("data") or {}
        if data.get("completed"):
            return data

        time.sleep(3)

    raise RuntimeError("等待服务端合并分片超时")


def upload_file(token: str, parent_id: int, path: Path) -> None:
    """完整上传一个文件：建单 → 分片 → 完成 → 等结果。"""
    size = path.stat().st_size
    log(f"开始上传 {path.name}（{size / 1024 / 1024:.1f} MB）")

    etag = md5_of(path)
    created = create_upload(token, parent_id, path, size, etag)

    if created.get("reuse"):
        log(f"{path.name} 已在云盘里，秒传完成（fileID={created.get('fileID')}）")
        return

    preupload_id = created["preuploadID"]
    slice_size = int(created["sliceSize"])
    total = max(1, (size + slice_size - 1) // slice_size)
    log(f"{path.name} 分 {total} 片，每片 {slice_size / 1024 / 1024:.1f} MB")

    # 先把分片读进内存再并发上传：文件句柄跨线程 seek 不可靠，
    # 而分片本身只有几十 MB，顺序读一遍比加锁简单。
    chunks: list[tuple[int, bytes]] = []
    with path.open("rb") as handle:
        for index in range(total):
            handle.seek(index * slice_size)
            chunks.append((index + 1, handle.read(slice_size)))

    done = 0
    with ThreadPoolExecutor(max_workers=CONCURRENCY) as pool:
        futures = {
            pool.submit(upload_slice_once, token, preupload_id, slice_no, chunk): slice_no
            for slice_no, chunk in chunks
        }
        for future in as_completed(futures):
            future.result()
            done += 1
            if done % 10 == 0 or done == total:
                log(f"{path.name} 分片 {done}/{total}")

    result = complete_upload(token, preupload_id)
    if result.get("async"):
        log(f"{path.name} 服务端合并中…")
        result = wait_async_result(token, preupload_id)

    log(f"{path.name} 上传完成（fileID={result.get('fileID')}）")


def upload_slice_once(token: str, preupload_id: str, slice_no: int, data: bytes) -> None:
    """带重试的分片上传：网络抖动在 Actions 里很常见。"""
    last: Exception | None = None
    for attempt in range(1, 4):
        try:
            upload_slice(token, preupload_id, slice_no, data)
            return
        except Exception as error:  # noqa: BLE001 - 网络异常类型很多，统一重试
            last = error
            log(f"分片 {slice_no} 第 {attempt} 次失败：{error}")
            time.sleep(attempt * 3)

    raise RuntimeError(f"分片 {slice_no} 连续失败：{last}")


def main() -> int:
    parser = argparse.ArgumentParser(description="上传构建产物到 123 云盘")
    parser.add_argument("--dir", default="dist", help="文件所在目录")
    parser.add_argument("--parent-id", type=int, default=None,
                        help="目标目录 ID；不填则按 --parent-name 在根目录按名字找")
    parser.add_argument("--parent-name", default=os.environ.get("PAN123_PARENT_NAME", DEFAULT_PARENT_NAME),
                        help=f"父目录名，默认 {DEFAULT_PARENT_NAME}")
    parser.add_argument("--file", action="append", default=[], help="要上传的文件名，可重复")
    parser.add_argument("--glob", default="", help="按通配符挑选文件，例如 *.7z")
    args = parser.parse_args()

    client_id = os.environ.get("PAN123_CLIENT_ID", "").strip()
    client_secret = os.environ.get("PAN123_CLIENT_SECRET", "").strip()
    if not client_id or not client_secret:
        log("未配置 PAN123_CLIENT_ID / PAN123_CLIENT_SECRET，跳过 123 网盘上传")
        return 0

    root = Path(args.dir)
    targets: list[Path] = []
    for name in args.file:
        candidate = root / name
        if candidate.is_file():
            targets.append(candidate)
        else:
            log(f"跳过（不存在）：{candidate}")

    if args.glob:
        targets.extend(sorted(root.glob(args.glob)))

    # 去重并保持顺序，避免同一个文件传两遍
    seen: set[Path] = set()
    unique: list[Path] = []
    for item in targets:
        resolved = item.resolve()
        if resolved in seen:
            continue
        seen.add(resolved)
        unique.append(item)

    if not unique:
        log("没有要上传的文件")
        return 0

    try:
        token = get_access_token(client_id, client_secret)
        parent_id = args.parent_id
        if parent_id is None:
            parent_id = resolve_parent(token, args.parent_name)
        for path in unique:
            upload_file(token, parent_id, path)
    except Exception as error:  # noqa: BLE001 - 顶层兜底，统一转成退出码
        log(f"上传失败：{error}")
        return 1

    log(f"全部完成，共 {len(unique)} 个文件")
    return 0


if __name__ == "__main__":
    sys.exit(main())
