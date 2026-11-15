using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    GameOps ops)
{
    private static readonly ILogger Logger = App.GetLogger<GuiPilot>();

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
        new("look", "重新看一眼当前画面，返回屏幕上的关键文字与坐标。不确定点了有没有生效时使用。", EmptySchema()),
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
    ];

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
        var maxSteps = Math.Clamp(settings.SelfLearningMaxSteps, 4, 40);
        var startedAt = DateTime.Now;
        var callsAtStart = kernel.Calls;
        var steps = new List<AiTraceStep>();
        var ready = 0;
        var summary = "未完成";
        var success = false;

        // 历史成功路径：有就直接写进提示词，模型照抄比重新摸索稳
        var reuse = traces.FindReusable(goal);
        var conversation = new LlmConversation();
        conversation.AddUser(BuildFirstMessage(goal, reuse), null);

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
        var args = ParseArgs(call.ArgumentsJson);
        switch (call.Name)
        {
            case "look":
                return (false, false, await SnapshotAsync("当前画面", ct).ConfigureAwait(false));

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
                var ms = Math.Clamp(GetInt(args, "ms", 800), 200, 10000);
                await Task.Delay(ms, ct).ConfigureAwait(false);
                return (false, false, $"已等待 {ms} 毫秒。");
            }

            case "resin":
            {
                var snapshot = await ops.ReadResinAsync(ct).ConfigureAwait(false);
                return (false, false, snapshot == null
                    ? "读不到树脂：米游社未配置且画面顶栏不可见。"
                    : snapshot.ToString());
            }

            case "done":
            {
                var ok = GetBool(args, "success", true);
                var text = GetString(args, "summary", ok ? "已完成" : "做不到");
                return (true, ok, text);
            }

            default:
                return (false, false, $"未知工具 {call.Name}。");
        }
    }

    /// <summary>
    /// 抓一张当前画面，返回 OCR 文字清单给模型；开着视觉模式时附带截图。
    /// </summary>
    /// <param name="prefix">结果前缀，说明刚才做了什么。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<string> SnapshotAsync(string prefix, CancellationToken ct)
    {
        var builder = new StringBuilder(prefix);
        try
        {
            ops.EnsureForeground();
            using var screen = runtime.CaptureToRectArea();
            var lines = await Task.Run(() => ReadTexts(screen, screen.Width, screen.Height), ct).ConfigureAwait(false);

            builder.Append('\n');
            builder.Append("屏幕文字：");
            builder.Append(lines.Count == 0 ? "（没有识别到文字）" : string.Empty);
            foreach (var line in lines.Take(MaxOcrItems))
            {
                builder.Append("\n· ").Append(line.Text).Append(" @ (").Append(line.X).Append(", ").Append(line.Y).Append(')');
            }

            if (lines.Count > MaxOcrItems)
            {
                builder.Append("\n（还有 ").Append(lines.Count - MaxOcrItems).Append(" 条未列出）");
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
    /// OCR 全屏并按 1920×1080 逻辑坐标返回文字块，自上而下、自左而右排序。
    /// </summary>
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
    private string BuildFirstMessage(string goal, AiTraceEntry? reuse)
    {
        var builder = new StringBuilder();
        builder.Append("要做的事：").Append(goal).AppendLine();
        if (reuse != null)
        {
            builder.AppendLine("以前做成过类似的事，路径如下，可以参考但不要盲信（界面可能已经不一样）：");
            builder.AppendLine(reuse.ToPlaybook());
        }

        builder.AppendLine("先调用 look 看一眼当前画面，再决定点哪里。");
        return builder.ToString();
    }

    /// <summary>
    /// 系统提示词。
    /// </summary>
    private static string BuildSystemPrompt(string goal, int maxSteps)
    {
        return $$"""
                 你是原神自动化工具 BetterGI 的界面操作层，正在直接控制一台真实的游戏客户端。
                 目标：{{goal}}

                 坐标约定：所有坐标都按 1920×1080 给出，(0,0) 是画面左上角。不要输出其他分辨率。

                 行动规则：
                 1. 每轮只调用一个工具，看返回的文字与坐标再决定下一步。
                 2. 优先用 click_text 点文字，比猜坐标稳；文字要抄画面上原样的短语。
                 3. 打开界面用 key（map/bag/character/wish/party/menu），不要一步步点菜单。
                 4. 点完没反应先 wait 500–1500ms 再看，不要连点；连续两次同样动作没变化就换方法。
                 5. 弹窗、确认框、加载中都要先处理掉再继续，否则点不到底下的东西。
                 6. 做不到就如实 done(success=false)，不要用乱点来凑。
                 7. 最多 {{maxSteps}} 步，步数紧张时只做最有把握的那一下。
                 8. 不认识的界面先 look，靠画面文字判断自己在哪。
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
