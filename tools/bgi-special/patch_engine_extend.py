#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把特别版宿主注册注入 EngineExtend.InitHost，可重复执行。

上游同步会整体覆盖源码，本脚本负责把 ocr / quest / OcrFactory / Bv
四个入口重新挂回宿主。锚点缺失时返回非 0，由 workflow 报警。
"""

import pathlib
import sys

TARGET = pathlib.Path("BetterGenshinImpact/Core/Script/EngineExtend.cs")
ANCHOR = 'engine.AddHostObject("genshin", new Dependence.Genshin(runtime, allConfig, ct, screenshots));'

BLOCK = '''
        // ===== 特别版扩展：识图 / OCR / 任务追踪 =====
        // OCR：屏幕或指定区域转文字，供脚本做条件判断
        var ocrHost = new OcrHost(runtime);
        engine.AddHostObject("ocr", ocrHost);
        // 任务追踪：读取左上角任务面板的标题与当前步骤
        engine.AddHostObject("quest", new QuestHost(ocrHost));
        // 直接暴露 OCR 引擎与 BgiVision 静态方法，给高级脚本自行组合
        engine.AddHostType("OcrFactory", typeof(BetterGenshinImpact.Core.Recognition.OCR.OcrFactory));
        engine.AddHostType("Bv", typeof(BetterGenshinImpact.GameTask.Common.BgiVision.Bv));
'''


def main() -> int:
    if not TARGET.is_file():
        print(f"[x] 找不到 {TARGET}", file=sys.stderr)
        return 2

    text = TARGET.read_text(encoding="utf-8")
    if 'AddHostObject("ocr"' in text:
        print("[=] 宿主注册已存在，跳过")
        return 0

    if ANCHOR not in text:
        print(
            "[x] EngineExtend.cs 中没有找到 genshin 宿主注册锚点，"
            "上游结构可能已改动，需要人工调整 tools/bgi-special/patch_engine_extend.py",
            file=sys.stderr,
        )
        return 3

    text = text.replace(ANCHOR, ANCHOR + BLOCK, 1)
    TARGET.write_text(text, encoding="utf-8")
    print("[+] 已注入 ocr / quest / OcrFactory / Bv")
    return 0


if __name__ == "__main__":
    sys.exit(main())
