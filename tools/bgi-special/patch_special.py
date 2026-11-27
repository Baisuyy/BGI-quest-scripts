#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把特别版的改动重新注入上游源码，可重复执行。

上游同步（sync_source.py）会整体覆盖源码，本脚本负责把「改到上游文件里」的几处补丁贴回去：

1. EngineExtend.InitHost         —— ocr / quest / OcrFactory / Bv / ai 宿主
2. （已移除）AllConfig —— 特别版配置节点改由 AllConfig.Special.cs 承载
3. App.xaml.cs                   —— 注册 AiKernel 单例
4. PathExecutor.MoveTo           —— 第三次卡死放弃前让内置 AI 接管
5. TaskSettingsPage.xaml         —— 插入内置 AI 设置卡片
6. TaskSettingsPageViewModel     —— 连通性测试命令
7. UpdateService.CheckUpdateAsync —— 特别版更新检查改道，不接官方更新通道
8. App.xaml.cs                   —— 注册「AI 助手」页（模型配置 + 本地助手）
9. MainWindow.xaml               —— 侧边栏加「AI 助手」入口
10. ApplicationHostService       —— 启动时注册助手小面板的全局快捷键
11. BetterGenshinImpact.csproj   —— 补回特别版文件依赖、上游没有的 NuGet 包
12. DownloadCategory.cs          —— 补回特别版增量包用的下载分类 SpecialUpdate
13. 全局搜索                     —— 顶栏搜索框、代码后置、DI 注册（新增文件走 KEEP）

每一处都先判断是否已存在（幂等），锚点找不到就返回非 0，由 workflow 报警并开 issue。
新增文件（Core/Ai 等）不在这里处理，由 sync_source.py 的 KEEP 清单保留。
"""

import pathlib
import re
import sys

ROOT = pathlib.Path.cwd()

ENGINE_EXTEND = "BetterGenshinImpact/Core/Script/EngineExtend.cs"
APP = "BetterGenshinImpact/App.xaml.cs"
PATH_EXECUTOR = "BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.cs"
SETTINGS_PAGE = "BetterGenshinImpact/View/Pages/TaskSettingsPage.xaml"
SETTINGS_VM = "BetterGenshinImpact/ViewModel/Pages/TaskSettingsPageViewModel.cs"
UPDATE_SERVICE = "BetterGenshinImpact/Service/UpdateService.cs"
MAIN_WINDOW = "BetterGenshinImpact/View/MainWindow.xaml"
APP_HOST = "BetterGenshinImpact/Service/ApplicationHostService.cs"
CSPROJ = "BetterGenshinImpact/BetterGenshinImpact.csproj"
DOWNLOAD_CATEGORY = "BetterGenshinImpact/Infrastructure/Network/Download/DownloadCategory.cs"

# ---------------------------------------------------------------- 11. NuGet 包
#
# csproj 跟随上游整体覆盖，特别版新增文件依赖、上游没有的包会一起被冲掉，
# 表现为编译期 CS0246（例如 MysQrCode 找不到 QRCoder）。这里按名字补齐，
# 已存在就跳过；版本只在第一次引入时锁定，之后要升级直接改这里。
EXTRA_PACKAGES = (
    ("Microsoft.Extensions.Localization", "10.0.12"),
    ("QRCoder", "1.6.0"),
    ("WPF-UI.Tray", "4.3.0"),
)


def ensure_packages() -> None:
    path = ROOT / CSPROJ
    if not path.is_file():
        print(f"[x] 找不到 {CSPROJ}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")
    missing = [(name, version) for name, version in EXTRA_PACKAGES
               if f'Include="{name}"' not in text]
    if not missing:
        print("[=] NuGet 包：齐全，跳过")
        return

    # 插在最后一条 PackageReference 之后，缩进跟随该行
    last = None
    for last in re.finditer(r"[ \t]*<PackageReference [^>]*/>[ \t]*\r?\n", text):
        pass
    if last is None:
        print(f"[x] {CSPROJ} 里找不到 PackageReference，上游结构可能已改动", file=sys.stderr)
        raise SystemExit(3)

    indent = re.match(r"[ \t]*", last.group(0)).group(0)
    block = "".join(f'{indent}<PackageReference Include="{name}" Version="{version}" />\n'
                    for name, version in missing)
    path.write_text(text[: last.end()] + block + text[last.end():], encoding="utf-8")
    print(f"[+] 补回 NuGet 包：{', '.join(name for name, _ in missing)}")


# ---------------------------------------------------------------- 12. 下载分类
#
# 上游把 DownloadCategories 从枚举改成了 record 列表，特别版增量包用的
# SpecialUpdate 不在里面，SpecialUpdater 会以 CS0117 编译失败。
CATEGORY_BLOCK = '''    /// <summary>
    /// 特别版增量包，安装完成后自动删除，留下的通常是未完成或校验失败的下载
    /// </summary>
    public static readonly DownloadCategory SpecialUpdate = new("special-update", "特别版增量包",
        "特别版更新的增量包，安装完成后自动删除");

'''


def patch_download_category() -> None:
    path = ROOT / DOWNLOAD_CATEGORY
    if not path.is_file():
        print(f"[x] 找不到 {DOWNLOAD_CATEGORY}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")
    if "SpecialUpdate" in text:
        print("[=] 特别版下载分类：已存在，跳过")
        return

    all_match = re.search(
        r"(public static IReadOnlyList<DownloadCategory> All \{ get; \} = \[)([^\]]*)(\];)", text)
    if all_match is None:
        print(f"[x] {DOWNLOAD_CATEGORY} 里找不到 All 列表，上游结构可能已改动", file=sys.stderr)
        raise SystemExit(3)

    items = all_match.group(2).strip()
    if not items.endswith(","):
        items += ","

    comment = text.rfind("    /// <summary>", 0, all_match.start())
    if comment == -1:
        print(f"[x] {DOWNLOAD_CATEGORY} 里找不到 All 的注释块", file=sys.stderr)
        raise SystemExit(3)

    text = (text[:comment]
            + CATEGORY_BLOCK
            + text[comment:all_match.start()]
            + all_match.group(1) + f"{items} SpecialUpdate" + all_match.group(3)
            + text[all_match.end():])
    path.write_text(text, encoding="utf-8")
    print("[+] 特别版下载分类 SpecialUpdate")

# ---------------------------------------------------------------- 1. 脚本宿主

HOST_ANCHOR = 'engine.AddHostObject("genshin", new Dependence.Genshin(runtime, allConfig, ct, screenshots));'

HOST_BLOCK = '''
        // ===== 特别版扩展：识图 / OCR / 任务追踪 / 内置 AI =====
        // OCR：屏幕或指定区域转文字，供脚本做条件判断
        var ocrHost = new OcrHost(runtime);
        engine.AddHostObject("ocr", ocrHost);
        // 任务追踪：读取左上角任务面板的标题与当前步骤
        engine.AddHostObject("quest", new QuestHost(ocrHost));
        // 直接暴露 OCR 引擎与 BgiVision 静态方法，给高级脚本自行组合
        engine.AddHostType("OcrFactory", typeof(BetterGenshinImpact.Core.Recognition.OCR.OcrFactory));
        engine.AddHostType("Bv", typeof(BetterGenshinImpact.GameTask.Common.BgiVision.Bv));
        // 内置大模型：脚本拿到的是内核的一层薄壳，密钥与预算都留在 C# 侧
        var aiKernel = App.GetService<AiKernel>() ?? new AiKernel(allConfig, App.GetLogger<AiKernel>());
        engine.AddHostObject("ai", new AiAgentHost(aiKernel));
'''

# ---------------------------------------------------------------- 2. 配置节点
#
# AllConfig / CommonConfig 的特别版成员不在这里注入。
# 它们是 partial 类，特别版字段放在 AllConfig.Special.cs / CommonConfig.Special.cs 里，
# 由 sync_source.py 按「*.Special.cs 一律保留」的规则跳过覆盖。
# 这样做的好处：上游改这两个文件的结构时，同步不会因为锚点漂移而失败，
# 也不会出现「注入漏了一半、编译过了但功能没了」这种更难发现的情况。

# ---------------------------------------------------------------- 3. DI 注册

APP_ANCHOR = "                services.AddSingleton(all);"

APP_BLOCK = """

                // 内置大模型内核：跑图救援与脚本 ai.Chat 共用同一个出口，共用密钥与预算
                services.AddSingleton<AiKernel>();"""

# ---------------------------------------------------------------- 4. 卡死接管

STUCK_ANCHOR = """                            _inTrap++;
                            if (_inTrap > 2)"""

STUCK_BLOCK = """                            _inTrap++;

                            // 到第 N 次卡死（默认 3，可配 1 便于验证）时，先给内置 AI 一次接管机会
                            if (_inTrap == AiStuckThreshold && await TryAiRescueAsync(screen, waypoint, position))
                            {
                                _inTrap = 0;
                                prevPositions.Clear();
                                lastPositionRecord = DateTime.UtcNow;
                                InputHub.Foreground.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                                continue;
                            }

                            if (_inTrap >= AiStuckThreshold && _inTrap > 2)"""

# ---------------------------------------------------------------- 5. 设置卡片

PAGE_MARK = "<!-- 自动分解圣遗物 -->"

# ---------------------------------------------------------------- 6. 测试命令

VM_ANCHOR = """    [RelayCommand]
    private void OnStrategyDropDownOpened(string type)"""

VM_BLOCK = '''    /// <summary>内置 AI 连通性测试的展示文本。</summary>
    [ObservableProperty]
    private string _aiTestResult = "";

    /// <summary>
    /// 用当前配置发一条最小请求，验证地址、密钥、模型名是否可用。不占本次运行额度，也不参与熔断。
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task TestAiConnection()
    {
        var kernel = App.GetService<AiKernel>();
        if (kernel == null)
        {
            AiTestResult = "内核未初始化";
            return;
        }

        AiTestResult = "正在测试…";
        try
        {
            var reply = await kernel.TestConnectionAsync(CancellationToken.None);
            AiTestResult = "连接正常：" + reply;
        }
        catch (Exception e)
        {
            AiTestResult = "测试失败：" + e.Message;
        }
    }

'''


# ---------------------------------------------------------------- 7. 更新检查改道

UPDATE_ANCHOR = "            string newVersion = await GetLatestVersionAsync(option);"

UPDATE_HOOK = '''            // ---- 特别版：官方三条更新通道都会把客户端覆盖回官方版，改走自己的 Release ----
            if (await SpecialUpdater.TryHandleAsync(option, _httpClientFactory, _logger))
            {
                return;
            }
            // ---- 特别版 ----

'''


# ---------------------------------------------------------------- 8. AI 助手页注册

PAGE_REG_ANCHOR = "                services.AddView<CommonSettingsPage, CommonSettingsPageViewModel>();"

PAGE_REG_BLOCK = """
                services.AddView<AiSettingsPage, AiSettingsPageViewModel>();"""

# ---------------------------------------------------------------- 9. 侧边栏入口

NAV_ANCHOR = '''                <ui:NavigationViewItem Content="{i18n:T 独立任务}"
                                       NavigationCacheMode="Enabled"
                                       TargetPageType="{x:Type pages:TaskSettingsPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:SymbolIcon Symbol="TaskListSquareLtr24" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
'''

NAV_BLOCK = NAV_ANCHOR + '''
                <!-- 特别版：模型配置与本地助手入口，避免埋在「独立任务」长页里找不到 -->
                <ui:NavigationViewItem Content="AI 助手"
                                       NavigationCacheMode="Enabled"
                                       TargetPageType="{x:Type pages:AiSettingsPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:FontIcon Glyph="&#xf544;" Style="{StaticResource FaFontIconStyle}" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
'''


# ---------------------------------------------------------------- 10. 助手小面板快捷键

APP_HOST_ANCHOR = "        await HandleActivationAsync();"

APP_HOST_BLOCK = """

        // 特别版：注册助手小面板的全局快捷键。失败只记日志，不阻断启动
        BetterGenshinImpact.View.Windows.AgentPanelWindow.EnsureGlobalHotkey();
"""


def patch(rel: str, marker: str, anchor: str, replacement: str, label: str) -> bool:
    """在 anchor 处插入/替换；marker 已存在则跳过。返回是否改动。"""
    path = ROOT / rel
    if not path.is_file():
        print(f"[x] 找不到 {rel}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")
    if marker in text:
        print(f"[=] {label}：已存在，跳过")
        return False

    if anchor not in text:
        print(f"[x] {label}：在 {rel} 中没有找到锚点，上游结构可能已改动", file=sys.stderr)
        print(f"    锚点：{anchor.splitlines()[0]}", file=sys.stderr)
        raise SystemExit(3)

    path.write_text(text.replace(anchor, replacement, 1), encoding="utf-8")
    print(f"[+] {label}")
    return True


def patch_page() -> bool:
    """在「自动分解圣遗物」卡片前插入内置 AI 卡片，缩进跟随该行。"""
    path = ROOT / SETTINGS_PAGE
    if not path.is_file():
        print(f"[x] 找不到 {SETTINGS_PAGE}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")
    if "<controls:AiSettingsCard" in text:
        print("[=] 设置页 AI 卡片：已存在，跳过")
        return False

    lines = text.split("\n")
    for index, line in enumerate(lines):
        if line.strip() == PAGE_MARK:
            indent = line[: len(line) - len(line.lstrip())]
            lines[index:index] = [f"{indent}<controls:AiSettingsCard />", ""]
            path.write_text("\n".join(lines), encoding="utf-8")
            print("[+] 设置页 AI 卡片")
            return True

    print(f"[x] {SETTINGS_PAGE} 中没有找到「{PAGE_MARK}」锚点", file=sys.stderr)
    raise SystemExit(3)


# ---------------------------------------------------------------- 13. 全局搜索
#
# 顶栏输入框 + 独立结果页。改到的三个文件（App.xaml.cs / MainWindow.xaml /
# MainWindow.xaml.cs）都是上游文件，同步会整体覆盖，补丁必须在这里重贴；
# Service/Search、SearchResultPage 这些新增文件由 sync_source 的 KEEP 保留。
#
# 2026-11-25 的同步把搜索整份删掉过一次：新增文件当时没进 KEEP，
# 「搜一下」这个入口凭空消失，而编译照样过——所以这里补了 KEEP。
MAIN_WINDOW_CS = "BetterGenshinImpact/View/MainWindow.xaml.cs"

SEARCH_BOX = '''                    <!-- 全局搜索：输入回车跳到「搜索结果」页，结果不在下拉里堆着 -->
                    <ui:TextBox x:Name="GlobalSearchBox"
                                Width="260"
                                Height="30"
                                Margin="18,0,0,0"
                                VerticalAlignment="Center"
                                FontSize="12.5"
                                Icon="{ui:SymbolIcon Search20}"
                                KeyDown="OnGlobalSearchKeyDown"
                                PlaceholderText="搜索  Ctrl+K"
                                ToolTip="搜页面、任务、脚本；回车看结果，Esc 清空" />
'''

HEADER_PATTERN = re.compile(
    r"(?P<open><ui:TitleBar\.Header>\s*<StackPanel[^>]*>)(?P<inner>.*?)(?P<close></StackPanel>\s*</ui:TitleBar\.Header>)",
    re.S)

SEARCH_CODE = '''    /// <summary>
    /// Ctrl+K 聚焦搜索框；Esc 在搜索框里时清空。
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 顶栏搜索框：回车提交，Esc 清空。
    /// </summary>
    private void OnGlobalSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                SubmitGlobalSearch();
                e.Handled = true;
                break;
            case Key.Escape:
                GlobalSearchBox.Text = string.Empty;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 把关键词交给搜索结果页，再导航过去。
    /// <para>
    /// 导航只接受页面类型，带不了参数，关键词走 <see cref="Service.Search.GlobalSearchState"/>。
    /// </para>
    /// </summary>
    private void SubmitGlobalSearch()
    {
        var keyword = GlobalSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        var state = App.GetService<GlobalSearchState>();
        if (state != null)
        {
            state.Query = keyword;
        }

        Navigate(typeof(SearchResultPage));
    }

'''


def patch_global_search() -> None:
    """顶栏搜索：XAML 包一层横向容器 + 代码后置三个方法 + DI 注册。"""
    xaml = ROOT / MAIN_WINDOW
    text = xaml.read_text(encoding="utf-8")
    if "GlobalSearchBox" not in text:
        match = HEADER_PATTERN.search(text)
        if match is None:
            print(f"[x] {MAIN_WINDOW}：找不到 TitleBar.Header 里的 StackPanel，上游结构可能已改动", file=sys.stderr)
            raise SystemExit(3)
        # 原 Header 里是一个带拖拽行为的 StackPanel：外面再包一层，把搜索框放到拖拽区之外，
        # 点输入框不会变成拖窗口。内层整体缩进 4 格，保持 XAML 可读。
        inner = "\n".join(
            ("    " + line) if line.strip() else line
            for line in match.group("inner").strip("\n").split("\n"))
        block = (match.group("open")
                 + '\n                    <StackPanel VerticalAlignment="Center" Orientation="Horizontal">\n'
                 + inner
                 + "\n                    </StackPanel>\n"
                 + SEARCH_BOX
                 + "                </StackPanel>\n            </ui:TitleBar.Header>")
        text = text[:match.start()] + block + text[match.end():]
        xaml.write_text(text, encoding="utf-8")
        print("[+] 顶栏搜索框")
    else:
        print("[=] 顶栏搜索框：已存在，跳过")

    code = ROOT / MAIN_WINDOW_CS
    source = code.read_text(encoding="utf-8")
    if "SubmitGlobalSearch" in source:
        print("[=] 顶栏搜索代码：已存在，跳过")
    else:
        using_anchor = "using BetterGenshinImpact.View.Behavior;"
        if using_anchor not in source:
            print(f"[x] {MAIN_WINDOW_CS} 缺少 using 锚点：{using_anchor}", file=sys.stderr)
            raise SystemExit(3)
        source = source.replace(
            using_anchor,
            "using BetterGenshinImpact.Service.Search;\n" + using_anchor + "\nusing BetterGenshinImpact.View.Pages;",
            1)

        ctor_anchor = ("        AddHandler(PreviewMouseWheelEvent, "
                       "new MouseWheelEventHandler(OnGlobalPreviewMouseWheel), true);")
        if ctor_anchor not in source:
            print(f"[x] {MAIN_WINDOW_CS} 找不到构造函数里的滚轮绑定，上游结构可能已改动", file=sys.stderr)
            raise SystemExit(3)
        source = source.replace(
            ctor_anchor,
            ctor_anchor + "\n        // Ctrl+K 直接聚焦顶栏搜索框，和浏览器/编辑器里的习惯一致"
                          "\n        PreviewKeyDown += OnWindowPreviewKeyDown;",
            1)

        method_anchor = "    private void OnGlobalPreviewMouseWheel(object sender, MouseWheelEventArgs e)"
        if method_anchor not in source:
            print(f"[x] {MAIN_WINDOW_CS} 找不到 OnGlobalPreviewMouseWheel，上游结构可能已改动", file=sys.stderr)
            raise SystemExit(3)
        source = source.replace(method_anchor, SEARCH_CODE + method_anchor, 1)
        code.write_text(source, encoding="utf-8")
        print("[+] 顶栏搜索代码")

    patch(APP, "using BetterGenshinImpact.Service.Search;", "using BetterGenshinImpact.Service.Guide;",
          "using BetterGenshinImpact.Service.Guide;\nusing BetterGenshinImpact.Service.Search;",
          "搜索 using")
    patch(APP, "AddView<SearchResultPage", "                services.AddView<MusicPage, MusicPageViewModel>();",
          "                services.AddView<MusicPage, MusicPageViewModel>();\n"
          "                // 全局搜索：顶栏输入，结果在独立页面里分组展示\n"
          "                services.AddSingleton<GlobalSearchState>();\n"
          "                services.AddSingleton<GlobalSearchService>();\n"
          "                services.AddView<SearchResultPage, SearchResultPageViewModel>();",
          "注册全局搜索")


# ---------------------------------------------------------------- 14. 版本需求比较
#
# 路线 / 脚本里的 bgiVersion 是「至少要这个版本」。特别版版本号长成
# 0.67.0-special.7.7.3，SemVer 把 -special.* 当预发布，排序在 0.67.0 之前，
# 于是要求 0.67.0 的路线在特别版上全部被判「版本不够」——2026-11-25 的线上日志里
# 所有标了 bgiVersion 的社区路线都被拒绝运行。
#
# Global.cs / Manifest.cs / PathingTask.cs 都是上游文件，同步会覆盖，所以在这里重贴。
GLOBAL = "BetterGenshinImpact/Core/Config/Global.cs"
MANIFEST = "BetterGenshinImpact/Core/Script/Project/Manifest.cs"
PATHING_TASK = "BetterGenshinImpact/GameTask/AutoPathing/Model/PathingTask.cs"

REQUIREMENT_ANCHOR = "    public static void WriteAllText(string relativePath, string blackListJson)"

REQUIREMENT_BLOCK = '''    /// <summary>
    ///     脚本 / 路线里写的 bgiVersion 是「至少要这个版本」，不是「必须精确等于」。
    ///     <para>
    ///         特别版版本号是 0.67.0-special.7.7.3 这种：SemVer 把 "-" 后面当预发布，
    ///         排序在 0.67.0 之前，要求 0.67.0 的路线在特别版上会全被判成「版本不够」。
    ///         特别版是在该内核版本上继续开发，比较时去掉 -special 段即可。
    ///     </para>
    ///     <para>
    ///         只用于「要求的最低版本」，不要拿去比较两个特别版之间的新旧——
    ///         那会把 0.67.0-special.8 和 0.67.0-special.7.7.3 看成一样。
    ///     </para>
    /// </summary>
    /// <param name="requiredVersion">脚本或路线要求的最低版本</param>
    /// <returns>当前版本是否满足要求；版本号解析失败时放行</returns>
    public static bool IsRequirementMet(string requiredVersion)
    {
        try
        {
            var required = SemVersion.Parse(StripSpecialSuffix(requiredVersion));
            var current = SemVersion.Parse(StripSpecialSuffix(Version));
            return required.CompareSortOrderTo(current) <= 0;
        }
        catch
        {
            // 写错的版本号不该让脚本直接不可用，交给其它校验
            return true;
        }
    }

    /// <summary>去掉特别版的 -special.x 后缀，只留内核版本。</summary>
    private static string StripSpecialSuffix(string version)
    {
        var index = version.IndexOf("-special", StringComparison.OrdinalIgnoreCase);
        return index > 0 ? version[..index] : version;
    }

'''


def patch_version_requirement() -> None:
    """把「最低版本」判断换成按内核版本比较，并去掉重复打三遍的错误日志。"""
    patch(GLOBAL, "IsRequirementMet", REQUIREMENT_ANCHOR,
          REQUIREMENT_BLOCK + REQUIREMENT_ANCHOR, "版本需求比较")

    for rel, marker, label in (
        (MANIFEST, "Global.IsNewVersion(BgiVersion)", "脚本清单版本检查"),
        (PATHING_TASK, "Global.IsNewVersion(task.Info.BgiVersion)", "路线版本检查"),
    ):
        path = ROOT / rel
        text = path.read_text(encoding="utf-8")
        if marker not in text:
            print(f"[=] {label}：已是新版，跳过")
            continue
        text = text.replace(marker, "!" + marker.replace("Global.IsNewVersion", "Global.IsRequirementMet"), 1)
        path.write_text(text, encoding="utf-8")
        print(f"[+] {label}")

    # PathingTask 里同一条错误日志连打三次，纯噪音
    path = ROOT / PATHING_TASK
    text = path.read_text(encoding="utf-8")
    line = r'[ \t]*TaskControl\.Logger\.LogError\("地图追踪任务[^\n]*\n'
    deduped, count = re.subn(f"({line})(?:{line})+", r"\1", text)
    if count:
        path.write_text(deduped, encoding="utf-8")
        print(f"[+] 路线版本日志去重（{count} 处）")


# ---------------------------------------------------------------- 15. 插件市场
#
# 市场客户端要进 DI（页面 VM 构造参数），而 App.xaml.cs 是上游文件。
# 市场本身的代码在 Core/Plugins/Market/ 与 ViewModel/Pages/Plugins/ 下，由 KEEP 保留。
def patch_plugin_market() -> None:
    patch(APP, "using BetterGenshinImpact.Core.Plugins.Market;",
          "using BetterGenshinImpact.Core.Plugins.Hosting;",
          "using BetterGenshinImpact.Core.Plugins.Hosting;\nusing BetterGenshinImpact.Core.Plugins.Market;",
          "插件市场 using")
    patch(APP, "AddSingleton<PluginMarketClient>", "                services.AddSingleton<GlobalSearchService>();",
          "                services.AddSingleton<GlobalSearchService>();\n"
          "                // 插件市场：读索引、下载并校验安装包\n"
          "                services.AddSingleton<PluginMarketClient>();",
          "注册插件市场")


# ---------------------------------------------------------------- 16. 特别版设置卡片
#
# 日志（落盘级别/诊断包/上传）与远程任务（QQ 机器人、米游社绑定）这两张卡片放在
# View/Controls/SpecialSettingsCards.xaml 里，页面只需要一行引用。
# 上游每次同步都会覆盖 CommonSettingsPage.xaml，所以这一行由本脚本补回。
SETTINGS_PAGE_COMMON = "BetterGenshinImpact/View/Pages/CommonSettingsPage.xaml"
CARD_CONTROLS = "BetterGenshinImpact/View/Controls/SpecialSettingsCards.xaml"


def patch_special_settings_cards() -> None:
    path = ROOT / SETTINGS_PAGE_COMMON
    if not path.is_file():
        print(f"[x] 找不到 {SETTINGS_PAGE_COMMON}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")
    if "<controls:SpecialSettingsCards" not in text:
        anchor = "        <!-- 网络与代理：卡片标题右侧显示当前模式，内容由 NetworkSettingsView 提供 -->"
        if anchor not in text:
            print("[x] 设置页：没有找到「网络与代理」卡片锚点，上游结构可能已改动", file=sys.stderr)
            raise SystemExit(3)
        text = text.replace(anchor, "        <controls:SpecialSettingsCards />\n\n" + anchor, 1)
        print("[+] 设置页插入特别版卡片")

    if 'xmlns:controls="clr-namespace:BetterGenshinImpact.View.Controls"' not in text:
        ua = '      xmlns:pageView="clr-namespace:BetterGenshinImpact.View.Pages.View"'
        if ua not in text:
            print("[x] 设置页：没有找到 xmlns 锚点", file=sys.stderr)
            raise SystemExit(3)
        text = text.replace(
            ua, '      xmlns:controls="clr-namespace:BetterGenshinImpact.View.Controls"\n' + ua, 1)
        print("[+] 设置页补充 controls 命名空间")

    path.write_text(text, encoding="utf-8")


# ---------------------------------------------------------------- 17. 远程任务与日志接线
#
# 远程任务（QQ 机器人）、米游社扫码、日志增强都注册在 App.xaml.cs 里，是上游文件。
REMOTE_TASK_ANCHOR = """                services.AddSingleton<NotificationService>();
                services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());
"""
REMOTE_TASK_BLOCK = """                // 远程任务：QQ 机器人把聊天指令转成独立任务并回传截图，只在主实例启动
                services.AddSingleton<AiTraceStore>();
                services.AddSingleton<MysDailyNoteClient>(sp => new MysDailyNoteClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                    sp.GetRequiredService<ILogger<MysDailyNoteClient>>()));
                services.AddSingleton<GenshinWikiClient>(sp => new GenshinWikiClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.GameApi),
                    sp.GetRequiredService<ILogger<GenshinWikiClient>>()));
                // 米游社扫码绑定：网页端扫码通道拿登录态，不需要用户抄 Cookie
                services.AddSingleton<MysPassportClient>(sp => new MysPassportClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.GameApi),
                    sp.GetRequiredService<ILogger<MysPassportClient>>()));
                services.AddSingleton<MysBindingService>();
                services.AddSingleton<GameOps>();
                services.AddSingleton<AgentTasks>();
                services.AddSingleton<RemoteTaskPlanRunner>();
                services.AddSingleton<RemoteTaskCommandHandler>();
                services.AddSingleton<RemoteTaskIntentResolver>();
                services.AddSingleton<QqBotService>();
                services.AddHostedService(sp => sp.GetRequiredService<QqBotService>());
                // 定时任务：按时间点或树脂阈值自动触发，与手动点开始同一条执行路径
                services.AddSingleton<RemoteScheduleService>();
                services.AddHostedService(sp => sp.GetRequiredService<RemoteScheduleService>());
"""

FILE_SINK_PATTERN = re.compile(
    r"[ \t]*\.WriteTo\.File\(logFile,.*?retainedFileTimeLimit: TimeSpan\.FromDays\(21\)\)\)",
    re.S)

FILE_SINK_BLOCK = """                        .WriteTo.Sink(new RepeatSuppressorSink(
                            new LoggerConfiguration()
                                .WriteTo.File(
                                    // 一行一条：时间 / 级别 / 实例 / 来源 → 消息；异常另起，方便按行 grep。
                                    // 行首带日期：按天滚动的文件如果只有时分秒，跨午夜后既排不出先后，
                                    // 也没法把「任务开始后发生过什么」算准。
                                    // 异常栈由格式器去重：同一条错误反复重试时只展开一次，见 ExceptionDedupTextFormatter。
                                    new ExceptionDedupTextFormatter(
                                        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{BgiInstance}] {SourceContext} | {Message:lj}{NewLine}"),
                                    logFile,
                                    rollingInterval: RollingInterval.Day,
                                    // 一天几百万行的极端情况不至于长成一个几百 MB 打不开的文件
                                    fileSizeLimitBytes: 64L * 1024 * 1024,
                                    rollOnFileSizeLimit: true,
                                    shared: true,
                                    retainedFileCountLimit: 31,
                                    retainedFileTimeLimit: TimeSpan.FromDays(21))
                                .CreateLogger()),
                            // 落盘级别可配：长时间只关心有没有报错的人可以设成 Error，
                            // 一天几百万行 Debug 既占地方又让 grep 变慢。
                            SpecialLogging.FileLogLevel(all)))"""


def patch_remote_task_and_logging() -> None:
    path = ROOT / APP
    if not path.is_file():
        print(f"[x] 找不到 {APP}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")

    # 1) 远程任务 + 米游社：整块注册
    if "AddSingleton<QqBotService>" not in text:
        if REMOTE_TASK_ANCHOR not in text:
            print("[x] App：没有找到 NotificationService 注册锚点", file=sys.stderr)
            raise SystemExit(3)
        text = text.replace(REMOTE_TASK_ANCHOR, REMOTE_TASK_ANCHOR + REMOTE_TASK_BLOCK, 1)
        print("[+] 注册远程任务与米游社服务")

    # 2) 日志：归档旧文件
    if "SpecialLogging.ArchiveLegacyLog" not in text:
        anchor = '                var logFile = Path.Combine(logFolder, "better-genshin-impact.log");\n'
        if anchor not in text:
            print("[x] App：没有找到 logFile 锚点", file=sys.stderr)
            raise SystemExit(3)
        text = text.replace(anchor, anchor +
                            "                // 旧版本行首只有时分秒，和带日期的新格式混在一个文件里读不出先后\n"
                            "                SpecialLogging.ArchiveLegacyLog(logFolder);\n", 1)
        print("[+] 日志归档调用")

    # 3) 日志：文件 sink 换成「抑制重复 + 异常去重 + 可配级别」
    if "RepeatSuppressorSink" not in text:
        replaced, count = FILE_SINK_PATTERN.subn(FILE_SINK_BLOCK, text)
        if count:
            text = replaced
            print("[+] 日志文件 sink 增强")
        else:
            # 不中断同步：日志格式退化不影响功能，但要让人看见
            print("[!] App：日志文件 sink 锚点没匹配上，落盘格式退回上游默认（去重与级别配置失效）",
                  file=sys.stderr)

    # 4) 出错自动攒诊断包
    if "ErrorDiagnosticsSink" not in text:
        anchor = """                loggerConfiguration.WriteTo.Sink(
                    new ExternalLogSink(externalLogHub, instanceIdentity),
                    LogEventLevel.Debug);
"""
        if anchor in text:
            text = text.replace(
                anchor, anchor +
                "                // 出错就自动攒一个诊断包：日志尾部 + 环境 + 脱敏配置，反馈时直接拖出去。\n"
                "                loggerConfiguration.WriteTo.Sink(new ErrorDiagnosticsSink(), LogEventLevel.Error);\n", 1)
            print("[+] 诊断包 sink")
        else:
            print("[!] App：没有找到 ExternalLogSink 锚点，诊断包 sink 未接入", file=sys.stderr)

    # 5) using
    for need, after in (
        ("using BetterGenshinImpact.Core.Special;", "using BetterGenshinImpact.Core.Script;"),
        ("using BetterGenshinImpact.Core.Mys;", "using BetterGenshinImpact.Core.Config;"),
        ("using BetterGenshinImpact.Service.RemoteTask;", "using BetterGenshinImpact.Service.Notification;"),
    ):
        if need in text:
            continue
        if after not in text:
            print(f"[!] App：缺少 using 锚点 {after}，{need} 未补", file=sys.stderr)
            continue
        text = text.replace(after, after + "\n" + need, 1)
        print(f"[+] {need}")

    path.write_text(text, encoding="utf-8")


# ---------------------------------------------------------------- 18. 插件版本比较
#
# 特别版的版本号是 0.67.0-special.x，SemVer 里低于 0.67.0。上游同步会覆盖这两处，
# 导致所有 sdk_version=0.67.0 的插件被判成「需要更高版本」而装不上。
GLOBAL_CONFIG = "BetterGenshinImpact/Core/Config/Global.cs"
PLUGIN_COMPAT = "BetterGenshinImpact/Core/Plugins/Hosting/PluginCompatibility.cs"


def patch_plugin_version_compare() -> None:
    # 1) Global：StripSpecialSuffix 必须是 public，插件兼容性检查才能调用
    path = ROOT / GLOBAL_CONFIG
    if not path.is_file():
        print(f"[x] 找不到 {GLOBAL_CONFIG}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")
    old = """    /// <summary>去掉特别版的 -special.x 后缀，只留内核版本。</summary>
    private static string StripSpecialSuffix(string version)"""
    new = """    /// <summary>
    /// 去掉特别版的 -special.x 后缀，只留内核版本。
    /// <para>
    /// SemVer 里 <c>0.67.0-special.7.7.3</c> 的优先级低于 <c>0.67.0</c>，
    /// 直接比会被判成「特别版比同版本的官方版还老」，脚本的 bgi_version 与插件的 sdk_version 都会误判。
    /// </para>
    /// </summary>
    public static string StripSpecialSuffix(string version)"""
    if "public static string StripSpecialSuffix" in text:
        print("[=] 插件版本比较：Global 已公开，跳过")
    elif old in text:
        path.write_text(text.replace(old, new, 1), encoding="utf-8")
        print("[+] Global.StripSpecialSuffix 改为 public")
    else:
        print("[x] Global：StripSpecialSuffix 锚点不匹配，上游可能改过", file=sys.stderr)
        raise SystemExit(3)

    # 2) PluginCompatibility：比较前剥掉 -special 后缀
    path = ROOT / PLUGIN_COMPAT
    if not path.is_file():
        print(f"[x] 找不到 {PLUGIN_COMPAT}", file=sys.stderr)
        raise SystemExit(2)

    text = path.read_text(encoding="utf-8")
    old = """        // 开发版等无法解析版本号的主程序跳过以下比较
        if (!TryParse(Global.Version, out var host))"""
    new = """        // 开发版等无法解析版本号的主程序跳过以下比较。
        // 特别版的版本号带 -special 后缀，SemVer 里它低于同版本号的正式版，
        // 不剥掉的话用官方 SDK（sdk_version 0.67.0）编译的插件会被判成「需要更高版本」而装不上。
        if (!TryParse(Global.StripSpecialSuffix(Global.Version), out var host))"""
    if "StripSpecialSuffix(Global.Version)" in text:
        print("[=] 插件版本比较：已剥后缀，跳过")
    elif old in text:
        path.write_text(text.replace(old, new, 1), encoding="utf-8")
        print("[+] 插件版本比较剥掉 -special 后缀")
    else:
        print("[x] PluginCompatibility：锚点不匹配，上游可能改过", file=sys.stderr)
        raise SystemExit(3)


def main() -> int:
    ensure_packages()
    patch_download_category()
    patch(ENGINE_EXTEND, 'AddHostObject("ocr"', HOST_ANCHOR,
          HOST_ANCHOR + HOST_BLOCK, "脚本宿主 ocr/quest/ai")
    patch(APP, "services.AddSingleton<AiKernel>();", APP_ANCHOR,
          APP_ANCHOR + APP_BLOCK, "注册 AiKernel")
    patch(PATH_EXECUTOR, "TryAiRescueAsync(screen, waypoint, position)", STUCK_ANCHOR,
          STUCK_BLOCK, "跑图卡死 AI 接管")
    patch_page()
    patch(SETTINGS_VM, "TestAiConnection", VM_ANCHOR,
          VM_BLOCK + VM_ANCHOR, "设置页测试命令")
    patch(UPDATE_SERVICE, "SpecialUpdater.TryHandleAsync", UPDATE_ANCHOR,
          UPDATE_HOOK + UPDATE_ANCHOR, "更新检查改道")
    patch(APP, "AddView<AiSettingsPage", PAGE_REG_ANCHOR,
          PAGE_REG_ANCHOR + PAGE_REG_BLOCK, "注册 AI 助手页")
    patch(MAIN_WINDOW, "pages:AiSettingsPage", NAV_ANCHOR,
          NAV_BLOCK, "侧边栏 AI 助手入口")
    patch(APP_HOST, "AgentPanelWindow.EnsureGlobalHotkey", APP_HOST_ANCHOR,
          APP_HOST_ANCHOR + APP_HOST_BLOCK, "助手小面板快捷键")
    patch_global_search()
    patch_version_requirement()
    patch_plugin_market()
    patch_special_settings_cards()
    patch_remote_task_and_logging()
    patch_plugin_version_compare()

    # EngineExtend / App / VM 需要引用 Core.Ai 里的 AiKernel
    for rel, anchor, line in (
        (ENGINE_EXTEND, "using BetterGenshinImpact.Core.Config;", "using BetterGenshinImpact.Core.Ai;\n"),
        (APP, "using BetterGenshinImpact.Core.Config;", "using BetterGenshinImpact.Core.Ai;\n"),
        (SETTINGS_VM, "using BetterGenshinImpact.Core.Config;", "using BetterGenshinImpact.Core.Ai;\n"),
    ):
        path = ROOT / rel
        text = path.read_text(encoding="utf-8")
        if "using BetterGenshinImpact.Core.Ai;" not in text:
            if anchor not in text:
                print(f"[x] {rel} 缺少 using 锚点：{anchor}", file=sys.stderr)
                return 3
            path.write_text(text.replace(anchor, line + anchor, 1), encoding="utf-8")
            print(f"[+] {rel} 补充 using")

    # UpdateService 需要引用 SpecialUpdater
    update_path = ROOT / UPDATE_SERVICE
    update_text = update_path.read_text(encoding="utf-8")
    if "using BetterGenshinImpact.Core.Special;" not in update_text:
        using_anchor = "using BetterGenshinImpact.Helpers;"
        if using_anchor not in update_text:
            print(f"[x] {UPDATE_SERVICE} 缺少 using 锚点：{using_anchor}", file=sys.stderr)
            return 3
        update_path.write_text(
            update_text.replace(using_anchor, "using BetterGenshinImpact.Core.Special;\n" + using_anchor, 1),
            encoding="utf-8")
        print(f"[+] {UPDATE_SERVICE} 补充 using")

    print("完成")
    return 0


if __name__ == "__main__":
    sys.exit(main())
