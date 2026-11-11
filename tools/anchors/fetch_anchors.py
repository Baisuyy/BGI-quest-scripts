#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""抓取米游社提瓦特大地图的传送锚点与七天神像。

用法：python3 tools/anchors/fetch_anchors.py

输出 tools/anchors/anchors_raw.json，字段：
    id        米游社点位 id
    kind      statue（七天神像） / waypoint（传送锚点）
    x, y      米游社地图坐标（**不是**游戏坐标，见 docs/锚点开启-调研.md）
    area_id   米游社区域编号
    z_level   分层编号（0 = 地表）

数据来自公开接口，仅供定位参考；坐标换算到游戏坐标需要另行标定。
"""

import json
import pathlib
import urllib.request

MAP_ID = 2  # 提瓦特大地图
POINT_URL = (
    f"https://api-static.mihoyo.com/common/map_user/ys_obc/v1/map/point/list"
    f"?map_id={MAP_ID}&app_sn=ys_obc"
)
LABEL_STATUE = 2
LABEL_WAYPOINT = 3
OUT = pathlib.Path(__file__).resolve().parent / "anchors_raw.json"


def fetch(url: str) -> dict:
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main() -> int:
    data = fetch(POINT_URL)
    if data.get("retcode") != 0:
        print("接口返回异常：", data.get("message"))
        return 1

    points = data["data"]["point_list"]
    anchors = []
    for p in points:
        label = p.get("label_id")
        if label not in (LABEL_STATUE, LABEL_WAYPOINT):
            continue
        anchors.append({
            "id": p["id"],
            "kind": "statue" if label == LABEL_STATUE else "waypoint",
            "x": round(p["x_pos"], 1),
            "y": round(p["y_pos"], 1),
            "area_id": p.get("area_id"),
            "z_level": p.get("z_level", 0),
        })

    anchors.sort(key=lambda a: (a["area_id"] or 0, a["kind"], a["id"]))
    payload = {
        "source": "米游社提瓦特大地图 point/list（label_id 2=七天神像, 3=传送锚点）",
        "coordinate_note": "x/y 为米游社地图坐标，不是 BGI 游戏坐标；换算方式待标定",
        "total": len(anchors),
        "statues": sum(1 for a in anchors if a["kind"] == "statue"),
        "waypoints": sum(1 for a in anchors if a["kind"] == "waypoint"),
        "anchors": anchors,
    }
    OUT.write_text(json.dumps(payload, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"已写出 {OUT.relative_to(OUT.parents[2])}：{payload['total']} 个（神像 {payload['statues']}，锚点 {payload['waypoints']}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
