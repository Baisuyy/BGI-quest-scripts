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
