using System;
using System.Linq;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 内置 AI 的日志块。
/// <para>
/// 一次接管输出一组以 <c>┌ │ └</c> 起头的行：块头写触发原因、起点状态与预算，
/// 块内每步一行，块尾写结果与距离变化。每条都是独立日志，遮罩窗口不会被多行消息挤乱，
/// 复制到文本或用 <c>grep "[AI]"</c> 筛选时仍然是一整块。
/// </para>
/// </summary>
internal sealed class AiLog
{
    private const string Tag = "[AI]";

    /// <summary>单行上限，避免任务面板文字把一行撑到几百字。</summary>
    private const int MaxLine = 220;

    private static readonly ILogger Logger = App.GetLogger<AiLog>();

    private static int _sequence;

    private AiLog()
    {
    }

    /// <summary>开一个块，返回块号。后续步骤与结束都用这个号。</summary>
    public static int Begin(string trigger, AiGoal goal, GameState start, AiConfig settings, int usedCalls)
    {
        var id = Interlocked.Increment(ref _sequence);

        Warn("┌ 接管 #{0} · {1}", id, trigger);
        Info("│ 位置 {0} · 距目标 {1} · {2}",
            start.X == 0 && start.Y == 0 ? "未知（地图匹配失败）" : $"({start.X:F1}, {start.Y:F1})",
            start.Distance.HasValue ? $"{start.Distance:F1}m" : "未知",
            start.Motion);
        Info("│ 目标 {0} · 预算 {1} 步 / {2}s · 本次已用 {3}/{4} · 模型 {5}",
            $"({goal.TargetX:F1}, {goal.TargetY:F1})",
            settings.RescueMaxSteps, settings.RescueTimeoutSeconds,
            usedCalls, settings.MaxCallsPerRun,
            string.IsNullOrWhiteSpace(settings.ModelName) ? "未填写" : settings.ModelName);

        return id;
    }

    /// <summary>块内补充一行（感知信息、异常、提醒）。</summary>
    public static void Note(int id, string text) => Info("│ {0}", Truncate(text));

    /// <summary>块内一步：动作、距离变化。</summary>
    /// <param name="delta">与上一步相比的距离变化，负数为拉近；null 表示这一步没有新坐标。</param>
    public static void Step(int id, int step, int max, string action, double? delta)
    {
        var suffix = delta switch
        {
            null => "",
            < -0.2 => $" → 拉近 {-delta:F1}m",
            > 0.2 => $" → 后退 {delta:F1}m",
            _ => " → 无进展",
        };
        Info("│ [{0}/{1}] {2}{3}", step, max, Truncate(action, 120), suffix);
    }

    /// <summary>块内一行说明模型的原话，模型不调工具时用得到。</summary>
    public static void Say(int id, string text) => Info("│ 模型：{0}", Truncate(text, 160));

    /// <summary>任务面板推进：步骤文字变了，或计数变大。</summary>
    public static void Quest(int id, QuestProgress from, QuestProgress to)
    {
        string line;
        if (!string.Equals(from.Step, to.Step, StringComparison.Ordinal))
        {
            line = $"{Short(from.Step)} → {Short(to.Step)}";
        }
        else if (from.Current.HasValue && to.Current.HasValue)
        {
            line = $"计数 {from.Current}/{from.Total} → {to.Current}/{to.Total}";
        }
        else
        {
            line = Short(to.Step);
        }

        Info("│ 任务推进 · {0}", Truncate(line, 160));
    }

    private static string Short(string text)
        => string.IsNullOrWhiteSpace(text) ? "（空）" : text;

    /// <summary>收块。</summary>
    public static void End(int id, bool success, string reason, int steps, int calls,
        TimeSpan elapsed, double startDistance, double? endDistance)
    {
        var route = double.IsNaN(startDistance)
            ? "起点未知"
            : $"{startDistance:F1} → {(endDistance.HasValue ? $"{endDistance.Value:F1}" : "?")}m";

        Info("└ {0} · {1} · {2} 步 / {3} 请求 / {4:F1}s · {5}",
            success ? "成功" : "失败", Truncate(reason, 80), steps, calls, elapsed.TotalSeconds, route);
    }

    /// <summary>块外的单行记录，用于脚本 <c>ai.Chat()</c> 这类一次性请求。</summary>
    public static void Once(string scene, int latencyMs, string reply)
        => Info("· {0} · {1}ms · {2}", scene, latencyMs, Truncate(reply, 160));

    /// <summary>块外的单行失败。</summary>
    public static void Fail(string scene, string error)
        => Warn("· {0} 失败 · {1}", scene, Truncate(error, 200));

    /// <summary>开一个本地助手块。</summary>
    public static int BeginAgent(string task, string sandboxRoot, AiConfig settings, int usedCalls)
    {
        var id = Interlocked.Increment(ref _sequence);
        Warn("┌ 本地助手 #{0} · {1}", id, Truncate(task, 80));
        Info("│ 沙箱 {0}", sandboxRoot);
        Info("│ 预算 {0} 步 / {1}s · 本次已用 {2}/{3} · 模型 {4}",
            settings.AgentMaxSteps, settings.AgentTimeoutSeconds,
            usedCalls, settings.MaxCallsPerRun,
            string.IsNullOrWhiteSpace(settings.ModelName) ? "未填写" : settings.ModelName);
        return id;
    }

    /// <summary>本地助手一步：动作与结果首行。</summary>
    public static void AgentStep(int id, int step, int max, string action, string result)
    {
        var firstLine = (result ?? "").Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        Info("│ [{0}/{1}] {2} → {3}", step, max, Truncate(action, 110), Truncate(firstLine, 90));
    }

    /// <summary>收本地助手块。</summary>
    public static void AgentEnd(int id, bool success, string summary, int steps, int calls, TimeSpan elapsed)
        => Info("└ {0} · {1} · {2} 步 / {3} 请求 / {4:F1}s",
            success ? "完成" : "未完成", Truncate(summary, 80), steps, calls, elapsed.TotalSeconds);

    /// <summary>授权判定：沙箱内/外的操作，用户允许还是拒绝。</summary>
    public static void Consent(string action, string target, bool inside, bool allowed)
        => Info("│ 授权 {0} · {1} {2} · {3}", action, inside ? "沙箱内" : "沙箱外",
            allowed ? "允许" : "拒绝", Truncate(target, 120));

    private static void Info(string format, params object?[] args)
        => Logger.LogInformation(Tag + " " + format, args);

    private static void Warn(string format, params object?[] args)
        => Logger.LogWarning(Tag + " " + format, args);

    private static string Truncate(string? text, int max = MaxLine)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = text.ReplaceLineEndings(" ");
        return text.Length <= max ? text : text[..max] + "…";
    }
}
