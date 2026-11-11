# BetterGI 特别版 · 脚本 API 扩展

在原版基础上**只增不改**：所有原有脚本行为、manifest 格式、路线格式完全不变，新增 4 个宿主对象/类型供脚本调用。

构建产物与官方发行版可互换，脚本里不调用新 API 时行为与官方一致。

---

## 1. `ocr` — 屏幕文字识别

把屏幕或任意区域转成文字。坐标默认按 **1080P 基准**书写，内部按当前窗口尺寸自动换算，换分辨率不用改脚本。

| 成员 | 返回 | 说明 |
|---|---|---|
| `ocr.width` / `ocr.height` | int | 当前截图的实际像素尺寸 |
| `ocr.scaleTo1080P` | double | 相对 1080P 的缩放比例 |
| `ocr.readScreen()` | string | 识别整屏，按阅读顺序拼接 |
| `ocr.readRegion(x, y, w, h, use1080P = true)` | string | 识别指定区域，`w/h` 传 0 表示到边缘 |
| `ocr.readLines(x, y, w, h, use1080P = true)` | OcrLine[] | 逐行结果，含坐标与置信度 |
| `ocr.saveRegion(path, x, y, w, h, use1080P = true)` | bool | 把区域存成图片，用来调坐标 |

`OcrLine` 字段：`text` `x` `y` `width` `height` `score`。

```js
// 读屏幕中央的交互提示
const text = ocr.readRegion(760, 620, 400, 80);
log.info('屏幕文字：' + text);

// 逐行拿坐标，自己做版面判断
for (const line of ocr.readLines(0, 0, 0, 0)) {
    log.info(`${line.text} @ ${Math.round(line.x)},${Math.round(line.y)}`);
}

// 调区域时先把图存下来看
ocr.saveRegion('debug/panel.png', 28, 118, 430, 190);
```

---

## 2. `quest` — 任务追踪面板

直接读左上角任务追踪，返回标题与当前步骤。区域按窗口比例计算，默认值对应 1080P 下 `(28,118) 430×190`。

```js
const t = quest.tracker();
log.info('任务：' + t.title);   // 例如「僭越者的试炼」
log.info('步骤：' + t.step);    // 例如「回到至冬宫分享与更新情报」
log.info('原始行：\n' + t.rawText);
```

识别不准时手动指定区域（同样按 1080P 基准）：

```js
const t = quest.tracker(20, 100, 460, 220);
```

判定方式：把同一水平线上的文本块合并成一行，再取**行高最大**的那行作为标题（游戏里任务名比步骤字号大），其余拼成步骤。所以任务面板关掉、或换成其他 UI 时，`title` 会为空。

---

## 3. `OcrFactory` / `Bv` — 底层能力

给需要自己组合识别逻辑的脚本用。

```js
// 直接调 OCR 引擎（参数是 OpenCvSharp.Mat）
const ra = captureGameRegion();
const lines = OcrFactory.Paddle.OcrResult(ra.SrcMat).Regions;
ra.dispose();

// BgiVision 的现成判定
const bigMap = Bv.IsInBigMapUi(captureGameRegion());
```

`Bv` 是 `BgiVision` 的静态工具类，原版内部在用（是否在对话、是否在大地图、F 键文本等），这里原样开放。

---

## 4. `ai` — 大模型调用

脚本不需要自己配密钥：**任务设置 → AI 智能决策（内置）** 卡片里填一次，内置决策与所有脚本共用同一份配置、同一份预算。
内置决策（跑图卡死接管）的完整说明见 [`AI-内置决策.md`](AI-内置决策.md)。

`ai` 只是内核 `AiKernel` 的一层薄壳：能聊天、能读状态，读不到 apiKey，也绕不过每小时上限与熔断。

| 属性 | 含义 |
|---|---|
| `ai.Enabled` | 总开关，关掉后 `Chat` 直接抛异常，不发请求 |
| `ai.Model` / `ai.Endpoint` | 当前配置，只读，可用于打日志 |
| `ai.VisionEnabled` | 视觉模式是否为 `multimodal` |
| `ai.Calls` / `ai.MaxCalls` | 本次已用次数 / 上限 |

| 方法 | 说明 |
|---|---|
| `ai.Chat(user)` | 单轮对话，返回文本 |
| `ai.Chat(user, system)` | 带系统提示的单轮对话 |

```js
if (ai.Enabled) {
    const text = await ai.Chat(
        '目标：走到凯瑟琳面前。当前坐标 2280,-820。只输出一个 JSON 动作。',
        '你是原神自动化脚本的决策模块，动作只有 goto/tp/interact/wait/done/fail。'
    );
    log.info(text);
}
```

服务地址填到 `/v1`，客户端自行拼 `/chat/completions`；选 Anthropic 协议则填根地址，客户端拼 `/v1/messages`。
密钥只在宿主内存里，脚本拿不到原文；HTTPS 强制、超时、每小时与单次运行上限、连续失败冷却也都在宿主侧，脚本绕不过去。

## 5. 用任务文字选路线段

把 `quest.tracker()` 的步骤文字和脚本里的段标签做匹配，就能从「当前该做哪一步」决定跑哪段：

```js
function pickSegByQuest(segments) {
    const t = quest.tracker();
    if (!t.step) return null;
    let best = null, bestScore = 0;
    for (const seg of segments) {
        const score = similarity(t.step, seg.label);
        if (score > bestScore) { bestScore = score; best = seg; }
    }
    return bestScore >= 0.5 ? best : null;   // 阈值按实际情况调
}

// 汉字二元组重合度，够用且不依赖分词
function similarity(a, b) {
    const grams = s => {
        const set = new Set();
        for (let i = 0; i < s.length - 1; i++) set.add(s.slice(i, i + 2));
        return set;
    };
    const A = grams(a.replace(/[\s→·，。]/g, '')), B = grams(b.replace(/[\s→·，。]/g, ''));
    if (!A.size || !B.size) return 0;
    let hit = 0;
    for (const g of A) if (B.has(g)) hit++;
    return hit / Math.max(A.size, B.size);
}
```

---

## 获取构建产物

推荐走 Actions：在仓库 Actions 页运行 **Build BGI Special**（仅手动触发，推代码不会自动构建），可指定分支、版本号、是否发 Release。产物为完整目录压缩包 `BetterGI_v<版本>_special-win-x64.7z` 与单独的 `BetterGI.exe`，挂在 Releases（prerelease）。

本地编译需要 .NET 10 SDK 与 Windows（WPF）：

```powershell
dotnet publish BetterGenshinImpact/BetterGenshinImpact.csproj -c Release -p:PublishProfile=FolderProfile
```

产物在 `BetterGenshinImpact/bin/x64/Release/net10.0-windows10.0.22621.0/publish/win-x64/`，单文件自包含，但 `Assets`、`dicts` 等资源仍需同目录。

Linux 上只能验证能否编译：

```bash
dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug -p:EnableWindowsTargeting=true
```

## 跟随上游

`bgi-special` 是单提交的源码快照，不带上游历史。`.github/workflows/sync-upstream.yml` 每天 05:17 拉一次上游默认分支，用 `tools/bgi-special/sync_source.py` 覆盖源码，再用 `patch_special.py` 把宿主注册、内置 AI 配置与卡死接管重新注入，成功后显式派发一次构建（`gh workflow run`），失败则开 issue。

新增文件按 `sync_source.py` 的 KEEP 白名单保留；改到上游文件里的补丁集中在 `patch_special.py`。上游若改动 `EngineExtend.InitHost`、`PathExecutor.MoveTo` 或任务设置页的结构导致锚点失效，同步会失败并开 issue，需要人工调整脚本。

## 改动清单

| 文件 | 改动 |
|---|---|
| `Core/Script/Dependence/VisionHost.cs` | 新增 `OcrHost` / `QuestHost` / `OcrLine` / `QuestTracker` |
| `Core/Ai/LlmClient.cs` | 传输层：OpenAI 兼容与 Anthropic 协议、工具声明、多轮展开 |
| `Core/Ai/AiKernel.cs` | 预算、熔断、超时、审计，全进程唯一大模型出口 |
| `Core/Ai/AiOperator.cs` | 工具循环：模型直接操作角色、传送、开关 BGI 功能 |
| `Core/Ai/GameStateProbe.cs` | 状态快照：坐标、运动状态、任务面板文字、压缩截图 |
| `Core/Config/AiConfig.cs` | 内置 AI 配置项（协议、地址、模型、密钥、接管开关、预算） |
| `Core/Script/Dependence/AiAgentHost.cs` | `ai` 宿主，薄壳调用 `AiKernel` |
| `GameTask/AutoPathing/PathExecutor.Ai.cs` | 第 3 次卡死时交给内置 AI 接管，成功率判定与交还 |
| `View/Controls/AiSettingsCard.xaml` | 独立设置卡片，避免同步时与页面耦合 |
| `Core/Config/AllConfig.cs` | 挂载 `AiConfig`，并兼容旧 `aiAgentConfig` 键 |
| `View/Pages/TaskSettingsPage.xaml` | 插入一行 `<controls:AiSettingsCard />` |
| `ViewModel/Pages/TaskSettingsPageViewModel.cs` | 新增连通性测试命令 |
| `Core/Script/EngineExtend.cs` | `InitHost` 中注册 `ocr` `quest` `OcrFactory` `Bv` `ai` |
| `App.xaml.cs` | 注册 `AiKernel` 单例 |

默认全部关闭：`AiConfig.Enabled` 为 `false` 时不抓帧、不发请求，脚本不调用 `ai.Chat()`、路线不卡死时与官方版完全一致。
