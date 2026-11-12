#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把特别版的改动重新注入上游源码，可重复执行。

上游同步（sync_source.py）会整体覆盖源码，本脚本负责把「改到上游文件里」的几处补丁贴回去：

1. EngineExtend.InitHost         —— ocr / quest / OcrFactory / Bv / ai 宿主
2. AllConfig                     —— 内置 AI 配置节点与旧配置兼容入口
3. App.xaml.cs                   —— 注册 AiKernel 单例
4. PathExecutor.MoveTo           —— 第三次卡死放弃前让内置 AI 接管
5. TaskSettingsPage.xaml         —— 插入内置 AI 设置卡片
6. TaskSettingsPageViewModel     —— 连通性测试命令
7. UpdateService.CheckUpdateAsync —— 特别版更新检查改道，不接官方更新通道
8. App.xaml.cs                   —— 注册「AI 助手」页（模型配置 + 本地助手）
9. MainWindow.xaml               —— 侧边栏加「AI 助手」入口

每一处都先判断是否已存在（幂等），锚点找不到就返回非 0，由 workflow 报警并开 issue。
新增文件（Core/Ai 等）不在这里处理，由 sync_source.py 的 KEEP 清单保留。
"""

import pathlib
import sys

ROOT = pathlib.Path.cwd()

ENGINE_EXTEND = "BetterGenshinImpact/Core/Script/EngineExtend.cs"
ALL_CONFIG = "BetterGenshinImpact/Core/Config/AllConfig.cs"
APP = "BetterGenshinImpact/App.xaml.cs"
PATH_EXECUTOR = "BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.cs"
SETTINGS_PAGE = "BetterGenshinImpact/View/Pages/TaskSettingsPage.xaml"
SETTINGS_VM = "BetterGenshinImpact/ViewModel/Pages/TaskSettingsPageViewModel.cs"
UPDATE_SERVICE = "BetterGenshinImpact/Service/UpdateService.cs"
MAIN_WINDOW = "BetterGenshinImpact/View/MainWindow.xaml"

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

CONFIG_ANCHOR = "    public AutoComboBuildConfig AutoComboBuildConfig { get; set; } = new();"

CONFIG_BLOCK = '''

    /// <summary>
    ///     内置大模型配置（特别版）
    /// </summary>
    public AiConfig AiConfig { get; set; } = new();

    /// <summary>
    ///     旧版脚本 AI 配置的兼容入口：读到时自动并入 <see cref="AiConfig"/>，不再单独写出。
    /// </summary>
    [JsonPropertyName("aiAgentConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyAiAgentConfig? LegacyAiConfig
    {
        get => null;
        set
        {
            if (value == null)
            {
                return;
            }

            // 旧配置只有端点/模型/密钥等基础项，接管相关开关按默认值补齐
            AiConfig.Enabled = value.Enabled;
            if (!string.IsNullOrWhiteSpace(value.Endpoint)) AiConfig.Endpoint = value.Endpoint;
            if (!string.IsNullOrWhiteSpace(value.ModelName)) AiConfig.ModelName = value.ModelName;
            if (!string.IsNullOrWhiteSpace(value.ApiKey)) AiConfig.ApiKey = value.ApiKey;
            if (value.Temperature > 0) AiConfig.Temperature = value.Temperature;
            if (value.TimeoutSeconds > 0) AiConfig.TimeoutSeconds = value.TimeoutSeconds;
            if (value.MaxCallsPerRun > 0) AiConfig.MaxCallsPerRun = value.MaxCallsPerRun;
            AiConfig.VisionMode = string.Equals(value.VisionMode, "multimodal", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(value.SystemPromptExtra)) AiConfig.SystemPromptExtra = value.SystemPromptExtra;
        }
    }

    /// <summary>
    ///     旧版（脚本专用）AI 配置结构，仅用于读取历史 config.json。
    /// </summary>
    public sealed class LegacyAiAgentConfig
    {
        public bool Enabled { get; set; }

        public string Endpoint { get; set; } = "";

        public string ModelName { get; set; } = "";

        public string ApiKey { get; set; } = "";

        /// <summary>旧版是字符串：<c>off</c> / <c>multimodal</c>。</summary>
        public string VisionMode { get; set; } = "off";

        public int MaxCallsPerRun { get; set; }

        public int TimeoutSeconds { get; set; }

        public double Temperature { get; set; }

        public string SystemPromptExtra { get; set; } = "";
    }'''

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
    patch(ENGINE_EXTEND, 'AddHostObject("ocr"', HOST_ANCHOR,
          HOST_ANCHOR + HOST_BLOCK, "脚本宿主 ocr/quest/ai")
    patch(ALL_CONFIG, "public AiConfig AiConfig", CONFIG_ANCHOR,
          CONFIG_ANCHOR + CONFIG_BLOCK, "AllConfig.AiConfig")
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

    # AllConfig / App / VM 需要额外的 using
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
