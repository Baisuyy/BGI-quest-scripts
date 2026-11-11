# BetterGI 特别版（源码分支）

本分支是 [BetterGI](https://github.com/babalae/better-genshin-impact) 的源码快照，额外开放了 4 个脚本宿主入口：

| 接口 | 用途 |
|---|---|
| `ocr.readScreen()` / `ocr.readRegion(x, y, w, h)` / `ocr.readLines(...)` | 屏幕或区域转文字 |
| `quest.tracker()` | 左上角任务追踪面板的标题与当前步骤 |
| `OcrFactory` / `Bv` | OCR 引擎与 BgiVision 静态判定 |

接口细节与示例见 [`Docs/SPECIAL-API.md`](Docs/SPECIAL-API.md)，可运行的自检脚本在 [`Docs/示例-读任务面板`](Docs/示例-读任务面板)。

## 构建

Actions 页运行 **Build BGI Special**，可选分支、版本号、是否发 Release；向本分支推送也会自动构建。产物挂到 Releases（prerelease），含完整目录 7z 与单独的 `BetterGI.exe`。

## 与上游的关系

- 本分支**只有一个提交**，不含上游历史，避免脚本仓库被几百 MB 历史撑大
- `Sync Upstream BGI` 每天 05:17 拉上游默认分支，覆盖源码后用 `tools/bgi-special/patch_engine_extend.py` 重新注入宿主注册
- 上游改动 `EngineExtend.InitHost` 结构时同步会失败并开 issue，需人工修脚本

改动仅两处：新增 `Core/Script/Dependence/VisionHost.cs`，`EngineExtend.InitHost` 里加 4 行注册。脚本不调用新 API 时，行为与官方版完全一致。
