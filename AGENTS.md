# 项目约定

- 使用 Windows 环境，使用简体中文沟通。
- C# 文件必须使用 UTF-8 无 BOM 编码，避免乱码。
- 保留已有注释，可修改或补充，不要随意删除。
- 在无冲突情况下请不要对类使用全路径

## 设计原则

- 设计时先明确各层职责、边界和依赖方向，保持分层清晰，避免跨层耦合。
- 核心代码保持简洁易懂，直接表达业务意图，避免不必要的抽象和复杂度。
- 首次实现聚焦主流程和已明确的需求，不为细微边界问题过度设计；暂不处理的场景可用注释记录。
- UI 层可为交互体验承载必要的复杂度，同时遵守 MVVM 和分层边界；整体 UI/UX 应直观易用，符合用户操作直觉。

## 代码规范

- 新增类、属性、字段和方法必须有注释；核心逻辑、执行步骤及逻辑分块也需说明。
- 重要 C# 文件原则上只放一个类；枚举或用途单一的小类可合并存放。
- 可为 null 的类型使用 `?` 标注。
- 私有字段使用 `_camelCase`，公共属性使用 `PascalCase`。
- 禁止新增 `BetterGenshinImpact/Assets` 目录。资源由 `BetterGI.Assets.*` 外部包提供，新增资源需人工修改对应 NuGet 包。

## 框架与 MVVM

主要依赖：WPF-UI 4.0.2、gong-wpf-dragdrop 3.2.1、CommunityToolkit.Mvvm 8.2.2、Microsoft.Xaml.Behaviors.Wpf 1.1.122。

- ViewModel 必须直接或间接继承 `ObservableObject`；项目的 `ViewModel` 基类已满足此要求。
- 使用 `[ObservableProperty]` 生成属性，使用 `[RelayCommand]` 生成命令；异步命令返回 `Task`。
- View 通过构造函数注入 ViewModel，沿用所在页面的 `DataContext` 约定；XAML 绑定路径须与其一致，避免复杂逻辑。
- XAML 绑定只读属性、计算属性或纯展示文本时，必须显式指定 `Mode=OneWay`，不要依赖目标依赖属性的默认绑定模式。尤其 `Run.Text` 默认双向绑定，绑定只读属性会在加载 XAML 时抛出异常。示例：`<Run Text="{Binding TriggerDirectory, Mode=OneWay}" />`。
- 只有需要将用户输入回写到可写属性的绑定才使用 `Mode=TwoWay`；新增或修改含 `Run.Text` 的视图时，必须检查所有展示用绑定的方向。
- 子视图构造时尚未加入父控件资源树，`StaticResource` 必须能在本视图或明确合并的字典中解析；禁止依赖稍后才挂接的父视图资源。新增类别模板须验证实际实例化及布局，不能只以编译通过作为 XAML 可用的依据。
- 控件局部样式应通过 `BasedOn` 继承对应 WPF-UI 默认样式；承载类别视图的 `ContentControl` 须明确内容拉伸，避免覆盖主题状态或出现内容挤缩。
- 交互优先使用 Behaviors，避免在代码后置中编写不符合 MVVM 的交互逻辑。
- 在 `App.xaml.cs` 中注册依赖：页面使用 `services.AddView<ExamplePage, ExampleViewModel>()`，单例服务使用 `services.AddSingleton<IExampleService, ExampleService>()`。
- 新增窗口必须启用主题背景；简单对话框优先使用 `ThemedMessageBox`，不要使用 WPF 自带的 `MessageBox`。
- JSON 序列化优先使用 Newtonsoft.Json；模型若已由 System.Text.Json 序列化，则使用 System.Text.Json 反序列化。

## 识别与模拟操作

- 键鼠操作后再识图，必须适当延时：游戏响应通常有 30～60 ms 延迟，截图来源也有延迟。
- 键鼠操作之间也需合理间隔，避免在 30 fps 游戏中因操作过快而丢失输入。

## 任务模式与实时触发器

- 任务（`TaskRunner` / `SoloTask`）一开始就进入任务模式：调度器只运行任务用 `AddTrigger` 租用的触发器，用户配置里开的自动拾取、自动剧情、自动吃药在任务期间一律停用。
- 所以凡是依赖实时触发器的任务，必须在自己的 `RunAsync` 里显式租用，并用 `using` 覆盖执行期间。踩过的坑：`auto_pathing`（机器人下发的采集）包成 SoloTask 时漏了 `AddTrigger("AutoPick")`，整条路线走完什么都没捡。
- 同一条路线手动跑正常、远程跑不拾取，就是这个原因——手动的入口是 `ScriptGroupProject`，那里已经租用了 `AutoPick`。排查时看日志：任务期间若只有「实时触发器 自动拾取 停用」而没有对应的启用，就是没租用。

## 编译验证

禁止直接运行主程序进行测试或截图（包括启动 exe、`dotnet run` 或通过 IDE 启动）。主程序要求管理员权限，harness 无法通过启动 exe 完成模拟测试和截图感知，因此不要将启动主程序作为验证步骤。

在仓库根目录执行以下命令，仅输出错误；退出码 0 表示成功，非 0 表示失败。编译通过即可，无需运行程序；若程序占用导致失败，停止验证并说明即可，无需继续尝试。

```powershell
dotnet msbuild BetterGenshinImpact.sln -restore -p:Configuration=Debug -nologo -tl:off -clp:ErrorsOnly
```

## 代码审查

仅在执行代码审查时，读取并遵循 [.agents/code-review.md](.agents/code-review.md)。

## 特别版发布约定

以下几条是维护者定下的固定做法，改流水线或写发布脚本时按这里的约定来，不要临时另起一套。

- **安装包用 Kachina**：安装器由 `YuehaiTeam/kachina-installer` 的 `kachina-builder.exe` 生成，配置在 `Build/kachina.special.config.json`，产物名 `BetterGI.Install.<版本>.exe`。旧的 `7z.sfx` 自解压包不再使用。
- **123 云盘默认落在 `BGI.special`**：上传目标目录固定为云盘根下的 `BGI.special`，按版本再分一层（`/BGI.special/<版本>`）。脚本的 `--parent-name` / `PAN123_WEBDAV_ROOT` 默认值都是它，不要再传根目录。
- **版本号**：`0.67.0-special.7.3` 这种形式。前缀 `<上游版本>` 跟随上游，**只有 `special.N` 这个尾号属于特别版自己的版本线**，更新检查也按尾号比较。
  - 用户说"版本号 +0.1"= 尾号 +0.1（`special.7.2` → `special.7.3`），**前缀不动**。
  - 上游升版时才改前缀（例如上游从 0.67.0 到 0.68.0），此时尾号继续在原序列上递增，不回退。
  - tag 与产物名一律用完整版本串：`special-v0.67.0-special.7.3`。
- **产物三件套**：`BetterGI.Install.<版本>.exe`（Kachina 安装器）、`BetterGI_v<版本>_special-win-x64.7z`（免安装压缩包）、`BetterGI_v<版本>_patch-from-<上一版>.zip`（增量包，只要存在上一个 special 发布就必须生成）。
- **更新源只能指向本仓库**：`Build/kachina.config.json` 是官方那份，`source` 全指向 `babalae` / `cocogoat` / Mirror 酱。打包特别版必须用 `Build/kachina.special.config.json`，`gen` 的 `-r` 也必须是 `Baisuyy/BGI-quest-scripts`。拿错配置的后果不是报错，而是特别版用户点「检查更新」被静默拉回官方版（版本号更低、功能更少），卸载信息也指向官方。流水线里已经加了断言，别为了绕开它去改 config。
- **7.2 及更早用的是 MicaSetup**（`BetterGI_v<版本>_Setup.exe`），那个安装器只有覆盖安装、没有任何差分更新能力；7.3 起换成 Kachina 才带 `BetterGI.update.exe`。用户反馈「覆盖安装没有热更新」，先确认他装的是哪个安装器，再谈是不是 bug。
- **发版只由人决定**：推 `special-v<完整版本>` 标签触发构建 + 发 Release。每日上游同步只跑 `make_release=false` 的构建验证，不自动发版——否则版本尾号会变成 `GITHUB_RUN_NUMBER`（`special.100` 这种），比人发的版本还大，发布页出现两条版本线，客户端天天弹更新。
- **`{{BASE}}` 取主版本**：`<Version>` 写的是完整串（`0.67.0-special.7.5`），发版说明里的「基于上游 X 构建」由流水线用正则截出 `0.67.0`，别再让 `{{BASE}}` 直接吃整串。
- **热更新只做相邻版本**：增量包名 `BetterGI_v<新>_patch-from-<旧>.zip`，流水线只拿「上一个 special 发布」当基准。客户端按 `_patch-from-` 后的版本号与当前版本精确匹配，匹配不上就明说「这一版没有可套用的增量包（只做相邻版本）」并给完整包链接，不要含糊成「没有在线更新」——那句话在 7.5 之后已经不成立。
- **`User` 目录是硬边界**：增量包不打包 `User/**`（构建端 `-like 'User\*'` 跳过），客户端 `ParseManifest` 再剔除一次，覆盖脚本动手前还挡一次。三层都留着——漏一层就是把用户改过的配置换回默认值，而用户根本看不出发生过什么。
- **升级路径的默认答案是热更新**：发版说明、弹窗、帮助文案一律先讲增量包（几 MB、退出即换、`User` 不动），完整包作为「跨版本 / 增量包缺失」的退路。别再写「需要下载安装包覆盖安装」了事。

## 地图追踪断点续跑

- 进度落在 `User/PathProgress.json`，键是相对 `User` 的路径，值是「第几段、段内第几点」。文件长度或修改时间变了，旧进度自动作废。
- 粒度是**段级优先**：段是按传送点切出来的，段首一般就是传送点，重跑一段几十秒，位置确定。只有确认人物还站在断点 30m 内，才做点级续跑——否则会跳过一次没执行的传送，把人留在提瓦特另一头。
- 记录点（`PathProgressStore.Note`）放在**每个路径点执行之前**，这样被强杀时最后落盘的位置就是要接着走的位置。
- 开关是 `CommonConfig.PathingResumeEnabled`，默认开。关掉用于「这条路线重新录过、要完整验证一遍」。
- 脚本执行路线时务必把文件路径传进 `AutoPathingScript.Run(json, logicalPath)`，否则进度只能按路线名记，同名或同点数的路线会串。

## 日志与诊断

- 落盘级别由 `CommonConfig.LogFileLevel` 控制（`Debug` / `Information` / `Warning` / `Error`），只影响文件，不影响界面日志框。改完要重启才生效，因为 Serilog 的级别在启动时定死。
- 日志里出现 `[ERR]` 会触发一次诊断包（`log/diagnostics/BGI-diag-*.zip`，含日志尾部、环境、脱敏配置摘要），10 分钟内不重复，保留最近 5 个。只在本地生成，不自动上传。
- **不要用 `LogError` 表达「正常的分支」**。踩过的坑：`LimitedFile.ReadPathSync` 在目录不存在时打 ERR，而首次运行的脚本那个 `records/` 目录本来就不存在，日志里于是全是假警报，真问题时反而被淹没。
