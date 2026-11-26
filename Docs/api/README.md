# API / SDK

BGI 对外提供三种接口：**脚本宿主 API**（给 JS 用）、**插件 SDK**（给 .NET 用）、**数据格式**（给人写文件用）。
本页是索引，具体签名与用法在各自的文档里。

## 1. 脚本宿主 API（JavaScript）

引擎是 **V8 / ClearScript**，不是 Node.js。没有 `require`，没有 npm，异步用 `Promise` + `await`。

### 官方部分

官方 API 全表维护在文档站，不在本仓库：

- <https://www.bettergi.com/feats/autos/jsscript.html>

核心宿主对象：`genshin`（游戏与输入）、`pathingScript`（跑路线）、`keyMouseScript`、`dispatcher`、`log`、
`file`（受限文件）、`http`（需白名单）、`captureGameRegion()`、`yolo`、`notification`，以及一批 OpenCV / 识别类型。

### 特别版扩展

| 宿主 | 能力 | 文档 |
| --- | --- | --- |
| `ocr` | 屏幕/区域转文字，1080P 基准坐标自动换算 | [SPECIAL-API.md](../SPECIAL-API.md) |
| `quest` | 读左上角任务追踪面板的标题与当前步骤 | 同上 |
| `OcrFactory` / `Bv` | 直接暴露 OCR 引擎与 BgiVision 静态判定 | 同上 |
| `ai` | 调内置大模型（密钥与预算留在宿主侧） | 同上 |

这四个在官方版里不存在。脚本用了它们，就要在 `manifest.json` 的 `bgi_version` 里写清要求，并在 README 里注明「需要特别版」。

### 脚本清单 `manifest.json`

字段表见 [developer/script-js.md 第 2 节](../development/script-js.md#2-manifestjson)。设置页两种写法：
`settings_ui`（基础表单）与 `settings_html`（自绘页面，见 [js-script-html-settings.md](../technical/js-script-html-settings.md)）。

## 2. 插件 SDK（.NET）

插件是独立程序集，用 `[PluggableImpl]` 把实现接到主程序的扩展点上。

| 类型 | 位置 | 说明 |
| --- | --- | --- |
| `IPluginContext` | `Core/Plugins/IPluginContext.cs` | `Manifest` / `PluginDirectory` / `DataDirectory` / `Logger` / `Settings`，可注入实现与设置 VM 的构造函数 |
| `IPluginSettingsStore` | `Core/Plugins/IPluginSettingsStore.cs` | `Load<T>()` / `Save<T>(T)`，落在 `User/PluginData/<id>/settings.json` |
| `PluginManifest` | `Core/Plugins/PluginManifest.cs` | `plugin.json` 的映射，snake_case |
| `[PluggableImpl]` | `Core/Plugins/PluggableImplAttribute.cs` | 声明某个可插拔能力的实现 |
| `[PluginSettingsView]` | `Core/Plugins/PluginSettingsViewAttribute.cs` | 声明插件的设置页 VM 与视图 |
| `[UiLanguage]` / `[GameLanguage]` | `Core/Plugins/` | 插件界面与游戏文本的语言包 |

### 扩展点

| 标识 | 接口 | 说明 |
| --- | --- | --- |
| `ocr` | `IOcrService` | 换 OCR 模型，用户在「设置 → OCR 配置」切换 |
| `teleport` | `ITpTaskFactory` | 换传送与大地图操作实现 |

只有这两个。插件**不能新增**扩展点（`[Pluggable]` 仅限主程序），也**不能**在后台起自己的服务（入口类 `IBgiPlugin` 尚未实现）。

开发流程、清单字段、打包与发布见 [development/plugin-dev.md](../development/plugin-dev.md)；
加载器、程序集解析、热切换等内部机制见 [design/plugin-system.md](../design/plugin-system.md)。

## 3. 数据格式

| 文件 | 用途 | 文档 |
| --- | --- | --- |
| 路线任务 `*.json` | 自动采集 / 挖矿 / 锄地 | [development/script-json.md](../development/script-json.md) |
| 脚本 `manifest.json` | JS 脚本项目 | [development/script-js.md](../development/script-js.md) |
| 插件 `plugin.json` | 插件清单 | [development/plugin-dev.md](../development/plugin-dev.md) |
| 插件市场 `index.json` | 市场索引 | [plugin-market/README.md](../../plugin-market/README.md) |

## 4. 内部服务：能用，但别指望稳定

插件通过服务容器可以拿到主程序的绝大多数单例（`AllConfig`、`ILogger`、`TaskRunner`、`GameCaptureService` ……），
**因为这层是给插件开的口子，没有兼容性承诺**：主程序内部重构时这些类型随时会变，插件会编译不过或运行期崩。
设计文档里对此的原话是「主程序的内部服务没有兼容性承诺」。

实践建议：

- 只注入**确实必要**的服务，别为了省事把整个容器当工具箱
- 优先走扩展点暴露的接口（`IOcrService` 等），那是有承诺的
- 需要某个内部能力但扩展点没暴露 → 先提需求加扩展点，别直接抓内部类型

## 5. 版本与本仓库

| | 值 |
| --- | --- |
| 当前版本 | 见 `BetterGenshinImpact/BetterGenshinImpact.csproj` 的 `<Version>` |
| 版本号形态 | `0.67.0-special.7.7.3`：内核版本 + 特别版预发布后缀 |
| 版本比较 | 比较前会剥掉 `-special` 后缀，避免 SemVer 把特别版判成「比同版本正式版更老」 |
| 插件最低 SDK | `0.66.3-alpha.1` |

因为后缀会被剥掉，**用官方 SDK 编译的插件（`sdk_version: 0.67.0`）在特别版上可以直接加载**，不需要为特别版单独编译。
