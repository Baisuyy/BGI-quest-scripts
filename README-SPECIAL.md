# BetterGI 特别版（源码分支）

本分支是 [BetterGI](https://github.com/babalae/better-genshin-impact) 的源码快照，额外做了两件事：**开放脚本感知接口**，以及**把大模型做进内核**。

## 一、内置 AI 决策

BGI 自己调用大模型，不是脚本、不是外挂：

- 跑图连续卡死、即将放弃路线时，把当前坐标、目标、运动状态交给模型
- 模型通过工具调用直接操作角色（走位 / 转镜头 / 跳跃 / 攻击 / 交互 / 传送 / 开关功能）
- 到达或拉近 5 米即交还规则逻辑，失败则按官方逻辑放弃
- 默认 6 步、45 秒、每小时 60 次；连续失败 3 次冷却 2 分钟

配置入口：**任务设置 → AI 智能决策（内置）**，支持 OpenAI 兼容（DeepSeek / 通义 / 智谱 / Kimi / Ollama / LM Studio）与 Anthropic 两种协议，卡片底部有连通性测试。

设计取舍、成本、失败行为、已知边界见 [`Docs/AI-内置决策.md`](Docs/AI-内置决策.md)。

接管过程在日志里是**一整块**：`[AI] ┌` 开头写触发原因与起点，中间每步标出「拉近 / 后退 / 无进展」，`[AI] └` 收尾写步数、请求数与耗时。日志文件在安装目录 `log/better-genshin-impact<日期>.log`，搜 `[AI]` 即可。

上机验证步骤与判定标准见 [`Docs/上机测试-内置AI.md`](Docs/上机测试-内置AI.md)。

## 二、脚本宿主接口

| 接口 | 用途 |
|---|---|
| `ocr.readScreen()` / `ocr.readRegion(x, y, w, h)` / `ocr.readLines(...)` | 屏幕或区域转文字 |
| `quest.tracker()` | 左上角任务追踪面板的标题与当前步骤 |
| `OcrFactory` / `Bv` | OCR 引擎与 BgiVision 静态判定 |
| `ai.Chat(user[, system])` | 调用同一个内核，密钥与预算都在宿主侧 |

接口细节与示例见 [`Docs/SPECIAL-API.md`](Docs/SPECIAL-API.md)，可运行的自检脚本在 [`Docs/示例-读任务面板`](Docs/示例-读任务面板)。

## 构建

Actions 页运行 **Build BGI Special**（仅手动触发），可选分支、版本号、是否发 Release。产物挂到 Releases（prerelease），含完整目录 7z 与单独的 `BetterGI.exe`。

## 与上游的关系

- 本分支**只有一个提交**，不含上游历史，避免脚本仓库被几百 MB 历史撑大
- `Sync Upstream BGI` 每天 05:17 拉上游默认分支，覆盖源码后用 `tools/bgi-special/patch_special.py` 重新注入宿主注册、AI 配置与卡死接管
- 新增文件在 `tools/bgi-special/sync_source.py` 的 KEEP 白名单里；上游文件上的改动用锚点补丁，锚点失效时同步会失败并开 issue

默认全部关闭：不开 AI、不调用新 API 时，行为与官方版完全一致。
