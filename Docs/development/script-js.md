# JS 脚本开发

BGI 的 JS 脚本跑在 **V8（ClearScript）** 里，不是 Node —— 没有 `require`、没有 npm、没有 `process`。
能做什么全看宿主往引擎里塞了什么：按 键鼠、跑路线、打怪、识图、OCR、读任务面板、调大模型。

本文只写**这个版本里实际存在的东西**和踩坑点。官方 API 全表在
[www.bettergi.com/feats/autos/jsscript.html](https://www.bettergi.com/feats/autos/jsscript.html)，两边合起来看。

---

## 1. 两种形态

### 单文件脚本

一个 `.js` 丢进 `User/Script/`，直接选中运行。没有 manifest、没有设置项、不能引库。
适合一次性小工具、临时调试。

### 脚本项目（推荐）

```
MyScript/
├─ manifest.json      必需
├─ main.js            入口
├─ settings.json      可选：基础表单
├─ settings/
│  └─ index.html      可选：HTML 设置页
└─ lib/               被 import 的模块，需在 manifest 的 library 里声明
```

整个目录放进 `User/Script/`（或从脚本仓库安装）。界面里选的是目录，不是文件。

---

## 2. `manifest.json`

```json
{
  "manifest_version": 1,
  "name": "自动采集示例",
  "version": "1.0.0",
  "bgi_version": "0.67.0",
  "description": "演示用",
  "authors": [{ "name": "你的名字", "link": "https://github.com/you" }],
  "main": "main.js",
  "settings_ui": "settings.json",
  "settings_html": "settings/index.html",
  "scripts": ["main.js", "lib/util.js"],
  "library": ["lib"],
  "saved_files": ["config.json"],
  "http_allowed_urls": ["https://api.example.com/"]
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `manifest_version` | 是 | 目前 `1` |
| `name` | 是 | 显示名 |
| `version` | 是 | 语义化版本 |
| `main` | 是 | 入口文件，相对路径，文件必须存在 |
| `bgi_version` | 否 | 要求的最低 BGI 版本。**低于它只报警告不阻止运行** |
| `description` | 否 | 说明，界面显示前 6 行 |
| `authors` | 否 | `[{name, link}]` |
| `settings_ui` | 否 | 基础表单定义，见第 6 节 |
| `settings_html` | 否 | 自绘设置页，见 [js-script-html-settings.md](../technical/js-script-html-settings.md) |
| `scripts` | 否 | 声明参与编译的文件，多文件项目必填 |
| `library` | 否 | 作为搜索路径的目录，`import` 才能找到 |
| `saved_files` | 否 | 允许 `file` 读写的相对路径（不写就只能读脚本目录） |
| `http_allowed_urls` | 否 | **不写就完全不能发请求**，见第 7 节 |

`bgi_version` 的比较只比主版本号，`0.67.0-special.7.7.3` 与 `0.67.0` 视为同一档，写 `0.67.0` 不会被特别版拒绝。

---

## 3. 宿主对象清单

进脚本就能用的全局名字。这张表是**按本仓库源码逐个核对过的**，比任何 wiki 都准。

### 3.1 自动化

| 名字 | 用途 | 常用成员 |
| --- | --- | --- |
| `genshin` | 游戏进程、窗口、输入、传送 | `genshin.teleport(x,y)`、`genshin.moveTo`、`genshin.isInGame`、`genshin.switchParty(name)` |
| `pathingScript` | 执行路线任务 | `pathingScript.run(path)`、`pathingScript.runSync`、`pathingScript.setConfig` |
| `keyMouseScript` | 按键脚本（.txt 按键序列） | `keyMouseScript.run(...)` |
| `dispatcher` | 起一个独立任务跑内置功能 | `dispatcher.runRealtimeTrigger(...)`、`dispatcher.addTimer(...)` |
| `characterDevelopmentTask` | 角色养成任务 | `run`、`cancel` |
| `notification` | 系统通知 | `notification.send(...)` |

### 3.2 识别

| 名字 | 用途 |
| --- | --- |
| `captureGameRegion()` | 截一张游戏画面，返回 `Region`/`ImageRegion` |
| `ocr` | **特别版扩展**：屏幕/区域转文字，见 [SPECIAL-API.md](../SPECIAL-API.md) |
| `quest` | **特别版扩展**：读左上角任务面板的标题与步骤 |
| `OcrFactory` | OCR 引擎本体，`OcrFactory.Paddle.OcrResult(mat)` |
| `Bv` | BgiVision 静态判定：`Bv.IsInBigMapUi(r)`、`Bv.IsInDialogue(r)`、`Bv.GetFKeyText(r)` 等 |
| `yolo` | ONNX YOLO 推理 |
| `Mat` / `Region` / `ImageRegion` / `RecognitionObject` / `DesktopRegion` / `Point2f` | OpenCV 与识别基础类型 |
| `BvPage` / `BvLocator` / `BvImage` | 新版页面级识别 |
| `GameCaptureRegion` | 坐标换算静态方法 |
| `CombatScenes` / `Avatar` | 队伍与角色状态 |
| `OpenCvSharp` | 整个 OpenCvSharp 命名空间 |

### 3.3 大模型

| 名字 | 说明 |
| --- | --- |
| `ai` | **特别版扩展**。`ai.Chat(user, system?)`、`ai.Enabled`、`ai.Model`。密钥与预算都在宿主侧，脚本拿不到原文 |

配置在「任务设置 → AI 智能决策（内置）」里填一次，所有脚本共用。关掉总开关后 `ai.Chat` 直接抛异常。

### 3.4 环境与工具

| 名字 | 说明 |
| --- | --- |
| `log` | `log.debug/info/warn/error(msg, ...args)`，写进 BGI 日志文件 |
| `file` | 受限文件读写，根目录是脚本目录；只能在脚本目录和 `saved_files` 内操作 |
| `http` | HTTP 请求，**必须在 `http_allowed_urls` 里白名单** |
| `host` | ClearScript 宿主函数，`host.newVarOfArr`、`host.addReference` 等 |
| `htmlMask` | HTML 遮罩层，在游戏画面上叠自绘 UI |
| `strategyFile` | 战斗策略脚本读写 |
| `settings` | 设置页保存的对象。字段名与 `settings.json` 里的 `name` 对应 |

### 3.5 全局函数

`sleep(ms)` · `getVersion()` · `keyDown` / `keyUp` / `keyPress` · `moveMouseBy` / `moveMouseTo` · `click` ·
`leftButtonClick` / `leftButtonDown` / `leftButtonUp` · `rightButton*` · `middleButton*` · `verticalScroll` ·
`captureGameRegion()` · `getAvatars()` · `inputText(text)` · `setGameMetrics(w,h)` / `getGameMetrics()`

### 3.6 可以直接 `new` 的参数类型

`RealtimeTimer` · `SoloTask` · `AutoSkipConfig` · `AutoDomainParam` · `AutoBossParam` · `AutoFightParam` ·
`AutoLeyLineOutcropParam` · `AutoStygianOnslaughtParam` · `CountInventoryItemParam` · `TargetItem` ·
`ResinUsage` / `ResinUsageMode` / `OriginalResinTier` / `ResinKind` · `GridScreenName` · `ItemIconRecognitionMode` ·
`CancellationToken` / `CancellationTokenSource` · `Task` · `Mat` / `Point2f` / `Pen` / `Color`

构造 `BvPage`、`CombatScenes`、`Avatar`、`KeyMouseHook`、`GameCaptureRegion` 这些需要运行环境的类型时，
宿主会用当前的游戏窗口自动补参数，写法与普通 `new` 一致。

---

## 4. 库与多文件

没有 CommonJS。用 ES Module：

```js
// lib/util.js
export function dist(a, b) { return Math.hypot(a.x - b.x, a.y - b.y); }
```

```js
// main.js
import { dist } from './lib/util.js';
```

前提是 `manifest.json` 里写了 `"library": ["lib"]`，否则搜索路径里没有 `lib`，`import` 会报找不到模块。
`"scripts"` 要把用到的文件都列上，仓库安装时才会一起打包。

---

## 5. 异步与超时

`captureGameRegion`、`http`、`file` 的多数方法返回 Promise，用 `await`：

```js
const r = await http.get('https://api.example.com/ping');
log.info(r);
```

`await sleep(1000)` 也可用 —— `sleep` 返回的是绑定本次运行取消令牌的 `Task`，脚本被手动停止时会立刻抛出来，不会让脚本卡在睡眠里不退出。

**不要用 `while(true)` 空转**：V8 是单线程，空转会卡死整个脚本引擎，停止按钮也点不动。要等待就 `await sleep(...)`。

---

## 6. 设置

### 基础表单 `settings.json`

```json
[
  { "name": "targetCount", "type": "input-int", "label": "要采集的数量", "default": 10 },
  { "name": "mode", "type": "combo", "label": "模式", "options": ["快速", "完整"], "default": "快速" },
  { "name": "enableDebug", "type": "checkbox", "label": "输出调试日志", "default": false }
]
```

运行时可读 `settings.targetCount`，类型与 `default` 一致。`type` 支持
`input-text` / `input-int` / `input-double` / `combo` / `checkbox` / `separator`，`combo` 还能用 `cascade_options` 做联动。

### HTML 设置页

`settings_ui` 表达不了的（分组、联动、列表、校验），用 `settings_html` 自己写页面，通过 `fetch('/__bgi/settings')` 读写。
完整说明见 [js-script-html-settings.md](../technical/js-script-html-settings.md)。两者可以共存：新版本用 HTML 页，旧版本回落到表单。

---

## 7. 网络与文件的白名单

这是新手最常撞的两堵墙：

```js
// 报错：当前JS脚本没有配置允许请求的URL
await http.get('https://api.example.com/x');
```

必须在 `manifest.json` 里加：

```json
"http_allowed_urls": ["https://api.example.com/"]
```

前缀匹配，结尾带 `/` 表示整个路径下都允许。`http://` 一律拒绝，只支持 HTTPS。

文件同理：`file` 的根是脚本目录，写到别处需要把相对路径加进 `saved_files`。
这是为了让「装个脚本」不至于变成「装个后门」—— 脚本是能按键、能截屏、能发请求的。

---

## 8. 一个能跑的完整例子

```js
// main.js —— 走到凯瑟琳面前，用 OCR 确认对话内容，不符就记日志
async function main() {
    log.info('开始，版本 ' + getVersion());

    // 1. 传送到蒙德城
    await genshin.teleport(2266, -1182);
    await sleep(3000);

    // 2. 用路线任务走过去（路线放在 User/AutoPathing/）
    await pathingScript.run('User/AutoPathing/日常/凯瑟琳.json');

    // 3. 按 F 交互，然后读屏幕上的文字
    keyPress('F');
    await sleep(1500);
    const text = ocr.readRegion(700, 400, 520, 200);
    log.info('对话内容：' + text);

    if (text.includes('冒险家协会')) {
        log.info('到了');
    } else {
        log.warn('好像走错地方了');
    }
}

await main();
```

---

## 9. 调试

| 手段 | 说明 |
| --- | --- |
| `log.info()` | 写进 `log/` 目录下的日志文件，界面「运行日志」里实时能看到 |
| `ocr.saveRegion(path, x, y, w, h)` | 把区域存成 PNG，用来校准坐标，比盲猜快得多 |
| 编辑器断点 | 不支持。V8 宿主没有接 CDP，只能靠日志和截图 |
| 手动停止 | 停止按钮会取消本次运行的令牌，`sleep`、`http`、`file` 异步方法都会抛出来 |

坐标一律按 **1080P 基准**写（`ocr`、`quest` 内部会按当前窗口换算），别按自己屏幕的分辨率写死。

---

## 10. 发布

脚本仓库机制与官方一致：一个 GitHub 仓库，根目录放 `manifest.json`，用户在「脚本仓库」里填地址即可安装和更新。
仓库结构、`repo.json` 版本信息、多语言 README 的写法见[官方说明](https://www.bettergi.com/feats/autos/srepo.html)。

特别版没有改这套机制 —— 你给官方写的脚本，在特别版里能直接跑；反过来说，用了 `ocr` / `quest` / `ai` 的脚本在老版本上会因为找不到这些全局对象而报错，所以：

> 用了特别版 API 的脚本，请在 `bgi_version` 里写清要求，并在 README 里说明「需要 BetterGI 特别版」。
