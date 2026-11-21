using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
    /// 回传一张整个桌面的截图（含游戏窗口外的内容）。
    /// </summary>
    FullscreenScreenshot,

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
    /// 查询树脂。
    /// </summary>
    Resin,

    /// <summary>
    /// 查看背包：打开界面并回传截图。
    /// </summary>
    Inventory,

    /// <summary>
    /// 查看抽卡：打开祈愿界面并回传截图。
    /// </summary>
    Wish,

    /// <summary>
    /// 关闭游戏进程。
    /// </summary>
    CloseGame,

    /// <summary>
    /// 切换到队伍中的第 N 位角色。
    /// </summary>
    SwitchCharacter,

    /// <summary>
    /// 运行一个脚本或脚本配置组。
    /// </summary>
    Script,

    /// <summary>
    /// 查看/新增/启用/停用定时任务。
    /// </summary>
    Schedule,

    /// <summary>
    /// 打开登录界面并回传二维码，用于换号。
    /// </summary>
    Login,

    /// <summary>
    /// 扫码绑定米游社账号：不开游戏也能查树脂，顺带把 UID 记下来。
    /// </summary>
    BindMys,

    /// <summary>
    /// 跑一条地图追踪路线，通常用于采集素材。
    /// </summary>
    Path,

    /// <summary>
    /// 交给模型自由操作：没有对应任务时，由它自己看画面点。
    /// </summary>
    Pilot,

    /// <summary>
    /// 一句话里点了好几件事，按顺序串起来跑。
    /// </summary>
    Plan,

    /// <summary>
    /// 检查并下载特别版更新。由人发一句话触发，机器人只负责执行。
    /// </summary>
    Update,

    /// <summary>
    /// 应用已经下载好的更新并重启（重启期间机器人会短暂离线）。
    /// </summary>
    ApplyUpdate,

    /// <summary>
    /// 与下发任务无关的话，由大模型直接回答。
    /// </summary>
    Chat
}

/// <summary>
/// 计划中的一步。
/// </summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="TaskName">任务显示名。</param>
public sealed record RemoteTaskStep(string TaskId, string TaskName);

/// <summary>
/// 一条解析后的远程指令。
/// </summary>
/// <param name="Kind">意图。</param>
/// <param name="TaskId">要运行的独立任务标识，仅 <see cref="RemoteTaskCommandKind.RunTask"/> 有值。</param>
/// <param name="TaskName">任务显示名，用于回复文案。</param>
/// <param name="Say">大模型给出的确认语或回答，规则解析时为 null。</param>
/// <param name="Args">指令参数：角色序号、脚本名、定时规则、自由操作目标等。</param>
/// <param name="Steps">计划中的步骤，仅 <see cref="RemoteTaskCommandKind.Plan"/> 有值。</param>
public sealed record RemoteTaskCommand(
    RemoteTaskCommandKind Kind,
    string? TaskId = null,
    string? TaskName = null,
    string? Say = null,
    string? Args = null,
    IReadOnlyList<RemoteTaskStep>? Steps = null);

/// <summary>
/// 把聊天文本解析成远程指令。
/// <para>
/// 识别顺序：整句内置指令 → 带参数的指令前缀 → 任务标识 → 任务显示名 → 中文别名 →
/// 宽松内置指令。全部不命中时返回 <see cref="RemoteTaskCommandKind.Unknown"/>，由上层决定
/// 是让模型自由操作，还是回一句用法提示。
/// </para>
/// </summary>
public static class RemoteTaskCommandParser
{
    /// <summary>
    /// 常见叫法到任务标识的别名，命中后仍以注册表里的显示名为准。
    /// <para>
    /// 「体力」「树脂」这两个词不在这里：它们太容易被当成「我现在有多少体力」，
    /// 查询走 <see cref="RemoteTaskCommandKind.Resin"/>，清体力请说「清体力」「体力计划」。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["一条龙"] = "one_dragon",
        ["日常"] = "one_dragon",
        ["清体力"] = "resin_plan",
        ["体力计划"] = "resin_plan",
        ["刷体力"] = "resin_plan",
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
        ["幽静"] = "auto_stygian_onslaught",
        ["幽境"] = "auto_stygian_onslaught",
        ["危战"] = "auto_stygian_onslaught",
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
        // 原文保留标点与空格：时间（04:00）、脚本名、任务名之间的分隔符都靠它们区分。
        // 规整串（去掉标点空格）只用于关键词与任务名的精确比较。
        var raw = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        var normalized = Compact(raw);
        if (normalized.Length == 0)
        {
            return new RemoteTaskCommand(RemoteTaskCommandKind.Help);
        }

        // 1. 内置指令：整句匹配，避免「截图任务」这类带任务名的句子被误判。
        if (TryMatchBuiltin(normalized, exactOnly: true) is { } builtin)
        {
            return new RemoteTaskCommand(builtin);
        }

        // 2. 带参数的指令：前缀命中后，其余部分作为参数交给上层解释。
        if (TryMatchWithArgs(raw) is { } withArgs)
        {
            return withArgs;
        }

        // 3. 一串兑换码：没有「兑换码」三个字也认，「8HZH86K6E2PW」直接发过来就该去兑换。
        if (TryMatchRedeemCode(raw) is { } redeem)
        {
            return redeem;
        }

        // 4. 一件以上的事：先看是不是「A，然后 B」，是就串起来跑。
        if (ParsePlan(raw, tasks) is { Count: > 1 } steps)
        {
            return new RemoteTaskCommand(RemoteTaskCommandKind.Plan, Steps: steps);
        }

        // 4. 去掉运行前缀后再匹配任务名，支持「运行一条龙」「帮我跑个清体力」。
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
            return new RemoteTaskCommand(
                RemoteTaskCommandKind.RunTask, task.TaskId, task.DisplayName, Args: ExtractTaskArgs(task.TaskId, subject));
        }

        // 5. 「查看当前树脂」这类问句：不在整句表里，但意图很明确。
        if (TryMatchResinQuery(normalized))
        {
            return new RemoteTaskCommand(RemoteTaskCommandKind.Resin);
        }

        // 6. 任务名没命中时，再放宽成「以指令开头」，支持「截图看看」「停止任务」。
        if (TryMatchBuiltin(normalized, exactOnly: false) is { } loose)
        {
            return new RemoteTaskCommand(loose);
        }

        // 6.5 只报了一个素材名（「析晶褶伞」「清心」）：盘上有这条路线就当成采集。
        // 这一步不能省——交给模型解析时，模型看不到本地文件，只会回「没有这种任务」。
        if (TryMatchMaterialRoute(normalized, out var material))
        {
            return new RemoteTaskCommand(RemoteTaskCommandKind.Path, Args: material);
        }

        // 7. 都不认识：交给上层判断要不要让模型自己上手。
        return new RemoteTaskCommand(RemoteTaskCommandKind.Unknown, Say: text?.Trim());
    }

    /// <summary>
    /// 判断整句是不是一个「只有素材名」的采集请求。
    /// <para>
    /// 判据是本地路线索引里真的有这个东西，而不是猜词性：短句、且能对上素材名/路线名才算。
    /// 长度上限用来挡住「今天有什么好看的」这类长句被误判成采集。
    /// </para>
    /// </summary>
    /// <param name="text">已去掉空格的正文。</param>
    /// <param name="material">归一后的素材名，用作后续查路线的关键词。</param>
    private static bool TryMatchMaterialRoute(string text, out string material)
    {
        material = string.Empty;
        foreach (var clause in SplitClauses(text))
        {
            var candidate = StripMaterialNoise(clause);
            if (candidate.Length is 0 or > 12)
            {
                continue;
            }

            try
            {
                if (PathingRouteLibrary.Find(candidate).Count > 0)
                {
                    material = candidate;
                    return true;
                }
            }
            catch (Exception)
            {
                // 路线目录不可读时按普通句子处理，不让扫描异常冒到聊天里
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 剥掉「材料」「素材」这类修饰词，只留可能对得上路线目录的部分。
    /// <para>
    /// 「捡材料析晶褶伞」的动作词在指令表里已经吃掉，但「材料」两个字还挂在参数上，
    /// 直接拿去查路线会一条都匹配不到。
    /// </para>
    /// </summary>
    /// <param name="text">原始片段。</param>
    internal static string StripMaterialNoise(string text)
    {
        var cleaned = (text ?? string.Empty).Trim().Trim('「', '」', '"', '“', '”', ' ', '。', '，');
        cleaned = Regex.Replace(cleaned, "^(采集|采一下|采点|采|挖|收集|捡|拾取|找|刷|跑图采)", "");
        cleaned = Regex.Replace(cleaned, "(材料|素材|特产|东西|路线|脚本|拾取|采集)$", "");
        return cleaned.Trim();
    }

    /// <summary>
    /// 把一句话拆成多个任务，按顺序执行。
    /// <para>
    /// 只在拆出「两个以上不同的任务」时才返回结果：一句话里提到一个任务加一堆客套话，
    /// 仍然走单任务，不会被当成计划。同名任务只保留第一次出现的位置，避免「清体力然后再清一次体力」跑两遍。
    /// </para>
    /// </summary>
    /// <param name="text">已去掉空格的正文。</param>
    /// <param name="tasks">可用任务清单。</param>
    private static List<RemoteTaskStep>? ParsePlan(
        string text,
        IReadOnlyList<(string TaskId, string DisplayName)> tasks)
    {
        var segments = SplitClauses(text);
        if (segments.Count < 2)
        {
            return null;
        }

        var steps = new List<RemoteTaskStep>();
        foreach (var segment in segments)
        {
            var subject = StripConnector(segment);
            if (subject.Length == 0)
            {
                continue;
            }

            var matched = MatchTask(subject, tasks);
            if (matched is not { } task)
            {
                continue;
            }

            if (steps.Any(step => string.Equals(step.TaskId, task.TaskId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            steps.Add(new RemoteTaskStep(task.TaskId, task.DisplayName));
        }

        return steps.Count > 1 ? steps : null;
    }

    /// <summary>
    /// 按标点与连接词切句。标点先切，剩下含连接词的长句再切一次，
    /// 这样「合成树脂然后打幽静」这种没标点的口语也能拆开。
    /// </summary>
    private static List<string> SplitClauses(string text)
    {
        var byPunctuation = text.Split(['，', ',', '、', '；', ';', '。', '→', '>'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

        var result = new List<string>();
        foreach (var part in byPunctuation)
        {
            var pieces = ConnectorRegex.Split(part)
                .Select(piece => piece.Trim())
                .Where(piece => piece.Length > 0);
            result.AddRange(pieces);
        }

        return result;
    }

    /// <summary>
    /// 切句用的连接词。单独成句的「再」也算，所以「打完再领邮件」能拆开。
    /// </summary>
    private static readonly Regex ConnectorRegex =
        new("然后|接着|之后|随后|最后|顺便|再打|再去|再跑|然后再|再", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 去掉句首的客套前缀与运行前缀，让剩下的部分能被任务名匹配。
    /// <para>
    /// 只去掉「帮我/先/去」这类虚词和「运行/执行」这类动词，不去掉任务名本身可能用到的字——
    /// 别名匹配是包含式，「打幽静」照样能命中「幽静」，不需要把「打」削掉。
    /// </para>
    /// </summary>
    private static string StripConnector(string segment)
    {
        var trimmed = segment.TrimStart('帮', '我', '你', '给', '把', '先', '去', '就', '都', '那', '这');
        foreach (var prefix in RunPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > prefix.Length)
            {
                trimmed = trimmed[prefix.Length..];
                break;
            }
        }

        return trimmed.TrimStart(' ', '个', '一', '下', '：', ':');
    }

    /// <summary>
    /// 从句子尾巴上取出任务参数：目前只有一条龙需要，「一条龙 日常」里的「日常」就是配置名。
    /// </summary>
    private static string? ExtractTaskArgs(string taskId, string subject)
    {
        if (!string.Equals(taskId, "one_dragon", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var index = subject.IndexOf("一条龙", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var rest = subject[(index + "一条龙".Length)..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    /// <summary>
    /// 兑换码形态：8–20 位，只含字母数字，且至少一个数字。
    /// <para>
    /// 原神的兑换码形如 8HZH86K6E2PW。要求含数字是为了不把「hello」这种普通英文当兑换码；
    /// 允许原文里夹着「领取激活码」之类的前后缀，取最长的那段码。
    /// </para>
    /// </summary>
    private static RemoteTaskCommand? TryMatchRedeemCode(string text)
    {
        var best = string.Empty;
        foreach (Match match in CodeRegex.Matches(text))
        {
            var code = match.Value;
            // 纯英文单词（screenshot、dragon）不是兑换码，必须有数字
            if (!code.Any(char.IsAsciiDigit) || code.Length <= best.Length)
            {
                continue;
            }

            best = code;
        }

        return best.Length == 0
            ? null
            : new RemoteTaskCommand(RemoteTaskCommandKind.RunTask, "redeem_code", "兑换码", Args: best);
    }

    /// <summary>
    /// 兑换码正则：边界处不能紧挨字母数字，避免把长串切出一个子串。
    /// </summary>
    private static readonly Regex CodeRegex = new(
        @"(?<![A-Za-z0-9])[A-Za-z0-9]{8,20}(?![A-Za-z0-9])",
        RegexOptions.Compiled);

    /// <summary>
    /// 是否在问树脂有多少。
    /// <para>
    /// 只认「查询类动词 + 树脂/体力」的组合，所以「体力不够了帮我清一下」不会被当成查询，
    /// 而是继续往下走任务名匹配。
    /// </para>
    /// </summary>
    private static bool TryMatchResinQuery(string compact)
    {
        if (!compact.Contains("树脂", StringComparison.Ordinal) && !compact.Contains("体力", StringComparison.Ordinal))
        {
            return false;
        }

        string[] asks = ["查", "看", "多少", "还剩", "还有", "剩余", "剩多"];
        return asks.Any(ask => compact.Contains(ask, StringComparison.Ordinal));
    }

    /// <summary>
    /// 带参数的指令：前缀命中即返回，剩余文本作为参数。
    /// <para>
    /// 前缀按长度倒序匹配，「切换角色」不会被「切角色」抢先。
    /// </para>
    /// </summary>
    private static RemoteTaskCommand? TryMatchWithArgs(string text)
    {
        foreach (var (kind, prefixes) in ArgCommands)
        {
            foreach (var prefix in prefixes)
            {
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var args = text[prefix.Length..].Trim().TrimStart(' ', '到', '成', '：', ':', '第', '个')
                    .TrimEnd(' ', '个', '号', '。', '！', '!');
                return new RemoteTaskCommand(kind, Args: args);
            }
        }

        return null;
    }

    /// <summary>
    /// 需要参数的内置指令。
    /// </summary>
    private static readonly (RemoteTaskCommandKind Kind, string[] Prefixes)[] ArgCommands =
    [
        // 放在最前：多角色时用户要回「绑定米游社 2」，这里把序号当成参数交给上层
        (RemoteTaskCommandKind.BindMys, ["绑定米游社", "米游社登录"]),
        (RemoteTaskCommandKind.SwitchCharacter, ["切换角色", "切角色", "换角色", "切换人物", "切人", "换人", "切到", "换到"]),
        (RemoteTaskCommandKind.Script, ["运行脚本", "执行脚本", "跑脚本", "运行配置组"]),
        (RemoteTaskCommandKind.Schedule, ["定时任务", "计划任务", "定时"]),
        // 采集类是「跑盘上的路线」，不是「让模型在画面上点」：一条路线动辄几十个点，
        // 靠 look/click 一步点点不出来，而且模型根本不知道本地有哪些路线。
        (RemoteTaskCommandKind.Path, ["采集", "采一下", "采点", "采", "挖", "收集", "捡", "拾取",
            "找素材", "刷素材", "跑图采"]),
        // 自由操作只剩「明确要模型自己上手」的说法，避免把采集请求抢走
        (RemoteTaskCommandKind.Pilot, ["自由操作", "你自己操作", "你自己点", "看着点", "随便点", "你自己看着办"])
    ];

    /// <summary>
    /// 匹配内置指令。exactOnly 为 true 时要求整句相等，否则允许指令作为句子开头。
    /// </summary>
    private static RemoteTaskCommandKind? TryMatchBuiltin(string text, bool exactOnly)
    {
        var compact = Compact(text);

        // 「更新」这类词太短：允许作前缀就会把「更新脚本」抢成更新软件。
        // 只认整句，其余说法交给更具体的关键词或模型。
        if (exactOnly)
        {
            foreach (var (kind, keywords) in ExactOnlyKeywords)
            {
                if (keywords.Any(keyword => string.Equals(compact, keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    return kind;
                }
            }
        }

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
    /// 只认整句的关键词。整句匹配不会误伤「更新脚本」「升级武器」这类别的话。
    /// </summary>
    private static readonly (RemoteTaskCommandKind Kind, string[] Keywords)[] ExactOnlyKeywords =
    [
        (RemoteTaskCommandKind.Update, ["更新", "升级", "更新一下", "升级一下", "更新软件", "更新程序",
            "更新bgi", "更新bettergi", "更新特别版"]),
        // 第二步必须单独一句：重启会杀掉机器人自己，得让上一条回复先发出去
        (RemoteTaskCommandKind.ApplyUpdate, ["重启更新", "应用更新", "安装更新", "重启并更新", "重启以完成更新"])
    ];

    /// <summary>
    /// 内置指令关键词，按优先级排列；「任务」类放最后，避免抢占任务名。
    /// <para>
    /// 查询类指令只做整句匹配：一旦允许「以…开头」，用户说「体力不够了帮我清一下」就会被当成查询。
    /// </para>
    /// </summary>
    private static readonly (RemoteTaskCommandKind Kind, string[] Keywords)[] BuiltinKeywords =
    [
        (RemoteTaskCommandKind.Help, ["help", "帮助", "菜单", "指令", "用法", "你能做什么", "怎么用", "不会用", "有哪些命令",
            "有什么命令", "功能列表", "怎么玩"]),
        (RemoteTaskCommandKind.Status, ["status", "progress", "状态", "进度", "在跑吗", "在干嘛", "跑完了吗", "还要多久",
            "跑到哪了", "在跑什么", "怎么样了", "好了没"]),
        (RemoteTaskCommandKind.CloseGame, ["关游戏", "关闭游戏", "退出游戏", "关掉游戏", "把游戏关了", "关原神", "退出原神", "结束游戏"]),
        (RemoteTaskCommandKind.FullscreenScreenshot, ["全屏截图", "截全屏", "屏幕截图", "桌面截图", "全局截图", "整个屏幕"]),
        (RemoteTaskCommandKind.Resin, ["查体力", "看体力", "体力多少", "体力查询", "有多少体力", "还剩多少体力", "体力还剩多少",
            "体力满了没", "体力满了吗", "体力爆了没", "树脂满了吗", "树脂满了没",
            "查树脂", "看树脂", "树脂多少", "树脂查询", "有多少树脂", "体力", "树脂"]),
        (RemoteTaskCommandKind.Inventory, ["背包", "看背包", "查看背包", "打开背包", "包里有什么", "查看物品", "有多少摩拉"]),
        (RemoteTaskCommandKind.Wish, ["抽卡", "祈愿", "看抽卡", "抽卡记录", "祈愿记录"]),
        (RemoteTaskCommandKind.Login, ["切号", "换号", "换账号", "扫码登录", "登录二维码", "登录界面"]),
        // 更新只收带前缀的说法：单独一个「更新」交给 ExactOnlyKeywords，
        // 否则「更新脚本」会被这里的 StartsWith 抢走。
        (RemoteTaskCommandKind.Update, ["检查更新", "查更新", "更新软件", "更新程序", "更新bgi",
            "更新bettergi", "更新特别版", "升级软件", "有没有新版", "有新版本吗"]),
        // 「绑定米游社」只在这里出现一次就会和「扫码登录」撞车，所以写全一点，避免误抢换号指令
        (RemoteTaskCommandKind.BindMys, ["绑定米游社", "米游社绑定", "绑定mys", "米游社登录", "登录米游社",
            "绑定米游社账号", "扫码绑定米游社", "绑定一下米游社"]),
        (RemoteTaskCommandKind.Schedule, ["定时任务", "计划任务", "定时", "有哪些定时"]),
        (RemoteTaskCommandKind.Script, ["脚本列表", "有哪些脚本", "有什么脚本", "装了哪些脚本", "我的脚本"]),
        (RemoteTaskCommandKind.Screenshot, ["screenshot", "截图", "画面", "看看", "现在什么样", "游戏画面", "拍张照"]),
        (RemoteTaskCommandKind.Stop, ["stop", "cancel", "停止", "取消", "打断", "停", "别跑了", "先停下", "暂停一下"]),
        (RemoteTaskCommandKind.TaskList, ["tasks", "list", "任务列表", "任务", "列表", "能做什么", "有什么任务", "有啥任务",
            "能跑什么"])
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
    internal static string Compact(string text)
    {
        return new string(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && c != '！' && c != '？').ToArray());
    }
}
