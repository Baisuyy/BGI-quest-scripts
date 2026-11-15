using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 远程指令的意图。
/// </summary>
public enum RemoteTaskCommandKind
{
    /// <summary>
    /// 无法识别。
    /// </summary>
    Unknown,

    /// <summary>
    /// 使用说明。
    /// </summary>
    Help,

    /// <summary>
    /// 查询当前运行状态。
    /// </summary>
    Status,

    /// <summary>
    /// 回传一张当前画面。
    /// </summary>
    Screenshot,

    /// <summary>
    /// 停止当前任务。
    /// </summary>
    Stop,

    /// <summary>
    /// 列出可下发的任务。
    /// </summary>
    TaskList,

    /// <summary>
    /// 运行指定任务。
    /// </summary>
    RunTask,

    /// <summary>
    /// 与下发任务无关的话，由大模型直接回答。
    /// </summary>
    Chat
}

/// <summary>
/// 一条解析后的远程指令。
/// </summary>
/// <param name="Kind">意图。</param>
/// <param name="TaskId">要运行的独立任务标识，仅 <see cref="RemoteTaskCommandKind.RunTask"/> 有值。</param>
/// <param name="TaskName">任务显示名，用于回复文案。</param>
/// <param name="Say">大模型给出的确认语或回答，规则解析时为 null。</param>
public sealed record RemoteTaskCommand(
    RemoteTaskCommandKind Kind,
    string? TaskId = null,
    string? TaskName = null,
    string? Say = null);

/// <summary>
/// 把聊天文本解析成远程指令。
/// <para>
/// 识别顺序：内置指令关键词 → 任务标识 → 任务显示名 → 中文别名。全部不命中时返回
/// <see cref="RemoteTaskCommandKind.Unknown"/>，由上层回复用法提示。
/// </para>
/// </summary>
public static class RemoteTaskCommandParser
{
    /// <summary>
    /// 常见叫法到任务标识的别名，命中后仍以注册表里的显示名为准。
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["一条龙"] = "one_dragon",
        ["日常"] = "one_dragon",
        ["清体力"] = "resin_plan",
        ["体力"] = "resin_plan",
        ["树脂"] = "resin_plan",
        ["邮件"] = "claim_mail",
        ["领邮件"] = "claim_mail",
        ["每日奖励"] = "claim_daily_rewards",
        ["每日委托"] = "claim_daily_rewards",
        ["纪行"] = "claim_battle_pass",
        ["历练点"] = "claim_encounter_points",
        ["尘歌壶"] = "serenitea_pot_rewards",
        ["合成树脂"] = "craft_condensed_resin",
        ["浓缩树脂"] = "craft_condensed_resin",
        ["秘境"] = "auto_domain",
        ["副本"] = "auto_domain",
        ["首领"] = "auto_boss",
        ["周本"] = "auto_boss",
        ["地脉"] = "auto_ley_line_outcrop",
        ["地脉花"] = "auto_ley_line_outcrop",
        ["钓鱼"] = "auto_fishing",
        ["伐木"] = "auto_wood",
        ["烹饪"] = "auto_cook",
        ["七圣召唤"] = "auto_genius_invokation",
        ["幽境危战"] = "auto_stygian_onslaught",
        ["兑换码"] = "redeem_code",
        ["分解圣遗物"] = "auto_artifact_salvage",
        ["返回主界面"] = "return_main_ui"
    };

    /// <summary>
    /// 运行指令的前缀，去掉后剩余部分才参与任务名匹配。
    /// </summary>
    private static readonly string[] RunPrefixes = ["运行", "执行", "开始", "启动", "跑", "run", "start"];

    /// <summary>
    /// 解析一条消息文本。
    /// </summary>
    /// <param name="text">已去掉 @ 与首尾空白的正文。</param>
    /// <param name="tasks">可用任务清单（标识 + 显示名）。</param>
    /// <returns>解析结果，无法识别时 <see cref="RemoteTaskCommandKind.Unknown"/>。</returns>
    public static RemoteTaskCommand Parse(string? text, IReadOnlyList<(string TaskId, string DisplayName)> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return new RemoteTaskCommand(RemoteTaskCommandKind.Help);
        }

        // 1. 内置指令：整句匹配，避免「截图任务」这类带任务名的句子被误判。
        if (TryMatchBuiltin(normalized, exactOnly: true) is { } builtin)
        {
            return new RemoteTaskCommand(builtin);
        }

        // 2. 去掉运行前缀后再匹配任务名，支持「运行一条龙」「帮我跑个清体力」。
        var subject = normalized;
        foreach (var prefix in RunPrefixes)
        {
            if (subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && subject.Length > prefix.Length)
            {
                subject = subject[prefix.Length..].TrimStart(' ', '个', '一', '下', '：', ':');
                break;
            }
        }

        var matched = MatchTask(subject, tasks) ?? MatchTask(normalized, tasks);
        if (matched is { } task)
        {
            return new RemoteTaskCommand(RemoteTaskCommandKind.RunTask, task.TaskId, task.DisplayName);
        }

        // 3. 任务名没命中时，再放宽成「以指令开头」，支持「截图看看」「停止任务」。
        return TryMatchBuiltin(normalized, exactOnly: false) is { } loose
            ? new RemoteTaskCommand(loose)
            : new RemoteTaskCommand(RemoteTaskCommandKind.Unknown);
    }

    /// <summary>
    /// 匹配内置指令。exactOnly 为 true 时要求整句相等，否则允许指令作为句子开头。
    /// </summary>
    private static RemoteTaskCommandKind? TryMatchBuiltin(string text, bool exactOnly)
    {
        var compact = Compact(text);
        foreach (var (kind, keywords) in BuiltinKeywords)
        {
            foreach (var keyword in keywords)
            {
                var hit = exactOnly
                    ? string.Equals(compact, keyword, StringComparison.OrdinalIgnoreCase)
                    : compact.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    return kind;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 内置指令关键词，按优先级排列；「任务」类放最后，避免抢占任务名。
    /// </summary>
    private static readonly (RemoteTaskCommandKind Kind, string[] Keywords)[] BuiltinKeywords =
    [
        (RemoteTaskCommandKind.Help, ["help", "帮助", "菜单", "指令", "用法"]),
        (RemoteTaskCommandKind.Status, ["status", "progress", "状态", "进度", "在跑吗", "在干嘛"]),
        (RemoteTaskCommandKind.Screenshot, ["screenshot", "截图", "画面", "看看"]),
        (RemoteTaskCommandKind.Stop, ["stop", "cancel", "停止", "取消", "打断", "停"]),
        (RemoteTaskCommandKind.TaskList, ["tasks", "list", "任务列表", "任务", "列表", "能做什么"])
    ];

    /// <summary>
    /// 在任务清单里找到匹配项：标识精确 → 显示名精确 → 别名 → 显示名包含。
    /// </summary>
    private static (string TaskId, string DisplayName)? MatchTask(
        string subject,
        IReadOnlyList<(string TaskId, string DisplayName)> tasks)
    {
        if (subject.Length == 0)
        {
            return null;
        }

        foreach (var task in tasks)
        {
            // 任务标识含下划线，比较时忽略分隔符，兼容 one_dragon / onedragon 两种写法。
            if (string.Equals(Compact(task.TaskId), Compact(subject), StringComparison.OrdinalIgnoreCase))
            {
                return task;
            }
        }

        foreach (var task in tasks)
        {
            if (string.Equals(Compact(task.DisplayName), Compact(subject), StringComparison.OrdinalIgnoreCase))
            {
                return task;
            }
        }

        var compact = Compact(subject);

        // 别名允许出现在句子里，例如「帮我跑个秘境」，取命中的最长别名。
        var aliasHit = Aliases
            .Where(alias => compact.Contains(alias.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(alias => alias.Key.Length)
            .Select(alias => alias.Value)
            .FirstOrDefault();
        if (aliasHit is not null)
        {
            foreach (var task in tasks)
            {
                if (string.Equals(task.TaskId, aliasHit, StringComparison.OrdinalIgnoreCase))
                {
                    return task;
                }
            }
        }

        // 包含匹配放在最后，且要求至少两个字，避免单字误命中。
        if (compact.Length < 2)
        {
            return null;
        }

        foreach (var task in tasks)
        {
            var name = Compact(task.DisplayName);
            if (name.Contains(compact, StringComparison.OrdinalIgnoreCase)
                || compact.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return task;
            }
        }

        return null;
    }

    /// <summary>
    /// 去掉空白与常见标点，便于「清 体力」「一条龙！」这类输入命中。
    /// </summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Compact(text.Replace('\r', ' ').Replace('\n', ' '));
    }

    /// <summary>
    /// 只保留文字与数字，去掉空白和标点。
    /// </summary>
    private static string Compact(string text)
    {
        return new string(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && c != '！' && c != '？').ToArray());
    }
}
