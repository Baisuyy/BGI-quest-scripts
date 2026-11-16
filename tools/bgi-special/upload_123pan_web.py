#!/usr/bin/env python3
"""用 123 云盘网页登录态（authorToken）上传文件。

网页端上传分四步，这里照抄：
  1. POST /b/api/file/upload_request        建单，拿 uploadId / sliceSize / key
  2. POST /b/api/file/s3_upload_object/auth  按分片号区间换预签名 URL（20 分钟有效）
  3. PUT  {presignedUrl}                     逐片上传，每片 32MB
  4. POST /b/api/file/upload_complete/v2     告诉服务端合并

注意事项：
  * token 是单设备会话，网页退出登录或在别处登录就会失效，需要重新取
  * 分片是流式读的，内存占用约等于「分片大小 × 并发数」，与文件多大无关
  * 预签名 20 分钟过期，单片失败会重新换一批签名再试

用法：
  python upload_123pan_web.py --token <authorToken> --file a.exe --parent-id 38782849
  PAN123_WEB_TOKEN=... python upload_123pan_web.py --file a.exe
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import os
import sys
import time
from typing import Any

import requests

API = "https://api.123278.com/b/api"
TIMEOUT = 60
PART_RETRY = 4
# 云盘上特别版的归档目录，安装包默认落在这里，不再散在根目录
DEFAULT_PARENT_NAME = "BGI.special"


class PanError(RuntimeError):
    pass


class PanWebClient:
    def __init__(self, token: str) -> None:
        self.session = requests.Session()
        self.session.headers.update({
            "Authorization": f"Bearer {token}",
            "platform": "web",
            "App-Version": "3",
            "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                           "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"),
            "Accept": "application/json, text/plain, */*",
        })

    def _call(self, path: str, payload: dict[str, Any] | None = None,
              method: str = "POST") -> dict[str, Any]:
        url = f"{API}/{path.lstrip('/')}"
        if method == "GET":
            resp = self.session.get(url, timeout=TIMEOUT)
        else:
            resp = self.session.post(url, json=payload or {}, timeout=TIMEOUT)
        if resp.status_code == 401:
            raise PanError("登录态已失效（HTTP 401）：token 是单设备会话，请重新登录网页端再取一次")
        try:
            body = resp.json()
        except ValueError:
            raise PanError(f"{path} 返回非 JSON（HTTP {resp.status_code}）：{resp.text[:200]}")

        if body.get("code") != 0:
            raise PanError(f"{path} 失败：code={body.get('code')} {body.get('message')}")
        return body.get("data") or {}

    def _get(self, path: str, params: dict[str, Any]) -> dict[str, Any]:
        """带查询参数的 GET。文件列表这类接口只认 query，不认 JSON body。"""
        url = f"{API}/{path.lstrip('/')}"
        resp = self.session.get(url, params=params, timeout=TIMEOUT)
        if resp.status_code == 401:
            raise PanError("登录态已失效（HTTP 401）：token 是单设备会话，请重新登录网页端再取一次")
        try:
            body = resp.json()
        except ValueError:
            raise PanError(f"{path} 返回非 JSON（HTTP {resp.status_code}）：{resp.text[:200]}")
        if body.get("code") != 0:
            raise PanError(f"{path} 失败：code={body.get('code')} {body.get('message')}")
        return body.get("data") or {}

    def whoami(self) -> dict[str, Any]:
        return self._call("user/info", method="GET")

    def list_dir(self, parent_id: int = 0, limit: int = 100) -> list[dict[str, Any]]:
        """列出一层目录内容。网页端的列表接口走 GET + query，event 固定为 homeListFile。"""
        data = self._get("file/list/new", {
            "driveId": 0,
            "limit": limit,
            "next": 0,
            "orderBy": "file_name",
            "orderDirection": "asc",
            "parentFileId": parent_id,
            "trashed": "false",
            "SearchData": "",
            "Page": 1,
            "OnlyLookAbnormalFile": 0,
            "event": "homeListFile",
            "operateType": 4,
        })
        return data.get("InfoList") or []

    def resolve_parent(self, name: str, fallback: int = 0) -> int:
        """按目录名在根目录找文件夹 ID。

        云盘的目录 ID 每个账号都不一样，配置里写死 ID 换个账号就失效，
        所以默认按名字找；找不到就退回 fallback（根目录），由调用方提示用户。
        """
        name = (name or "").strip().strip("/")
        if not name:
            return fallback
        try:
            items = self.list_dir(0)
        except PanError as exc:
            print(f"列目录失败（{exc}），退回根目录上传", file=sys.stderr)
            return fallback
        for item in items:
            # Type: 0 文件 / 1 文件夹
            if str(item.get("FileName")) == name and int(item.get("Type") or 0) == 1:
                file_id = int(item.get("FileId") or 0)
                print(f"目标目录 {name} → parentFileId={file_id}")
                return file_id
        print(f"根目录下没有名为 {name} 的文件夹，先传到根目录；"
              f"需要归档请用 --parent-id 指定目录 ID", file=sys.stderr)
        return fallback

    def upload(self, path: str, parent_id: int, workers: int = 3) -> dict[str, Any]:
        name = os.path.basename(path)
        size = os.path.getsize(path)
        etag = md5_of(path)
        print(f"[{name}] {size / 1048576:.1f} MB, md5={etag}")

        info = self._call("file/upload_request", {
            "driveId": 0,
            "etag": etag,
            "fileName": name,
            "parentFileId": parent_id,
            "size": size,
            "type": 0,
            "duplicate": 2,
        })

        if info.get("Reuse"):
            # 服务端已有同 MD5 的 blob，建个引用就完事
            print(f"[{name}] 秒传命中")
            return {"name": name, "size": size, "reuse": True}

        file_id = info["FileId"]
        upload_id = info["UploadId"]
        bucket = info["Bucket"]
        key = info["Key"]
        node = info["StorageNode"]
        slice_size = int(info["SliceSize"])
        parts = max(1, (size + slice_size - 1) // slice_size)
        print(f"[{name}] uploadId={upload_id[:16]}… 分片 {parts} × {slice_size / 1048576:.0f} MB @ {node}")

        urls = self.presign(bucket, key, upload_id, node, 1, parts)
        started = time.time()

        # 服务端有时把这个 uploadId 建成「单片会话」：不管 partNumber 传什么都只签第 1 片。
        # 实测这种会话可以直接把整个文件 PUT 到 part 1（S3 单片上限 5GB），然后按
        # isMultipart=false 收尾，比死等第 2 片签名可靠。
        if len(urls) < parts:
            if size > 4 * 1024 ** 3:
                raise PanError(
                    f"服务端只签了 {len(urls)} 片（需要 {parts} 片），"
                    f"而文件超过 4GB 不能整包直传，请改用开放平台接口")
            print(f"[{name}] 只拿到 1 片签名，改为整包直传（{size / 1048576:.1f} MB）")
            if not self.put_whole(path, urls[1]):
                raise PanError("整包直传失败")
            urls = {1: ""}
            parts = 1
        else:
            done = 0
            failed: list[int] = []

            def send(part: int) -> tuple[int, bool]:
                ok = self.put_part(path, urls[part], part, slice_size, size)
                return part, ok

            with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
                futures = [pool.submit(send, p) for p in range(1, parts + 1)]
                for fut in concurrent.futures.as_completed(futures):
                    part, ok = fut.result()
                    done += 1
                    if not ok:
                        failed.append(part)
                    pct = done * 100 // parts
                    speed = size * done / parts / 1048576 / max(time.time() - started, 0.001)
                    print(f"\r[{name}] {done}/{parts} 片 ({pct}%) {speed:.1f} MB/s", end="", flush=True)
            print()

            if failed:
                # 签名可能过期，重新换一批再传剩下的
                print(f"[{name}] 重传失败分片 {sorted(failed)}")
                retry_urls = self.presign(bucket, key, upload_id, node, 1, parts)
                for part in sorted(failed):
                    if not self.put_part(path, retry_urls[part], part, slice_size, size):
                        raise PanError(f"分片 {part} 重试后仍失败")

        result = self._call("file/upload_complete/v2", {
            "fileId": file_id,
            "bucket": bucket,
            "fileSize": size,
            "key": key,
            "isMultipart": parts > 1,
            "uploadId": upload_id,
            "StorageNode": node,
        })
        info2 = result.get("file_info") or {}
        print(f"[{name}] 完成，FileId={info2.get('FileId')} 用时 {time.time() - started:.0f}s")
        return {"name": name, "size": size, "file_id": info2.get("FileId")}

    def presign(self, bucket: str, key: str, upload_id: str, node: str,
                start: int, end: int) -> dict[int, str]:
        data = self._call("file/s3_upload_object/auth", {
            "bucket": bucket,
            "key": key,
            "partNumberStart": start,
            "partNumberEnd": end,
            "uploadId": upload_id,
            "StorageNode": node,
        })
        raw = data.get("presignedUrls") or {}
        if not raw:
            raise PanError("没有拿到预签名地址，接口返回为空")
        return {int(k): v for k, v in raw.items()}

    def put_whole(self, path: str, url: str) -> bool:
        """把整个文件 PUT 到 part 1。流式上传，内存里只留 requests 的缓冲。"""
        for attempt in range(1, PART_RETRY + 1):
            try:
                with open(path, "rb") as fh:
                    resp = requests.put(url, data=fh, timeout=3600,
                                        headers={"Content-Type": "application/octet-stream"})
                if resp.status_code in (200, 201):
                    return True
                print(f"整包直传返回 HTTP {resp.status_code}（第 {attempt} 次）：{resp.text[:120]}")
            except requests.RequestException as exc:
                print(f"整包直传异常 {exc}（第 {attempt} 次）")
            time.sleep(min(2 ** attempt, 10))
        return False

    def put_part(self, path: str, url: str, part: int, slice_size: int, total: int) -> bool:
        offset = (part - 1) * slice_size
        length = min(slice_size, total - offset)
        for attempt in range(1, PART_RETRY + 1):
            try:
                with open(path, "rb") as fh:
                    fh.seek(offset)
                    data = fh.read(length)
                # 预签名按 host 签名，不能再加 Authorization 之类的头
                resp = requests.put(url, data=data, timeout=300,
                                    headers={"Content-Type": "application/octet-stream"})
                if resp.status_code in (200, 201):
                    return True
                if resp.status_code in (401, 403):
                    print(f"\n分片 {part} 签名失效（HTTP {resp.status_code}），重试")
                    return False
                print(f"\n分片 {part} 失败 HTTP {resp.status_code}（第 {attempt} 次）")
            except requests.RequestException as exc:
                print(f"\n分片 {part} 异常 {exc}（第 {attempt} 次）")
            time.sleep(min(2 ** attempt, 10))
        return False


def md5_of(path: str, chunk: int = 4 << 20) -> str:
    digest = hashlib.md5()
    with open(path, "rb") as fh:
        while True:
            block = fh.read(chunk)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description="用网页登录态上传到 123 云盘")
    parser.add_argument("--token", default=os.environ.get("PAN123_WEB_TOKEN", ""),
                        help="网页 localStorage 里的 authorToken")
    parser.add_argument("--file", action="append", default=[], help="要上传的本地文件，可重复")
    parser.add_argument("--dir", default="", help="本地目录，配合 --glob 用")
    parser.add_argument("--glob", default="", help="按通配符挑文件，例如 *.exe")
    parser.add_argument("--parent-id", type=int, default=None,
                        help="目标目录 ID；不填则按 --parent-name 在根目录按名字找")
    parser.add_argument("--parent-name", default=os.environ.get("PAN123_PARENT_NAME", DEFAULT_PARENT_NAME),
                        help=f"父目录名，默认 {DEFAULT_PARENT_NAME}")
    parser.add_argument("--workers", type=int, default=3, help="分片并发数")
    parser.add_argument("--whoami", action="store_true", help="只打印当前登录账号，不上传")
    args = parser.parse_args()

    if not args.token:
        print("缺少 token：--token 或环境变量 PAN123_WEB_TOKEN", file=sys.stderr)
        return 2

    files = list(args.file)
    if args.dir and args.glob:
        from glob import glob as _glob
        files += _glob(os.path.join(args.dir, args.glob))

    client = PanWebClient(args.token)

    try:
        if args.whoami or not files:
            me = client.whoami()
            print(json.dumps({k: me.get(k) for k in ("UID", "Nickname", "Passport")},
                             ensure_ascii=False))
            if not files:
                return 0

        parent_id = args.parent_id
        if parent_id is None:
            parent_id = client.resolve_parent(args.parent_name)
        print(f"上传目标 parentFileId={parent_id}")

        for path in files:
            if not os.path.isfile(path):
                print(f"跳过（不是文件）：{path}", file=sys.stderr)
                continue
            client.upload(path, parent_id, args.workers)
    except PanError as exc:
        print(f"失败：{exc}", file=sys.stderr)
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
