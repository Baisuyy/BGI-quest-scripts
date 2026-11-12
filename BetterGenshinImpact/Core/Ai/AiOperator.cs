using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Plugins;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Runtime;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>一次接管的目标与边界。</summary>
public sealed record AiGoal(
    string Title,
    string MapName,
    string MatchMethod,
    double TargetX,
    double TargetY,
    string? ExtraContext = null,
    bool AllowTeleport = false,
    double ReachDistance = 4);

/// <summary>接管结果。<see cref="EndDistance"/> 为结束时距目标的距离。</summary>
public sealed record AiOperatorResult(
    bool Success,
    string Reason,
    int Steps,
    int Calls,
    double StartDistance,
    double EndDistance);

/// <summary>
/// 内置操作层：把 BGI 自己的输入、传送、截图、功能开关包装成工具，交给大模型一步一步调用。
/// <para>
/// 模型看不到代码，也不需要知道坐标怎么算；它只做「按一下什么、往哪走」这类玩家动作，
/// 感知全部来自 BGI 已有的地图匹配与 OCR。这就是「用 BGI 当媒介让 AI 间接操作」。
/// </para>
/// </summary>
public sealed class AiOperator(GameRuntime runtime, AllConfig config, AiKernel kernel, CancellationToken ct)
{
    private static readonly ILogger Logger = App.GetLogger<AiOperator>();
    private readonly GameStateProbe _probe = new(runtime);
    private readonly Dictionary<string, IDisposable> _featureLeases = [];

    /// <summary>本次接管的日志块号，块内每一步都带上它。</summary>
    private int _blockId;

    /// <summary>本次接管的起始时刻，用于块尾统计耗时。</summary>
    private DateTime _startedAt;

    /// <summary>模型可用的全部动作。</summary>
    private static readonly LlmTool[] Tools =
    [
        new("move", "朝一个方向移动一小段。卡住时优先用 backward/left/right 换角度，不要连续同向硬顶。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["direction"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("forward", "backward", "left", "right"),
                    },
                    ["ms"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "持续毫秒，100–2000，默认 600",
                    },
                },
                ["required"] = new JsonArray("direction"),
            }),
        new("turn", "横向转动镜头。dx 为正向右转、为负向左转，范围 -600–600 像素。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["dx"] = new JsonObject { ["type"] = "integer", ["description"] = "鼠标横向位移，-600–600" },
                },
                ["required"] = new JsonArray("dx"),
            }),
        new("jump", "跳跃。用于跨越矮障碍或脱离卡住的地形。", EmptySchema()),
        new("attack", "普通攻击。可打碎挡路的物件或触发交互。", EmptySchema()),
        new("interact", "交互/拾取（F 键）。靠近机关、宝箱、NPC 时使用。", EmptySchema()),
        new("quest", "重新读左上角任务面板，返回任务标题、当前步骤、目标动作与计数进度。对话/交付/开启之后可以调用，确认任务是否已推进。", EmptySchema()),
        new("tp", "传送到接管目标点。只在距离超过 80 米且允许传送时使用。", EmptySchema()),
        new("toggle_feature", "开关 BGI 自带功能（自动剧情/自动拾取/自动吃药），只在明确需要时使用。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["feature"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("auto_skip", "auto_pick", "auto_eat"),
                    },
                    ["enabled"] = new JsonObject { ["type"] = "boolean" },
                },
                ["required"] = new JsonArray("feature", "enabled"),
            }),
        new("finish", "结束接管。到达目标、或确认无法到达时调用。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["success"] = new JsonObject { ["type"] = "boolean" },
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "一句话说明" },
                },
                ["required"] = new JsonArray("success", "reason"),
            }),
    ];

    private static JsonObject EmptySchema() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    /// <summary>
    /// 执行一次接管。整个过程最多 <see cref="AiConfig.RescueMaxSteps"/> 步、<see cref="AiConfig.RescueTimeoutSeconds"/> 秒。
    /// </summary>
    public async Task<AiOperatorResult> RunAsync(AiGoal goal, string? trigger = null)
    {
        var settings = config.AiConfig;
        var callsAtStart = kernel.Calls;
        var maxSteps = Math.Clamp(settings.RescueMaxSteps, 1, 20);
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(settings.RescueTimeoutSeconds, 10, 300));

        using var screen = runtime.CaptureToRectArea();
        var start = _probe.Read(screen, goal.MapName, goal.MatchMethod, goal.TargetX, goal.TargetY);
        var startDistance = start.Distance ?? double.NaN;
        // 任务面板只在开始时读一次；接管是短平快的脱困动作，不必每步都付 OCR 的 100–300ms
        var quest = settings.ReadQuestProgress ? _probe.ReadQuest() : QuestProgress.Empty;
        var image = settings.VisionMode ? _probe.EncodeScreenshot(screen) : null;

        _startedAt = DateTime.UtcNow;
        _blockId = AiLog.Begin(trigger ?? "跑图卡死接管", goal, start, settings, kernel.Calls);
        if (!quest.IsEmpty)
        {
            AiLog.Note(_blockId, "任务 " + quest.ToPromptText());
        }
        else if (settings.ReadQuestProgress)
        {
            AiLog.Note(_blockId, "任务面板为空（未追踪任务，或面板被遮挡）");
        }

        if (settings.VisionMode)
        {
            AiLog.Note(_blockId, "视觉模式已开启，每步附一张压缩截图");
        }

        var conversation = new LlmConversation();
        conversation.AddUser(BuildFirstUserMessage(goal, start, quest), image);

        // 接管期间所有按键先释放，避免上一段逻辑残留的 W 键干扰
        InputHub.Foreground.ReleaseAll();

        var lastState = start;
        var lastActionKey = "";
        var repeated = 0;
        var steps = 0;

        try
        {
            while (steps < maxSteps && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                LlmReply reply;
                try
                {
                    reply = await kernel.SendAsync("卡死救援", conversation, Tools, BuildSystemPrompt(goal, maxSteps), ct);
                }
                catch (AiUnavailableException e)
                {
                    return Result(false, e.Message, steps, startDistance, lastState, kernel.Calls - callsAtStart);
                }

                conversation.AddAssistant(reply);

                if (!reply.WantsTool)
                {
                    // 模型只说话不动手：催一次；连续两次就结束，别把步数浪费在解释上
                    repeated++;
                    AiLog.Say(_blockId, reply.Text);
                    if (repeated >= 2)
                    {
                        return Result(false, "模型未执行任何动作", steps, startDistance, lastState, kernel.Calls - callsAtStart);
                    }

                    conversation.AddUser("不要解释，直接调用一个工具。到达或无法前进时调用 finish。");
                    continue;
                }

                repeated = 0;
                var call = reply.Calls[0];
                steps++;

                var previousDistance = lastState.Distance;
                var execution = await ExecuteAsync(call, goal, lastState, steps, maxSteps);
                var delta = execution.State?.Distance is double now && previousDistance is double before
                    ? now - before
                    : (double?)null;
                AiLog.Step(_blockId, steps, maxSteps, DescribeTool(call), delta);

                if (execution.Finished)
                {
                    return Result(execution.Success, execution.Content, steps, startDistance, lastState, kernel.Calls - callsAtStart);
                }

                // 任务节点（交互）之后、以及每 3 步刷新一次任务面板：
                // 步骤文字或计数变了，说明这条路真的在推进，比坐标变化更能说明「走对了」。
                // quest 工具本身刚读过，不再重复 OCR。
                if (settings.ReadQuestProgress && call.Name != "quest"
                    && (call.Name == "interact" || steps % 3 == 0))
                {
                    var refreshed = _probe.ReadQuest();
                    if (refreshed.AdvancedOver(quest))
                    {
                        AiLog.Quest(_blockId, quest, refreshed);
                        quest = refreshed;
                        execution = execution with
                        {
                            Content = execution.Content + "\n任务进度：" + quest.ToPromptText(),
                        };
                    }
                    else if (!refreshed.IsEmpty)
                    {
                        quest = refreshed;
                    }
                }

                // 连续两次一模一样的动作视为无效，提醒模型换策略
                var actionKey = call.Name + call.ArgumentsJson;
                if (actionKey == lastActionKey)
                {
                    conversation.AddToolResult(call.Id,
                        execution.Content + "\n提示：这个动作刚做过，距离没有明显变化，换一个方向。");
                }
                else
                {
                    conversation.AddToolResult(call.Id, execution.Content);
                }

                lastActionKey = actionKey;

                if (execution.State != null)
                {
                    lastState = execution.State;
                    if (lastState.Distance < goal.ReachDistance)
                    {
                        return Result(true, "已到达目标点", steps, startDistance, lastState, kernel.Calls - callsAtStart);
                    }
                }

                await Task.Delay(150, ct);
            }

            return Result((lastState.Distance ?? double.MaxValue) < goal.ReachDistance,
                DateTime.UtcNow >= deadline ? "接管超时" : "步数用尽", steps, startDistance, lastState,
                kernel.Calls - callsAtStart);
        }
        finally
        {
            InputHub.Foreground.ReleaseAll();
            foreach (var lease in _featureLeases.Values)
            {
                lease.Dispose();
            }

            _featureLeases.Clear();
        }
    }

    private AiOperatorResult Result(bool success, string reason, int steps, double startDistance,
        GameState state, int calls)
    {
        var end = state.Distance ?? double.NaN;
        AiLog.End(_blockId, success, reason, steps, calls, DateTime.UtcNow - _startedAt, startDistance, state.Distance);
        return new AiOperatorResult(success, reason, steps, calls, startDistance, end);
    }

    /// <summary>把一次工具调用写成一行日志，参数只保留决策相关的部分。</summary>
    private static string DescribeTool(LlmToolCall call)
    {
        var args = ParseArgs(call.ArgumentsJson);
        return call.Name switch
        {
            "move" => $"move {GetString(args, "direction", "forward")} {Math.Clamp(GetInt(args, "ms", 600), 100, 2000)}ms",
            "turn" => $"turn {GetInt(args, "dx", 0):+#;-#;0}",
            "quest" => "quest（读任务面板）",
            "toggle_feature" => $"toggle {GetString(args, "feature", "?")}={(GetBool(args, "enabled", true) ? "on" : "off")}",
            "finish" => $"finish({(GetBool(args, "success", false) ? "success" : "fail")})",
            _ => call.Name,
        };
    }

    private static string BuildSystemPrompt(AiGoal goal, int maxSteps)
    {
        return $"""
                你是原神自动化工具 BetterGI 的内置操作层，正在直接控制一个真实角色。
                目标：{goal.Title}

                行动规则：
                1. 每轮只调用一个工具，看返回的状态再决定下一步。
                2. 卡住（距离不变）通常是被地形、墙、空气墙挡住：先 backward 200–500ms，再 left/right 换个角度前进，或 jump 越过矮障碍。
                3. 不要连续两次用完全相同的动作；同一方向超过两次没进展就立刻换方向。
                4. 距离小于 {goal.ReachDistance:F0} 米即视为到达，调用 finish(success=true)。
                5. 最多 {maxSteps} 步，超时会强制交还；步数紧张时优先做最有把握的动作。
                6. 任务面板给了当前任务与步骤：如果目标点就在任务目标附近，任务进度变化（步骤文字或计数）说明走对了；连续几步任务进度不动，说明这条路只是绕过路障、与任务无关，按距离继续走即可。
                7. 拿不准任务是否推进时调用 quest 重新读面板，不要连续两次以上调用。
                8. 禁止调用未提供的工具，禁止输出代码或解释长段落。
                """;
    }

    private string BuildFirstUserMessage(AiGoal goal, GameState state, QuestProgress quest)
    {
        var lines = new List<string>
        {
            "当前状态：" + state.ToPromptText(),
            "目标点：" + $"({goal.TargetX:F1}, {goal.TargetY:F1})，地图 {goal.MapName}",
        };
        if (!quest.IsEmpty)
        {
            lines.Add("任务面板：" + quest.ToPromptText());
            if (quest.Distance.HasValue && quest.Distance.Value > 0)
            {
                lines.Add("任务面板显示目标距离 " + $"{quest.Distance.Value:F0} 米；如果与上面的目标点距离差得远，说明当前目标是任务里的另一段，先按目标点走。");
            }
        }

        if (!string.IsNullOrWhiteSpace(goal.ExtraContext))
        {
            lines.Add("补充信息：" + goal.ExtraContext);
        }

        if (goal.AllowTeleport)
        {
            lines.Add("允许传送：距离超过 80 米可以直接 tp。");
        }
        else
        {
            lines.Add("不允许传送：只能走位。");
        }

        lines.Add("请开始，先调用一个工具。");
        return string.Join("\n", lines);
    }

    private sealed record ExecutionResult(bool Finished, bool Success, string Content, GameState? State);

    private async Task<ExecutionResult> ExecuteAsync(LlmToolCall call, AiGoal goal, GameState state, int step, int maxSteps)
    {
        var args = ParseArgs(call.ArgumentsJson);
        var budget = $"（第 {step}/{maxSteps} 步）";

        switch (call.Name)
        {
            case "move":
            {
                var direction = GetString(args, "direction", "forward");
                var ms = Math.Clamp(GetInt(args, "ms", 600), 100, 2000);
                await MoveAsync(direction, ms);
                var next = await ObserveAsync(goal, $"{direction} {ms}ms");
                return new ExecutionResult(false, false, next.ToJson() + budget, next);
            }
            case "turn":
            {
                var dx = Math.Clamp(GetInt(args, "dx", 0), -600, 600);
                InputHub.Foreground.Mouse.MoveMouseBy(dx, 0);
                await Task.Delay(220, ct);
                var next = await ObserveAsync(goal, $"转视角 {dx}px");
                return new ExecutionResult(false, false, next.ToJson() + budget, next);
            }
            case "jump":
            {
                InputHub.Foreground.SimulateAction(GIActions.Jump, KeyType.KeyPress);
                await Task.Delay(500, ct);
                var next = await ObserveAsync(goal, "跳跃");
                return new ExecutionResult(false, false, next.ToJson() + budget, next);
            }
            case "attack":
            {
                InputHub.Foreground.SimulateAction(GIActions.NormalAttack, KeyType.KeyPress);
                await Task.Delay(600, ct);
                var next = await ObserveAsync(goal, "普攻");
                return new ExecutionResult(false, false, next.ToJson() + budget, next);
            }
            case "interact":
            {
                InputHub.Foreground.SimulateAction(GIActions.PickUpOrInteract, KeyType.KeyPress);
                await Task.Delay(600, ct);
                var next = await ObserveAsync(goal, "交互");
                return new ExecutionResult(false, false, next.ToJson() + budget, next);
            }
            case "quest":
            {
                var progress = _probe.ReadQuest();
                return new ExecutionResult(false, false, progress.ToJson() + budget, state);
            }
            case "tp":
            {
                if (!goal.AllowTeleport)
                {
                    return new ExecutionResult(false, false, "不允许传送，请改用走位。" + budget, state);
                }

                var distance = state.Distance ?? 0;
                if (distance < 80)
                {
                    return new ExecutionResult(false, false,
                        $"距离只有 {distance:F0} 米，不必传送，请走位。" + budget, state);
                }

                try
                {
                    await Pluggable.Get<ITpTaskFactory>().Create(runtime, config, ct).Tp(goal.TargetX, goal.TargetY);
                    await Task.Delay(1500, ct);
                    var next = await ObserveAsync(goal, "已传送");
                    return new ExecutionResult(false, false, next.ToJson() + budget, next);
                }
                catch (Exception e)
                {
                    return new ExecutionResult(false, false, "传送失败：" + e.Message + budget, state);
                }
            }
            case "toggle_feature":
            {
                var feature = GetString(args, "feature", "");
                var enabled = GetBool(args, "enabled", true);
                var message = ToggleFeature(feature, enabled);
                var next = await ObserveAsync(goal, message);
                return new ExecutionResult(false, false, next.ToJson() + "；" + message + budget, next);
            }
            case "finish":
            {
                var success = GetBool(args, "success", true);
                var reason = GetString(args, "reason", success ? "到达目标" : "无法到达");
                return new ExecutionResult(true, success, reason, state);
            }
            default:
                return new ExecutionResult(false, false, $"未知工具 {call.Name}，可用：move/turn/jump/attack/interact/quest/tp/toggle_feature/finish", state);
        }
    }

    private async Task<GameState> ObserveAsync(AiGoal goal, string note)
    {
        try
        {
            using var screen = runtime.CaptureToRectArea();
            var state = _probe.Read(screen, goal.MapName, goal.MatchMethod, goal.TargetX, goal.TargetY, note);
            if (state.X == 0 && state.Y == 0)
            {
                // 地图匹配偶发失败：保留上一次读到的坐标，别让模型以为瞬移了
                return state with { Note = note + "；坐标识别失败，沿用上次位置" };
            }

            return state;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "读取状态失败");
            return new GameState(goal.MapName, 0, 0, goal.TargetX, goal.TargetY, null, "未知", note);
        }
    }

    private async Task MoveAsync(string direction, int ms)
    {
        var action = direction switch
        {
            "forward" => GIActions.MoveForward,
            "backward" => GIActions.MoveBackward,
            "left" => GIActions.MoveLeft,
            "right" => GIActions.MoveRight,
            _ => GIActions.MoveForward,
        };

        InputHub.Foreground.SimulateAction(action, KeyType.KeyDown);
        await Task.Delay(ms, ct);
        InputHub.Foreground.SimulateAction(action, KeyType.KeyUp);
        await Task.Delay(180, ct);
    }

    private string ToggleFeature(string feature, bool enabled)
    {
        var dispatcher = TaskTriggerDispatcher.InstanceNullable();
        if (dispatcher == null)
        {
            return "后台调度器未启动，无法切换功能";
        }

        var name = feature.ToLowerInvariant() switch
        {
            "auto_skip" => "AutoSkip",
            "auto_pick" => "AutoPick",
            "auto_eat" => "AutoEat",
            _ => null,
        };
        if (name == null)
        {
            return $"不支持的功能 {feature}";
        }

        if (enabled)
        {
            if (!_featureLeases.ContainsKey(name))
            {
                var lease = dispatcher.AddTrigger(name);
                if (lease != null)
                {
                    _featureLeases[name] = lease;
                }
            }

            return $"已开启 {name}";
        }

        if (_featureLeases.Remove(name, out var existing))
        {
            existing.Dispose();
        }

        return $"已关闭 {name}";
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
}
