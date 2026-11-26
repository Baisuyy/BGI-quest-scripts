# 开发者文档

面向三件事：**给 BGI 写自动化流程**、**给 BGI 加能力**、**给 BGI 提代码**。

## 选哪条路

| 你想做的事 | 用哪种 | 门槛 | 入口 |
| --- | --- | --- | --- |
| 固定点位跑图：采集、挖矿、锄地 | 路线任务（JSON） | 会用录制工具就行 | [script-json.md](script-json.md) |
| 有判断逻辑的流程：读任务面板、按条件分支、OCR 校验 | JS 脚本 | 会写 JavaScript | [script-js.md](script-js.md) |
| 换 OCR 模型、换传送实现 | 插件 | 会写 C#、能编 .NET | [plugin-dev.md](plugin-dev.md) |
| 改 BGI 本身 | 源码贡献 | C# + WPF | 本文最后 |

大致判断：**能用录制解决的别写脚本，能用脚本解决的别写插件。** 插件是原生代码，装的人要重启软件，还要信任你。

## 官方能力 vs 特别版能力

| | 官方版 | 特别版 |
| --- | --- | --- |
| 路线任务 JSON | 完整支持 | 完全一致，可直接复用官方路线 |
| JS 脚本 | 完整支持 | 完全一致，官方脚本直接能跑 |
| 插件机制 | 完整支持 | 完全一致，`sdk_version` 照官方写 |
| JS 扩展 `ocr` / `quest` / `ai` | 无 | **只有特别版有**，见 [SPECIAL-API.md](../SPECIAL-API.md) |
| 插件市场 | 无 | 只有特别版有，索引格式见 [plugin-market/README.md](../../plugin-market/README.md) |

写脚本时如果用了 `ocr`、`quest`、`ai`，请把 `bgi_version` 写清并在 README 里注明「需要特别版」，否则老版本用户会遇到 `ocr is not defined`。

## 常用链接

- 官方文档站：<https://www.bettergi.com/>
- JS 脚本 API 全表：<https://www.bettergi.com/feats/autos/jsscript.html>
- 脚本仓库：<https://www.bettergi.com/feats/autos/srepo.html>
- 地图追踪（路线）：<https://www.bettergi.com/feats/autos/pathing.html>

## 设计文档（想改动内部实现再看）

| 主题 | 文档 |
| --- | --- |
| 插件系统：加载、服务容器、可插拔能力、包格式 | [plugin-system.md](../design/plugin-system.md) |
| 脚本 HTML 设置页 | [js-script-html-settings.md](../technical/js-script-html-settings.md) |
| 日志写入策略 | [logging.md](../design/logging.md) |
| 全局搜索 | [global-search.md](../design/global-search.md) |

## 改源码

```bash
# Linux / macOS 只能验证能否编译（WPF 跑不起来）
dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug -p:EnableWindowsTargeting=true

# Windows 本地发布
dotnet publish BetterGenshinImpact/BetterGenshinImpact.csproj -c Release -p:PublishProfile=FolderProfile
```

特别版跟随上游的机制（`sync_source.py` 的 KEEP 白名单、`patch_special.py` 的锚点补丁）见仓库
`tools/bgi-special/` 与 [SPECIAL-API.md](../SPECIAL-API.md) 末尾的「跟随上游」一节。

> 改上游文件（`App.xaml.cs`、`CommonSettingsPage.xaml` 这类）之前先想一下：这个改动下次同步会不会被覆盖？
> 会被覆盖的，要么放进独立文件，要么写进 `patch_special.py`，否则就是 2026-11-26 那次 QQ 机器人「静默消失」的重演。
