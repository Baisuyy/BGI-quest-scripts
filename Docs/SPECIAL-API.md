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

## 4. 用任务文字选路线段

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

推荐走 Actions：在仓库 Actions 页运行 **Build BGI Special**，可指定分支、版本号、是否发 Release；向 `bgi-special` 分支推送代码也会自动构建。产物为完整目录压缩包 `BetterGI_v<版本>_special-win-x64.7z` 与单独的 `BetterGI.exe`，挂在 Releases（prerelease）。

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

`bgi-special` 是单提交的源码快照，不带上游历史。`.github/workflows/sync-upstream.yml` 每天 05:17 拉一次上游默认分支，用 `tools/bgi-special/sync_source.py` 覆盖源码，再用 `patch_engine_extend.py` 把宿主注册重新注入，成功后自动触发构建。

上游若改动 `EngineExtend.InitHost` 的结构导致注入锚点失效，同步会失败并开 issue，需要人工调整脚本或 `VisionHost.cs`。

## 改动清单

| 文件 | 改动 |
|---|---|
| `Core/Script/Dependence/VisionHost.cs` | 新增 `OcrHost` / `QuestHost` / `OcrLine` / `QuestTracker` |
| `Core/Script/EngineExtend.cs` | `InitHost` 中注册 `ocr` `quest` `OcrFactory` `Bv`，共 4 行 |

没有修改任何既有方法、配置项或默认行为。
