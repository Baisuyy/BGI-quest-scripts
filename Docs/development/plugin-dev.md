# 插件开发

插件是**独立编译的程序集**，装进 `Plugins/` 目录，由 BGI 加载后把里面的实现接到主程序的能力槽位上。
和 JS 脚本的区别：插件是原生 .NET 代码，能跑在游戏线程外、能读大模型、能用 OpenCV，性能不受脚本引擎限制。

> 完整设计（加载上下文、程序集解析、热切换、卸载清理）见
> [plugin-system.md](../design/plugin-system.md)，本文只写**做插件需要知道的部分**。

---

## 1. 现在能做什么

先说清楚边界，免得白写：

| 能力 | 标识 | 接口 | 说明 |
| --- | --- | --- | --- |
| OCR 模型 | `ocr` | `IOcrService` | 换掉识别游戏文字用的 OCR。用户在「设置 → OCR 配置」里切换 |
| 传送 | `teleport` | `ITpTaskFactory` | 换掉大地图传送与大地图操作的实现 |

**目前只有这两个扩展点。** 插件不能新增扩展点（`[Pluggable]` 只允许主程序使用），也不能在后台起自己的服务 ——
入口类 `IBgiPlugin` 在设计里留了位置但**尚未实现**，所以「插件启动时开个轮询」这类需求现在还做不到。
需要新的扩展点，得先给主程序加接口，属于改 BGI 本身。

---

## 2. 目录与清单

### 安装后的样子

```
Plugins/
└─ ppocr_v5/                     ← 目录名 = 插件标识
   ├─ plugin.json                ← 清单，必需
   ├─ MyPlugin.dll               ← 入口程序集
   ├─ MyPlugin.deps.json
   └─ models/                    ← 私有资源随便放
```

插件数据（设置、缓存）写到 `User/PluginData/<插件标识>/`，**卸载插件时保留**，重装还能接着用。

### `plugin.json`

```json
{
  "manifest_version": 1,
  "id": "ppocr_v5",
  "name": "PaddleOCR V5 中文",
  "version": "1.0.0",
  "authors": ["你的名字"],
  "description": "PP-OCRv5 检测 + 中文识别，也能识别英文和日文。",
  "entry": "PpOcrV5Plugin.dll",
  "sdk_version": "0.67.0",
  "max_host_version": "",
  "package_id": "PpOcrV5Plugin",
  "homepage": "https://github.com/you/ppocr_v5"
}
```

| 字段 | 必填 | 规则 |
| --- | --- | --- |
| `manifest_version` | 是 | 固定 `1` |
| `id` | 是 | 小写字母开头，只含小写字母、数字、下划线，≤ 32 字符。**与目录名一致** |
| `name` | 是 | 显示名 |
| `version` | 是 | SemVer |
| `entry` | 是 | 入口程序集，目录内相对路径，**不能含 `..`**，必须以 `.dll` 结尾 |
| `sdk_version` | 是 | 编译时用的 BGI 版本，同时是要求的最低版本 |
| `max_host_version` | 否 | 最高支持版本，空串表示不限 |
| `authors` / `description` / `homepage` / `package_id` | 否 | 展示与更新用 |

`id` 只能用**小写字母、数字、下划线**，不能有连字符：正则 `^[a-z][a-z0-9_]{0,31}$`。
写 `ppocr-v5` 会因为清单校验失败而装不上，改成 `ppocr_v5`。安装时目录名就是 `id`，所以目录也不会出现连字符。
`builtin`、`bgi`、`onnxruntime` 是保留标识，不能用。

---

## 3. 写一个最小插件

以 OCR 为例。工程引用主程序程序集（**不要**把 `BetterGenshinImpact.dll` 拷进插件目录，加载时由宿主提供）：

```csharp
using BetterGenshinImpact.Core.Plugins;
using BetterGenshinImpact.Core.Recognition.OCR;
using OpenCvSharp;

namespace PpOcrV5Plugin;

/// <summary>用 PP-OCRv5 替换内置 OCR。</summary>
[PluggableImpl("paddle_v5", "PaddleOCR V5")]
public sealed class PpOcrV5Service : IOcrService, IDisposable
{
    // 构造函数可以注入插件上下文、主程序服务；实例在切换实现后被释放
    public PpOcrV5Service(IPluginContext context)
    {
        var modelPath = Path.Combine(context.PluginDirectory, "models");
        context.Logger.LogInformation("加载模型：{Path}", modelPath);
        // 加载模型……
    }

    public string Ocr(Mat mat) => OcrResult(mat).Text;
    public string OcrWithoutDetector(Mat mat) => OcrResultWithoutDetector(mat).Text;
    public OcrRecognizerResult OcrResultWithoutDetector(Mat mat) { /* ... */ }
    public OcrRecognizerResult[] OcrResultLinesWithoutDetector(Mat[] lines) { /* ... */ }
    public OcrResult OcrResult(Mat mat) { /* ... */ }

    public void Dispose() { /* 释放推理会话 */ }
}
```

要点：

- `[PluggableImpl("名称", "显示名")]` 的名称是**小写 snake_case**，不带前缀；完整标识是 `<插件id>.<名称>`，如 `ppocr_v5.paddle_v5`
- 一个类只能实现**一个**可插拔能力接口，且必须是非抽象类
- 实现必须**线程安全**：实时触发器和任务会同时调用
- 构造函数可以 `new` 模型、读文件；耗时操作放构造函数里没问题，它在启用时执行，不占界面线程

`[PluggableImpl]`、`IPluginContext` 由开发包提供隐式 using，不必手写 `using BetterGenshinImpact.Core.Plugins;`。

---

## 4. 两个扩展点的约定

### `IOcrService`

```csharp
string Ocr(Mat mat);                                  // 检测 + 识别，返回整段文字
string OcrWithoutDetector(Mat mat);                   // 只识别，输入应是裁好的单行
OcrRecognizerResult OcrResultWithoutDetector(Mat mat);
OcrRecognizerResult[] OcrResultLinesWithoutDetector(Mat[] lines);  // 批量，一次推理
OcrResult OcrResult(Mat mat);
```

**调用方保留 `Mat` 的所有权**，实现不要去 `Dispose` 传进来的图。
`OcrResultLinesWithoutDetector` 是性能关键路径（任务面板、对话选项一次几十行），尽量真的做批处理。

### `ITpTaskFactory`

```csharp
ITpTask Create(GameRuntime runtime, AllConfig config, CancellationToken ct);
```

返回的实例**只依赖传入参数**，不要持有工厂字段 —— 插件停用后工厂会被换回默认实现，但已经创建的实例还要能跑到任务结束。

---

## 5. 设置界面

插件可以带一个自己的设置页，插件页上会显示「设置」按钮：

```csharp
using BetterGenshinImpact.Core.Plugins;

[PluginSettingsView(typeof(PpOcrV5SettingsView))]
public partial class PpOcrV5SettingsViewModel : ObservableObject
{
    private readonly IPluginContext _context;

    public PpOcrV5SettingsViewModel(IPluginContext context)
    {
        _context = context;
        var s = context.Settings.Load<PpOcrV5Settings>();   // User/PluginData/<id>/settings.json
        Threshold = s.Threshold;
    }

    [ObservableProperty] private double _threshold = 0.6;

    [RelayCommand]
    private void Save() => _context.Settings.Save(new PpOcrV5Settings { Threshold = Threshold });
}
```

规则：

- 声明**最多一个** `[PluginSettingsView]`，多了直接加载失败
- 视图必须继承 `FrameworkElement` 且有无参构造函数，`DataContext` 会被设成这个 VM
- 视图只能用**自己合并的资源字典**和应用级资源（WPF-UI 主题），不能用插件页的资源；局部样式用 `BasedOn` 继承 WPF-UI 默认样式
- VM 实现 `IDisposable` 的话，窗口关闭时会被释放
- `Settings.Load<T>()` 每次调用都读文件，频繁用请自己缓存；`Save<T>()` 先写临时文件再替换，不会读到半个文件

---

## 6. 多语言

两个特性，都是可选的：

```csharp
[assembly: UiLanguage("zh-CN")]   // 插件界面语言，读语言包文件
[assembly: GameLanguage("zh-CN")] // 游戏内文本（如怪物名），读文本表
```

语言包放在插件目录下，声明不合法会让**整个插件加载失败**。具体文件格式与读取顺序见
[plugin-system.md](../design/plugin-system.md) 第 12 节。不做多语言就不用管。

---

## 7. 打包

两种格式，装的时候走同一条路径：

| 格式 | 要求 | 用途 |
| --- | --- | --- |
| `.zip` | 根目录或唯一顶层目录下有 `plugin.json` | 手动分发、发布附件、**插件市场** |
| `.nupkg` | 包内 `plugin/` 目录下有 `plugin.json` | 发布到 NuGet 源 |

zip 的内容就是第 2 节那个目录：

```bash
# 在插件输出目录里打包，保证 plugin.json 在 zip 根
cd artifacts/plugins/ppocr_v5
zip -r ../ppocr_v5-1.0.0.zip .
sha256sum ../ppocr_v5-1.0.0.zip
```

安装时 BGI 会先把包装到临时目录、读完清单校验一遍再落到 `Plugins/`，不合法不会留下半个插件。

---

## 8. 发布到插件市场

市场读的是这份索引：

```
https://raw.githubusercontent.com/Baisuyy/BGI-quest-scripts/bgi-special/plugin-market/index.json
```

用户在「插件」页的「市场设置」里可以改成自建地址，所以你也可以自己托管一份。

条目格式（完整字段表见 [plugin-market/README.md](../../plugin-market/README.md)）：

```json
{
  "id": "ppocr_v5",
  "name": "PaddleOCR V5 中文",
  "version": "1.0.0",
  "description": "PP-OCRv5 检测与中文识别模型。",
  "authors": ["你的名字"],
  "homepage": "https://github.com/you/ppocr_v5",
  "categories": ["OCR 模型"],
  "tags": ["ocr", "识别", "文字"],
  "download_url": "https://github.com/you/ppocr_v5/releases/download/v1.0.0/ppocr_v5-1.0.0.zip",
  "sha256": "完整的 64 位小写十六进制",
  "size": 5242880,
  "min_host_version": "0.67.0",
  "updated_at": "2026-11-27"
}
```

硬规则：

- **`sha256` 不能省。** 插件以管理员权限运行，没有校验值的条目会被整条丢弃，界面上根本不显示
- `download_url` 必须 `https://` 且能**直链下载**（GitHub Release 资产最省事，`raw.githubusercontent.com` 的小文件也行）
- `id` 与 `plugin.json` 里的 `id` 一致，否则已安装状态对不上，会一直提示「安装」
- `version` 比已装的高才显示「更新」；低了显示「降级安装」

收录流程：打包 → 传 Release → `sha256sum` → 在本仓库 `plugin-market/index.json` 里加一条 → 提交。
没有审核环节，代价是**加条目前必须自己验过包能装、能跑**。

---

## 9. 兼容性与版本

加载时逐项检查，任何一项不过就不启用，原因显示在插件卡片上：

| 检查 | 失败提示 |
| --- | --- |
| `manifest_version` 是否为 1 | 不支持的清单版本 |
| `id` 格式、与目录名是否一致 | 标识不合法 / 与目录不一致 |
| `version`、`sdk_version` 是否为合法 SemVer | 不是有效的版本号 |
| `sdk_version` ≥ `0.66.3-alpha.1` | 插件基于旧版本编译，请更新插件 |
| `sdk_version` ≤ 当前 BGI 版本 | 需要 BetterGI x.y.z 或更高版本 |
| `max_host_version` 非空时必须 ≥ 当前版本 | 只支持到 x.y.z |

**特别版的版本号带 `-special` 后缀**（如 `0.67.0-special.7.7.3`），SemVer 里它低于 `0.67.0`。
比较时会先剥掉后缀再比内核版本，所以 `sdk_version` 照官方写 `0.67.0` 就能在特别版上用，不需要为特别版单独编译。
`max_host_version` 同理。

---

## 10. 安全提醒

插件是**以 BGI 的权限运行的原生代码**，能读写文件、发网络请求、注入进程。BGI 能做的校验只有：

1. 安装前校验包的 SHA-256（走市场时）
2. 安装时检查清单与路径合法（不能跳出插件目录）
3. 装之前弹窗让你确认

**没有沙箱，也没有权限系统。** 装第三方插件等于信任作者。自己写插件时也请注意：不要在构造函数里做网络请求，用户点「启用」的那一刻不该产生副作用。
