using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Service.RemoteTask;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 一次自由操作的结果。
/// </summary>
/// <param name="Success">模型是否自认完成。</param>
/// <param name="Summary">模型给出的一句话结论。</param>
/// <param name="Steps">走了多少步。</param>
/// <param name="Calls">消耗了多少次模型请求。</param>
/// <param name="Trace">本次轨迹，已落盘。</param>
public sealed record PilotOutcome(bool Success, string Summary, int Steps, int Calls, AiTraceEntry Trace);

/// <summary>
/// 界面代跑器：当用户要的事没有对应任务时，让模型自己看画面、自己点。
/// <para>
/// 与 <see cref="AiOperator"/> 的分工：那个负责「地图上有坐标的目标，怎么走过去」，
/// 这个负责「界面上的按钮，点哪里」。两者共用同一套输入层与预算控制。
/// </para>
/// <para>
/// 模型看到的坐标统一是 1920×1080 逻辑坐标——原神 PC 端的标准分辨率。
/// 实际画面是 2K/4K 时按比例换算，模型不需要知道用户的显示器尺寸。
/// </para>
/// <para>
/// 每一步都写进轨迹（动作、参数、结果、耗时），跑完落盘到 <c>log/ai-trace/</c>。
/// 成功的轨迹会在下次同类请求时作为参考喂回模型，这就是「自学习」的全部：
/// 不训练权重，只复用走通过的路。
/// </para>
/// </summary>
public sealed class GuiPilot(
    GameRuntime runtime,
    AllConfig config,
    AiKernel kernel,
    AiTraceStore traces,
    GameOps ops,
    AgentTasks agentTasks)
{
    private static readonly ILogger Logger = App.GetLogger<GuiPilot>();

    /// <summary>
    /// 截图编码器：模型要求看图时用它把画面压成 base64 JPEG。
    /// </summary>
    private readonly GameStateProbe _probe = new(runtime);

    /// <summary>
    /// 本步要附给模型的截图。工具结果只能是文本，图片在回填结果后单独作为一条用户消息发出。
    /// </summary>
    private string? _pendingImage;

    /// <summary>
    /// 模型坐标系宽度。
    /// </summary>
    private const int LogicalWidth = 1920;

    /// <summary>
    /// 模型坐标系高度。
    /// </summary>
    private const int LogicalHeight = 1080;

    /// <summary>
    /// 单次 OCR 摘要最多返回的文字块数，避免一屏几十条把上下文挤爆。
    /// </summary>
    private const int MaxOcrItems = 24;

    /// <summary>
    /// 工具集。
    /// </summary>
    private static readonly LlmTool[] Tools =
    [
        new("look", "重新看一眼当前画面，返回屏幕上的关键文字与坐标。不确定点了有没有生效时使用。"
                    + "如果光靠文字判断不了（纯图标按钮、开关状态、图片选项、界面布局），传 image=true 附一张截图。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["image"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "是否附带截图。文字够用时省略；需要看图标或界面布局时才传 true",
                    },
                },
            }),
        new("click", "点击画面上的一个位置。坐标按 1920×1080 给出，左上角是 (0,0)。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = new JsonObject { ["type"] = "integer", ["description"] = "0–1919" },
                    ["y"] = new JsonObject { ["type"] = "integer", ["description"] = "0–1079" },
                },
                ["required"] = new JsonArray("x", "y"),
            }),
        new("click_text", "在画面上找到一段文字并点它的中心。文字要和画面上显示的完全一致，只取一小段即可。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要点击的文字，例如「背包」「祈愿」" },
                },
                ["required"] = new JsonArray("text"),
            }),
        new("key", "按一个游戏按键。优先用语义名：map 地图、bag 背包、character 角色、wish 祈愿、party 队伍、menu 派蒙菜单、esc 返回、enter 确认、space 跳跃、f 交互。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "按键名" },
                },
                ["required"] = new JsonArray("name"),
            }),
        new("wait", "等待一会儿，让界面动画或加载走完。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["ms"] = new JsonObject { ["type"] = "integer", ["description"] = "200–10000" },
                },
                ["required"] = new JsonArray("ms"),
            }),
        new("drag", "按住左键从一点拖到另一点。拖动地图、拉滑块、转视角都用它；纯点击用 click。坐标按 1920×1080 给出。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x1"] = new JsonObject { ["type"] = "integer", ["description"] = "起点 x，0–1919" },
                    ["y1"] = new JsonObject { ["type"] = "integer", ["description"] = "起点 y，0–1079" },
                    ["x2"] = new JsonObject { ["type"] = "integer", ["description"] = "终点 x，0–1919" },
                    ["y2"] = new JsonObject { ["type"] = "integer", ["description"] = "终点 y，0–1079" },
                    ["ms"] = new JsonObject { ["type"] = "integer", ["description"] = "拖动时长，150–2000，默认 400" },
                },
                ["required"] = new JsonArray("x1", "y1", "x2", "y2"),
            }),
        new("scroll", "滚动滚轮。列表翻页、地图缩放、选择数量都用它；正数向上、负数向下。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["clicks"] = new JsonObject { ["type"] = "integer", ["description"] = "-10–10，正数向上" },
                    ["x"] = new JsonObject { ["type"] = "integer", ["description"] = "可选，先移到这个位置再滚" },
                    ["y"] = new JsonObject { ["type"] = "integer", ["description"] = "可选，先移到这个位置再滚" },
                },
                ["required"] = new JsonArray("clicks"),
            }),
        new("hold", "按住某个键一段时间。走路/冲刺/滑翔/长按技能用它：w/a/s/d 移动，shift 冲刺，space 滑翔，e/q 技能。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "按键名，如 w、shift、space、e、q" },
                    ["ms"] = new JsonObject { ["type"] = "integer", ["description"] = "按住多久，100–5000" },
                },
                ["required"] = new JsonArray("name", "ms"),
            }),
        new("shot", "把当前画面存成图片文件（失败留证、给用户看现场）。只截图不识别文字。", EmptySchema()),
        new("resin", "读取当前树脂数量（会先用米游社接口，失败再识别画面）。", EmptySchema()),
        new("done", "结束操作。做到或确认做不到时调用。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["success"] = new JsonObject { ["type"] = "boolean" },
                    ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "一句话说明结果" },
                },
                ["required"] = new JsonArray("success", "summary"),
            }),

        // 任务下发这一组排在后面，但优先级最高：能跑任务就不要用键鼠模拟。
        // 之前只给界面工具，模型面对「切号」「采素材」这类事只能干瞪眼，然后开始乱点。
        .. AgentTasks.Tools,
        // 路线与脚本体检：读路线点位、看断点、查脚本里的硬编码。
        // 没有这组，用户问「脚本跑到一半断了怎么办」时它只能凭记忆回答。
        .. AgentPathing.Tools,
    ];

    /// <summary>路线读取与脚本体检，无状态，直接复用。</summary>
    private static readonly AgentPathing Pathing = new();

    /// <summary>
    /// 语义按键到游戏动作的映射。
    /// </summary>
    private static readonly Dictionary<string, GIActions> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["map"] = GIActions.OpenMap,
        ["bag"] = GIActions.OpenInventory,
        ["inventory"] = GIActions.OpenInventory,
        ["character"] = GIActions.OpenCharacterScreen,
        ["wish"] = GIActions.OpenWishScreen,
        ["party"] = GIActions.OpenPartySetupScreen,
        ["battlepass"] = GIActions.OpenBattlePassScreen,
        ["quest"] = GIActions.OpenQuestMenu,
        ["menu"] = GIActions.OpenPaimonMenu,
        ["handbook"] = GIActions.OpenAdventurerHandbook
    };

    /// <summary>
    /// 直接按键的映射。
    /// </summary>
    private static readonly Dictionary<string, User32.VK> RawKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = User32.VK.VK_ESCAPE,
        ["escape"] = User32.VK.VK_ESCAPE,
        ["enter"] = User32.VK.VK_RETURN,
        ["space"] = User32.VK.VK_SPACE,
        ["f"] = User32.VK.VK_F,
        ["tab"] = User32.VK.VK_TAB,
        ["shift"] = User32.VK.VK_SHIFT,
        ["ctrl"] = User32.VK.VK_CONTROL,
        ["e"] = User32.VK.VK_E,
        ["q"] = User32.VK.VK_Q,
        ["1"] = User32.VK.VK_1,
        ["2"] = User32.VK.VK_2,
        ["3"] = User32.VK.VK_3,
        ["4"] = User32.VK.VK_4,
        ["w"] = User32.VK.VK_W,
        ["a"] = User32.VK.VK_A,
        ["s"] = User32.VK.VK_S,
        ["d"] = User32.VK.VK_D
    };

    private static JsonObject EmptySchema() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    /// <summary>
    /// 跑一次自由操作。
    /// </summary>
    /// <param name="goal">用户要做什么，原话或提炼后的短句。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<PilotOutcome> RunAsync(string goal, CancellationToken ct = default)
    {
        var settings = config.RemoteTaskConfig;
        // 登录、下载资源、过场这类流程本来就长，16 步根本走不完，模型会被硬截断
        var maxSteps = Math.Clamp(settings.SelfLearningMaxSteps, 6, 60);
        var startedAt = DateTime.Now;
        var callsAtStart = kernel.Calls;
        var steps = new List<AiTraceStep>();
        var ready = 0;
        var summary = "未完成";
        var success = false;

        // 游戏没开时第一次截图就会抛 NormalEndException，冒到机器人层只剩一条「处理远程指令失败」。
        // 在这里拦下来直接说人话，也省掉一次模型请求。
        if (runtime.IsStopping)
        {
            var empty = new AiTraceEntry(AiTraceStore.NewId(), startedAt, goal, false,
                "游戏没在运行", 0, 0, []);
            await traces.SaveAsync(empty).ConfigureAwait(false);
            return new PilotOutcome(false, "游戏没在运行（或画面采集已停），先开游戏再叫我", 0, 0, empty);
        }

        // 历史成功路径：有就直接写进提示词，模型照抄比重新摸索稳。
        // 没有成功样本时退一步，把同类失败的最后两步当作「别这么干」的提醒。
        var reuse = traces.FindReusable(goal);
        var lesson = reuse == null ? traces.FindFailedLesson(goal) : null;
        var conversation = new LlmConversation();
        conversation.AddUser(BuildFirstMessage(goal, reuse, lesson), null);

        InputHub.Foreground.ReleaseAll();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            for (var step = 1; step <= maxSteps && !ct.IsCancellationRequested; step++)
            {
                LlmReply reply;
                try
                {
                    reply = await kernel.SendAsync("自由操作", conversation, Tools, BuildSystemPrompt(goal, maxSteps),
                        ct).ConfigureAwait(false);
                }
                catch (AiUnavailableException e)
                {
                    summary = e.Message;
                    break;
                }

                conversation.AddAssistant(reply);
                if (!reply.WantsTool)
                {
                    // 只说不动：催一次，一直不动就收工，别把步数烧在解释上
                    if (++ready >= 2)
                    {
                        summary = string.IsNullOrWhiteSpace(reply.Text) ? "模型没有执行动作" : reply.Text;
                        break;
                    }

                    conversation.AddUser("不要解释，直接调用一个工具。做完调用 done。");
                    continue;
                }

                ready = 0;
                var call = reply.Calls[0];
                var stepWatch = Stopwatch.StartNew();
                var (finished, ok, content) = await ExecuteAsync(call, step, ct).ConfigureAwait(false);
                stepWatch.Stop();
                steps.Add(new AiTraceStep(step, call.Name, Compact(call.ArgumentsJson), Clip(content, 120),
                    (int)stepWatch.ElapsedMilliseconds));
                Logger.LogInformation("[自由操作] {Step}/{Max} {Tool}({Args}) → {Result}",
                    step, maxSteps, call.Name, Compact(call.ArgumentsJson), Clip(content, 80));

                if (finished)
                {
                    success = ok;
                    summary = content;
                    break;
                }

                conversation.AddToolResult(call.Id, content);

                // 截图单独补一条用户消息：tool 消息只能是文本，图片挂在 user 上面才发得出去
                if (_pendingImage is { } image)
                {
                    _pendingImage = null;
                    conversation.AddUser("这是刚才那张截图：", image);
                }

                // 模型偶尔一次返回多个工具调用，这里只跑第一个。其余的必须各回一条 tool 消息，
                // 否则下一轮请求会因为「tool_calls 后面缺 tool 响应」被服务端直接 400 打断。
                for (var i = 1; i < reply.Calls.Count; i++)
                {
                    conversation.AddToolResult(reply.Calls[i].Id, "同一步只执行一个动作，这个已跳过。");
                }
            }
        }
        finally
        {
            InputHub.Foreground.ReleaseAll();
        }

        var entry = new AiTraceEntry(
            AiTraceStore.NewId(),
            startedAt,
            goal,
            success,
            summary,
            steps.Count,
            kernel.Calls - callsAtStart,
            steps);
        await traces.SaveAsync(entry).ConfigureAwait(false);
        Logger.LogInformation("[自由操作] 结束 · {Result} · {Steps} 步 · 耗时 {Elapsed:F1}s · {Summary}",
            success ? "成功" : "失败", steps.Count, stopwatch.Elapsed.TotalSeconds, summary);
        return new PilotOutcome(success, summary, steps.Count, kernel.Calls - callsAtStart, entry);
    }

    /// <summary>
    /// 执行一次工具调用。
    /// </summary>
    /// <returns>是否结束、是否成功、给模型看的结果文本。</returns>
    private async Task<(bool Finished, bool Success, string Content)> ExecuteAsync(LlmToolCall call, int step,
        CancellationToken ct)
    {
        // 上一轮没发出去的截图不能带到这一步，否则模型会拿旧图解释新动作
        _pendingImage = null;
        var args = ParseArgs(call.ArgumentsJson);
        switch (call.Name)
        {
            case "look":
                return (false, false, await SnapshotAsync("当前画面", ct, GetBool(args, "image", false))
                    .ConfigureAwait(false));

            case "click":
            {
                var x = Math.Clamp(GetInt(args, "x", -1), 0, LogicalWidth - 1);
                var y = Math.Clamp(GetInt(args, "y", -1), 0, LogicalHeight - 1);
                if (GetInt(args, "x", -1) < 0 || GetInt(args, "y", -1) < 0)
                {
                    return (false, false, "坐标不合法，x/y 必须是 0 以上的整数。");
                }

                ops.EnsureForeground();
                using (var screen = runtime.CaptureToRectArea())
                {
                    var gx = (int)Math.Round(x * (double)screen.Width / LogicalWidth);
                    var gy = (int)Math.Round(y * (double)screen.Height / LogicalHeight);
                    screen.ClickTo(gx, gy);
                }

                await Task.Delay(700, ct).ConfigureAwait(false);
                return (false, false, await SnapshotAsync($"已点击 ({x}, {y})", ct).ConfigureAwait(false));
            }

            case "click_text":
            {
                var text = GetString(args, "text", string.Empty);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return (false, false, "text 不能为空。");
                }

                var hit = FindText(text);
                if (hit == null)
                {
                    return (false, false, $"画面上没找到「{text}」。可以先 look 看一下有哪些字。");
                }

                ops.EnsureForeground();
                using (var screen = runtime.CaptureToRectArea())
                {
                    var gx = (int)Math.Round(hit.Value.X * (double)screen.Width / LogicalWidth);
                    var gy = (int)Math.Round(hit.Value.Y * (double)screen.Height / LogicalHeight);
                    screen.ClickTo(gx, gy);
                }

                await Task.Delay(700, ct).ConfigureAwait(false);
                return (false, false, await SnapshotAsync($"已点击「{text}」", ct).ConfigureAwait(false));
            }

            case "key":
            {
                var name = GetString(args, "name", string.Empty);
                ops.EnsureForeground();
                if (Keys.TryGetValue(name, out var action))
                {
                    InputHub.Foreground.SimulateAction(action, KeyType.KeyPress);
                }
                else if (RawKeys.TryGetValue(name, out var vk))
                {
                    InputHub.Foreground.Keyboard.KeyPress(vk);
                }
                else
                {
                    return (false, false, $"不认识的按键「{name}」，可用：{string.Join("/", Keys.Keys.Concat(RawKeys.Keys).Take(16))}");
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
                return (false, false, await SnapshotAsync($"已按 {name}", ct).ConfigureAwait(false));
            }

            case "wait":
            {
                // 下载资源、加载地图动辄几十秒，上限太低模型只能反复小步等，白烧步数
                var ms = Math.Clamp(GetInt(args, "ms", 800), 200, 30000);
                await Task.Delay(ms, ct).ConfigureAwait(false);
                return (false, false, $"已等待 {ms} 毫秒。");
            }

            case "drag":
            {
                var x1 = Math.Clamp(GetInt(args, "x1", -1), 0, LogicalWidth - 1);
                var y1 = Math.Clamp(GetInt(args, "y1", -1), 0, LogicalHeight - 1);
                var x2 = Math.Clamp(GetInt(args, "x2", -1), 0, LogicalWidth - 1);
                var y2 = Math.Clamp(GetInt(args, "y2", -1), 0, LogicalHeight - 1);
                if (x1 < 0 || y1 < 0 || x2 < 0 || y2 < 0)
                {
                    return (false, false, "四个坐标都要给，范围 0–1919 / 0–1079。");
                }

                var ms = Math.Clamp(GetInt(args, "ms", 400), 150, 2000);
                ops.EnsureForeground();

                int ax, ay, bx, by;
                using (var screen = runtime.CaptureToRectArea())
                {
                    (ax, ay) = screen.ConvertPositionToDesktopRegion(x1, y1);
                    (bx, by) = screen.ConvertPositionToDesktopRegion(x2, y2);
                }

                // 分步移动而不是瞬移：游戏按帧读鼠标位置，一步跳过去会被当成没动
                DesktopRegion.DesktopRegionMove(ax, ay);
                InputHub.Foreground.Mouse.LeftButtonDown();
                var segments = Math.Clamp(ms / 16, 8, 60);
                for (var i = 1; i <= segments; i++)
                {
                    var t = (double)i / segments;
                    DesktopRegion.DesktopRegionMove(ax + (bx - ax) * t, ay + (by - ay) * t);
                    await Task.Delay(Math.Max(8, ms / segments), ct).ConfigureAwait(false);
                }

                InputHub.Foreground.Mouse.LeftButtonUp();
                await Task.Delay(500, ct).ConfigureAwait(false);
                return (false, false, await SnapshotAsync($"已从 ({x1}, {y1}) 拖到 ({x2}, {y2})", ct).ConfigureAwait(false));
            }

            case "scroll":
            {
                var clicks = Math.Clamp(GetInt(args, "clicks", 0), -10, 10);
                if (clicks == 0)
                {
                    return (false, false, "clicks 不能为 0；正数向上滚、负数向下滚。");
                }

                ops.EnsureForeground();
                if (GetInt(args, "x", -1) >= 0 && GetInt(args, "y", -1) >= 0)
                {
                    using var screen = runtime.CaptureToRectArea();
                    var (mx, my) = screen.ConvertPositionToDesktopRegion(
                        Math.Clamp(GetInt(args, "x", 0), 0, LogicalWidth - 1),
                        Math.Clamp(GetInt(args, "y", 0), 0, LogicalHeight - 1));
                    DesktopRegion.DesktopRegionMove(mx, my);
                    await Task.Delay(120, ct).ConfigureAwait(false);
                }

                InputHub.Foreground.Mouse.VerticalScroll(clicks);
                await Task.Delay(500, ct).ConfigureAwait(false);
                return (false, false, await SnapshotAsync($"已滚动 {clicks} 格", ct).ConfigureAwait(false));
            }

            case "hold":
            {
                var name = GetString(args, "name", string.Empty);
                var ms = Math.Clamp(GetInt(args, "ms", 600), 100, 5000);
                ops.EnsureForeground();

                if (Keys.TryGetValue(name, out var holdAction))
                {
                    InputHub.Foreground.SimulateAction(holdAction, KeyType.KeyDown);
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                    InputHub.Foreground.SimulateAction(holdAction, KeyType.KeyUp);
                }
                else if (RawKeys.TryGetValue(name, out var holdKey))
                {
                    InputHub.Foreground.Keyboard.KeyDown(holdKey);
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                    InputHub.Foreground.Keyboard.KeyUp(holdKey);
                }
                else
                {
                    return (false, false, $"不认识的按键「{name}」，可用：{string.Join("/", Keys.Keys.Concat(RawKeys.Keys).Take(20))}");
                }

                await Task.Delay(250, ct).ConfigureAwait(false);
                return (false, false, await SnapshotAsync($"已按住 {name} {ms} 毫秒", ct).ConfigureAwait(false));
            }

            case "shot":
            {
                ops.EnsureForeground();
                var path = SaveShot();
                return (false, false, path == null ? "截图失败，画面可能还没准备好。" : $"已保存：{path}");
            }

            case "resin":
            {
                var snapshot = await ops.ReadResinAsync(ct).ConfigureAwait(false);
                return (false, false, snapshot == null
                    ? $"读不到树脂：{ops.LastResinError ?? "米游社与画面两条路都不通"}。"
                    : snapshot.ToString());
            }

            case "done":
            {
                var ok = GetBool(args, "success", true);
                var text = GetString(args, "summary", ok ? "已完成" : "做不到");
                return (true, ok, text);
            }

            default:
                // 任务下发、路线、脚本都归 AgentTasks，参数校验与等待策略也在那边
                if (AgentTasks.Handles(call.Name))
                {
                    var content = await agentTasks.ExecuteAsync(call.Name, args, ct).ConfigureAwait(false);
                    return (false, false, content);
                }

                // 路线读取与脚本体检是纯本地只读操作，不碰游戏，直接给结果
                if (AgentPathing.Handles(call.Name))
                {
                    var content = await Pathing.ExecuteAsync(call.Name, args, ct).ConfigureAwait(false);
                    return (false, false, content);
                }

                return (false, false, $"未知工具 {call.Name}。可用工具见系统提示。");
        }
    }

    /// <summary>
    /// 把当前画面存成 JPEG，返回文件路径；失败返回 null。
    /// <para>
    /// 失败现场比「任务已完成」的截图有用：出问题时用户能看到卡在哪个界面、哪句提示上。
    /// 200 张一轮，数量涨得太快时人工清一次 <c>log/ai-shot</c> 即可。
    /// </para>
    /// </summary>
    private string? SaveShot()
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "log", "ai-shot");
            Directory.CreateDirectory(folder);
            using var screen = runtime.CaptureToRectArea();
            var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{AiTraceStore.NewId()}.jpg");
            Cv2.ImWrite(path, screen.SrcMat);
            Logger.LogInformation("[自由操作] 现场截图已保存 {Path}", path);
            return path;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "保存现场截图失败");
            return null;
        }
    }

    /// <summary>
    /// 抓一张当前画面，返回 OCR 文字清单给模型；<paramref name="withImage"/> 为 true 时另外附一张截图。
    /// <para>
    /// 截图不放进文字里，是因为工具结果只能是文本：这里先记下 base64，
    /// 由调用方在回填工具结果后作为一条用户消息补发。
    /// </para>
    /// </summary>
    /// <param name="prefix">结果前缀，说明刚才做了什么。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="withImage">是否附带截图。</param>
    private async Task<string> SnapshotAsync(string prefix, CancellationToken ct, bool withImage = false)
    {
        var builder = new StringBuilder(prefix);
        try
        {
            ops.EnsureForeground();
            using var screen = runtime.CaptureToRectArea();
            var lines = await Task.Run(() => ReadTexts(screen, screen.Width, screen.Height), ct).ConfigureAwait(false);

            if (withImage)
            {
                // 尺寸压到 768 宽，一张大约 1500 以内的视觉 token，够看清图标和布局
                _pendingImage = _probe.EncodeScreenshot(screen);
                builder.Append(_pendingImage is null ? "\n（截图失败，只能看文字）" : "\n（后面附了一张当前画面）");
            }

            builder.Append('\n');
            // HUD 上永远有一堆数字（UID、血量、延迟、帧率）和单字母快捷键标记。
            // 它们每屏都在，混在可点文字里会把模型的注意力带偏，所以分开列。
            var noise = lines.Where(line => IsHudNoise(line.Text)).ToList();
            var actionable = lines.Where(line => !IsHudNoise(line.Text)).ToList();

            builder.Append("屏幕文字：");
            if (actionable.Count == 0)
            {
                builder.Append(lines.Count == 0 ? "（没有识别到文字）" : "（只有数字和快捷键标记，见下）");
            }

            foreach (var line in actionable.Take(MaxOcrItems))
            {
                builder.Append("\n· ").Append(line.Text).Append(" @ (").Append(line.X).Append(", ").Append(line.Y).Append(')');
            }

            if (actionable.Count > MaxOcrItems)
            {
                builder.Append("\n（还有 ").Append(actionable.Count - MaxOcrItems).Append(" 条未列出）");
            }

            if (noise.Count > 0)
            {
                // 数字不是没用（原石数量、剩余次数都在这），但要明确标成「通常不用点」。
                builder.Append("\nHUD 数字与快捷键（一般不用点击）：")
                    .Append(string.Join("、", noise.Take(10).Select(line => line.Text)));
            }

            // 认出「这是加载/下载界面」比给模型多十条文字更有用：
            // 不然它会以为界面没反应，反复小步等待，把步数烧光。
            if (DetectState(lines) is { Length: > 0 } hint)
            {
                builder.Append("\n判断：").Append(hint);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "读取画面失败");
            builder.Append("\n（读取画面失败，可稍后重试）");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 从画面文字里判断当前处于什么阶段，给模型一句人话提示。
    /// </summary>
    /// <param name="lines">OCR 出来的文字块。</param>
    /// <returns>提示；认不出来时返回空串。</returns>
    private static string DetectState(IReadOnlyList<(string Text, int X, int Y)> lines)
    {
        static bool Has(IReadOnlyList<(string Text, int X, int Y)> source, params string[] keys)
            => source.Any(line => keys.Any(key => line.Text.Contains(key, StringComparison.Ordinal)));

        if (Has(lines, "准备下载资源", "正在下载", "下载资源", "资源校验", "更新中"))
        {
            return "这是资源下载/更新界面，可能持续几分钟。用 wait 20000–30000 等，不要连点，也不要换做法。";
        }

        if (Has(lines, "正在连接", "重新连接", "连接超时", "网络异常", "二级地址解析失败"))
        {
            return "网络连接有问题，先等 5–10 秒看会不会自己恢复；一直失败就如实报告，别硬点。";
        }

        if (Has(lines, "正在加载", "加载中", "Now Loading", "Loading"))
        {
            return "场景正在加载，wait 3000–8000 再看，不要重复点击。";
        }

        if (Has(lines, "点击屏幕继续", "点击任意位置", "点击继续"))
        {
            return "当前是「点击继续」类提示，click 一次屏幕中央即可。";
        }

        if (Has(lines, "欢迎进入游戏", "UID:", "UID："))
        {
            return "已经进入游戏主界面，可以开始执行目标了。";
        }

        return string.Empty;
    }

    /// <summary>
    /// HUD 噪声：纯数字（UID、血量、延迟、剩余次数）、单双字符快捷键标记。
    /// 它们几乎每屏都在，且永远点不动，混进可点文字里只会带偏模型。
    /// </summary>
    private static bool IsHudNoise(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return true;
        }

        // 纯数字/时间/比例：5765293、14977/14977、18:00、1.5、35%
        if (value.Any(char.IsDigit)
            && value.All(c => char.IsDigit(c) || c is '/' or ':' or '.' or '%' or '+' or '-' or ',' or ' '))
        {
            return true;
        }

        // 延迟与帧率：35 ms、60fps
        var lower = value.ToLowerInvariant();
        if ((lower.EndsWith("ms") || lower.EndsWith("fps")) && value[..^2].Trim().All(c => char.IsDigit(c) || c == '.'))
        {
            return true;
        }

        // 单双字符快捷键标记：Q、E、WS、X、F1
        return value.Length <= 2 && value.All(c => c < 128);
    }

    private List<(string Text, int X, int Y)> ReadTexts(ImageRegion screen, int width, int height)
    {
        var result = new List<(string Text, int X, int Y)>();
        try
        {
            var ocr = OcrFactory.Paddle.OcrResult(screen.SrcMat);
            foreach (var region in ocr.Regions)
            {
                var text = (region.Text ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                var x = (int)Math.Round(region.Rect.Center.X * LogicalWidth / width);
                var y = (int)Math.Round(region.Rect.Center.Y * LogicalHeight / height);
                result.Add((text, Math.Clamp(x, 0, LogicalWidth - 1), Math.Clamp(y, 0, LogicalHeight - 1)));
            }

            result = result
                .OrderBy(item => item.Y / 24)
                .ThenBy(item => item.X)
                .ToList();
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "OCR 失败");
        }

        return result;
    }

    /// <summary>
    /// 在画面上找一段文字，返回其中心点的逻辑坐标。
    /// </summary>
    private (int X, int Y)? FindText(string text)
    {
        try
        {
            using var screen = runtime.CaptureToRectArea();
            var lines = ReadTexts(screen, screen.Width, screen.Height);
            // 先找完全相等的，再退到包含，避免「祈愿」命中「祈愿记录」这种更长的条目
            var exact = lines.FirstOrDefault(line => string.Equals(line.Text, text, StringComparison.Ordinal));
            if (exact.Text != null)
            {
                return (exact.X, exact.Y);
            }

            var partial = lines
                .Where(line => line.Text.Contains(text, StringComparison.Ordinal))
                .OrderBy(line => line.Text.Length)
                .Select(line => ((int X, int Y)?)(line.X, line.Y))
                .FirstOrDefault();
            return partial;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "查找文字失败");
            return null;
        }
    }

    /// <summary>
    /// 第一条用户消息：目标 + 历史成功路径 + 起步提示。
    /// </summary>
    private string BuildFirstMessage(string goal, AiTraceEntry? reuse, string? lesson)
    {
        var builder = new StringBuilder();
        builder.Append("用户说的：").Append(goal).AppendLine();
        if (reuse != null)
        {
            builder.AppendLine("以前做成过类似的事，路径如下，可以参考但不要盲信（界面可能已经不一样）：");
            builder.AppendLine(reuse.ToPlaybook());
        }

        if (!string.IsNullOrWhiteSpace(lesson))
        {
            builder.AppendLine("教训：" + lesson);
        }

        builder.AppendLine();
        builder.AppendLine("先分清这句话是「要你动手」还是「在说你」：");
        builder.AppendLine("· 疑问、抱怨、纠正、闲聊（「你怎么没登出」「不是这样」「你是不是…」）——不是任务。");
        builder.AppendLine("  用 look 看一眼现状，照实回答；用户说的和画面对得上就承认，对不上就指出差在哪。不要为了「完成任务」去改游戏状态。");
        builder.AppendLine("· 确实是操作请求（清体力、切号、领邮件、刷本、采集）——先 list_tasks 看有没有现成任务，没有才操作界面。");
        builder.AppendLine("拿不准时按「不是任务」处理，回答完问一句要不要现在做。");
        return builder.ToString();
    }

    /// <summary>
    /// 系统提示词。
    /// <para>
    /// 这里的排序就是优先级：先任务、再路线、最后才是键鼠。以前只给界面工具，
    /// 模型遇到「切号」「采素材」这类事只能硬点，点不动就开始原地等，最后报「做不到」。
    /// </para>
    /// </summary>
    private static string BuildSystemPrompt(string goal, int maxSteps)
    {
        return $$"""
                 你是原神自动化工具 BetterGI 的操作中枢，正在直接控制一台真实的游戏客户端。
                 用户说的：{{goal}}

                 坐标约定：所有坐标都按 1920×1080 给出，(0,0) 是画面左上角。不要输出其他分辨率。

                 ===== 第一步：这是任务还是提问 =====
                 用户的话不一定是要你操作。先分类，再动手：
                 · 提问 / 抱怨 / 纠正 / 闲聊（「你怎么没登出」「不是这个意思」「你是不是…」）
                   → 不是任务。look 看一眼现状，如实回答，说明「现在是什么状态、和你说的是否一致」。
                   不要为了让目标「成立」去改游戏状态，也不要用 done(success=true) 把抱怨包装成完成。
                 · 操作请求（清体力、切号、领邮件、刷本、采集、跑脚本）
                   → 按下面的能力优先级执行。
                 分不清时按「不是任务」处理：先回答，末尾问一句「要我现在去做吗」。
                 只有当用户明确要求「做」或「继续」时，才假设要动手。

                 ===== 能力与优先级 =====
                 一、下发内置任务（首选，系统自己跑得比你点得稳）
                 · list_tasks 看有哪些任务；describe_task 看参数；run_task 下发
                 · list_pathing / run_pathing：采集素材、跑地图路线
                 · list_paths / read_path / path_progress：看路线点位、比对新旧路线、查断点（脚本跑到一半断了先看这里）
                 · review_script：只读体检脚本，列出无关输出、写死队伍名、写文件、绝对路径
                   —— 用户说「这脚本私货太多」时先调它，别凭空评价。你没有写文件的权限，
                   要改就说明「第几行改成什么」，让用户自己在脚本编辑器里改
                 · list_scripts / run_script：跑用户脚本；脚本源码可用 read 读，要改就用 write_file
                 · list_accounts 看账号页里配了哪些号；切号用 run_task switch_account，params 填 {"account":"账号名或UID"}
                   —— 登录界面全是图标和图片，靠点击点不出来，不要自己去试
                 · task_status 查进度，stop_task 停止
                 · 凡是内置任务能做的，绝不要用键鼠模拟替代。登录、切号、领奖、刷本、清体力、采集都属于这一类。

                 二、操作界面（内置任务做不到时）
                 · look 看画面文字与坐标；click_text 点文字；click 点坐标；key 按键
                 · drag 拖动（地图、滑块、视角）；scroll 滚轮（列表翻页、地图缩放）
                 · hold 按住键走路/冲刺/滑翔：w+a/s/d 走位，shift 冲刺，space 滑翔，e/q 放技能
                 · shot 存一张现场图：连续几轮没进展、或要留失败证据时用，路径会写进日志
                 · 只有图标没有文字的地方（设置齿轮、开关、图片选项）OCR 读不到，
                   look 带 image=true 要一张截图，看清位置再点
                 · 打开界面用 key：map 地图、bag 背包、character 角色、wish 祈愿、party 队伍、menu 派蒙菜单、esc 返回
                 · look 返回里「HUD 数字与快捷键」那一行是 UID、血量、延迟之类的固定显示，通常点不动，不要拿它当目标

                 ===== 行动规则 =====
                 1. 每轮只调用一个工具，看返回结果再决定下一步。
                 2. 不确定有哪些能力时先 list_tasks，不要凭记忆编任务标识。
                 3. 界面操作优先 click_text，比猜坐标稳；文字要抄画面上原样的短语，不要自己缩写。
                 4. 看到「加载中」「准备下载资源」「过场」这类画面：用 wait 5000–30000 毫秒等，**不要连点**。
                    连点只会把界面点到别处，然后彻底迷失。
                 5. 点击之后要核对结果：标题、面包屑、底部按钮有一个变了，才算真的进去了。
                    「屏幕文字变了」不等于「点对了地方」——换了个页面也可能是点歪了。
                 6. 同一个工具、同样的参数连续调用两次仍没有进展，第三次必须换方法：
                    换点击目标、换按键路径（比如先从 esc 回主界面）、或 look 带 image=true 看图标。
                    连着三轮原地打转就停下来说明卡在哪，不要耗光步数。
                 7. 弹窗、确认框、协议、年龄提示都要先处理掉，否则点不到底下的东西。
                 8. 系统提示「已达每小时上限」时，是调用额度用尽了，不是操作失败。
                    直接 done(success=false) 说明「AI 额度用完，稍后再试或手动操作」，不要换个说法重试。
                 9. done(success=true) 只能用于「画面能证明目标已达成」的情况。
                    证明不了就 success=false，并在 summary 里写清楚看到了什么、缺哪一步。
                 10. 最多 {{maxSteps}} 步。步数紧张时只做最有把握的那一步。
                 """;
    }

    private static JsonObject ParseArgs(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        }
        catch (Exception)
        {
            return new JsonObject();
        }
    }

    private static string GetString(JsonObject args, string key, string fallback)
        => args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;

    private static int GetInt(JsonObject args, string key, int fallback)
    {
        if (args[key] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : fallback;
    }

    private static bool GetBool(JsonObject args, string key, bool fallback)
        => args[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    /// <summary>
    /// 把参数 JSON 压成一行短串，用于日志与轨迹。
    /// </summary>
    private static string Compact(string json)
        => json.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace("  ", string.Empty).Trim();

    /// <summary>
    /// 截断长文本。
    /// </summary>
    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
