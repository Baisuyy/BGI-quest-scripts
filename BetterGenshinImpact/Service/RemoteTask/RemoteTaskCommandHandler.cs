using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.GameTask.SoloTask.Execution;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 一条回复：文本必填，图片可选。
/// </summary>
/// <param name="Text">回复正文。</param>
/// <param name="Image">要一并回传的 JPEG 截图。</param>
/// <param name="RunId">本次指令启动的独立任务运行标识，未启动任务时为 null。</param>
public sealed record RemoteTaskReply(string Text, byte[]? Image = null, Guid? RunId = null);

/// <summary>
/// 把远程指令映射到游戏内操作：启动/停止独立任务、查树脂、开界面、关游戏、自由操作。
/// <para>
/// 能用任务解决的一律走任务（<see cref="SoloTaskExecutionService"/>，与界面上点「开始」同一条路），
/// 任务解决不了的才交给 <see cref="GuiPilot"/> 让模型自己点——这样确定性的活不依赖大模型，
/// 长尾的活也不会卡在「没有这个任务」上。
/// </para>
/// </summary>
public sealed class RemoteTaskCommandHandler(
    SoloTaskExecutionService executionService,
    SoloTaskRegistry registry,
    GameRuntimeService runtimeService,
    IConfigService configService,
    GameOps ops,
    AgentTasks agentTasks,
    AiKernel kernel,
    AiTraceStore traces,
    RemoteTaskPlanRunner planRunner,
    ILogger<RemoteTaskCommandHandler> logger)
{
    /// <summary>
    /// 可下发的任务清单：只保留可以在独立任务入口直接运行的任务。
    /// </summary>
    public IReadOnlyList<(string TaskId, string DisplayName)> Tasks { get; } = registry.All
        .Where(d => d.Scopes.HasFlag(SoloTaskScopes.Standalone))
        .Select(d => (d.TaskId, d.DisplayName))
        .ToList();

    /// <summary>
    /// 取任务的显示名，未注册时回退到任务标识。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    public string DisplayNameOf(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return "任务";
        }

        return registry.TryGet(taskId, out var descriptor) ? descriptor.DisplayName : taskId;
    }

    /// <summary>
    /// 处理一条远程指令并生成回复。
    /// </summary>
    /// <param name="message">已去除 @ 的入站消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回复内容。</returns>
    public async Task<RemoteTaskReply> ExecuteAsync(
        RemoteTaskCommand command,
        QqBotIncomingMessage message,
        CancellationToken cancellationToken)
    {
        return command.Kind switch
        {
            RemoteTaskCommandKind.Help => new RemoteTaskReply(command.Say ?? BuildHelp()),
            RemoteTaskCommandKind.TaskList => new RemoteTaskReply(
                command.Say is { Length: > 0 } say ? say + "\n\n" + BuildTaskList() : BuildTaskList()),
            RemoteTaskCommandKind.Status => new RemoteTaskReply(command.Say ?? BuildStatus()),
            RemoteTaskCommandKind.Screenshot => new RemoteTaskReply(
                command.Say ?? "当前画面如下。",
                await CaptureAsync(cancellationToken).ConfigureAwait(false)),
            RemoteTaskCommandKind.FullscreenScreenshot => await FullscreenAsync(command, cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.Stop => new RemoteTaskReply(await StopAsync().ConfigureAwait(false)),
            RemoteTaskCommandKind.RunTask => await StartAsync(
                    command.TaskId!, command.TaskName ?? command.TaskId!, command.Say, cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.Resin => await ResinAsync(command, cancellationToken).ConfigureAwait(false),
            RemoteTaskCommandKind.Inventory => await OpenPanelAsync(command, "bag", "背包", cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.Wish => await OpenPanelAsync(command, "wish", "祈愿", cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.CloseGame => await CloseGameAsync(command, cancellationToken).ConfigureAwait(false),
            RemoteTaskCommandKind.SwitchCharacter => await SwitchCharacterAsync(command, cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.Script => await ScriptAsync(command, cancellationToken).ConfigureAwait(false),
            RemoteTaskCommandKind.Schedule => new RemoteTaskReply(Schedule(command)),
            RemoteTaskCommandKind.Login => await LoginAsync(command, cancellationToken).ConfigureAwait(false),
            RemoteTaskCommandKind.Pilot => await PilotAsync(command.Args ?? message.Content, cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.Plan => StartPlan(command),
            RemoteTaskCommandKind.Chat => new RemoteTaskReply(command.Say ?? BuildHelp()),
            _ => await HandleUnknownAsync(command, message, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// 抓取当前画面并压成 JPEG。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="allowStartCapture">截图器未启动时是否尝试自动启动。</param>
    /// <returns>JPEG 字节；无法截图时返回 null。</returns>
    public async Task<byte[]?> CaptureAsync(CancellationToken cancellationToken, bool allowStartCapture = true)
    {
        var config = configService.Get().RemoteTaskConfig;
        try
        {
            var runtime = runtimeService.Current;
            if (runtime is null && allowStartCapture)
            {
                runtime = await StartRuntimeAsync(cancellationToken).ConfigureAwait(false);
            }

            if (runtime is null)
            {
                logger.LogInformation("远程截图失败：截图器未启动");
                return null;
            }

            using var frame = TaskControl.CaptureGameImage(runtime.Capture);
            return await Task.Run(() => EncodeJpeg(frame, config.ScreenshotMaxWidth, config.ScreenshotQuality), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "远程截图失败");
            return null;
        }
    }

    /// <summary>
    /// 启动一个独立任务。
    /// </summary>
    private async Task<RemoteTaskReply> StartAsync(
        string taskId,
        string taskName,
        string? say,
        CancellationToken cancellationToken)
    {
        var request = new SoloTaskStartRequest
        {
            RunId = Guid.NewGuid(),
            TaskId = taskId
        };
        var response = await executionService.StartAsync(request).ConfigureAwait(false);
        if (!response.Accepted)
        {
            return new RemoteTaskReply($"没能启动「{taskName}」：{response.ErrorMessage}");
        }

        logger.LogInformation("远程下发任务：{TaskId}（{RunId}）", taskId, request.RunId);
        var head = string.IsNullOrWhiteSpace(say) ? $"已开始「{taskName}」。" : say.Trim();
        return new RemoteTaskReply(
            $"{head}\n发送「状态」查看进度，发送「停止」可中断，跑完会自动回传截图。",
            RunId: request.RunId);
    }

    /// <summary>
    /// 停止当前任务。
    /// </summary>
    private async Task<string> StopAsync()
    {
        var status = executionService.GetStatus();
        if (status.RunId is not { } runId)
        {
            return "当前没有在跑的任务。";
        }

        var response = await executionService.StopAsync(new SoloTaskStopRequest { RunId = runId }).ConfigureAwait(false);
        return response.Accepted
            ? "已发送停止指令，任务正在收尾。"
            : $"停止失败：{response.ErrorMessage}";
    }

    /// <summary>
    /// 查树脂：先用米游社接口，没有就识别画面。
    /// </summary>
    private async Task<RemoteTaskReply> ResinAsync(RemoteTaskCommand command, CancellationToken cancellationToken)
    {
        var snapshot = await ops.ReadResinAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
        {
            return new RemoteTaskReply(
                $"{command.Say}\n读不到树脂。两个办法：\n" +
                "· 游戏开在主界面/战斗界面时再问我一次（我识别画面顶栏）\n" +
                "· 在设置里填米游社 Cookie 与原神 UID，不开游戏也能查");
        }

        var threshold = configService.Get().RemoteTaskConfig.ResinAlertThreshold;
        var hint = snapshot.Original >= threshold
            ? "\n已经攒到提醒线了，说「清体力」我就去打。"
            : string.Empty;
        var text = $"{command.Say}\n{snapshot}{hint}";
        return new RemoteTaskReply(text.Trim());
    }

    /// <summary>
    /// 打开一个界面并把截图发回去。
    /// </summary>
    private async Task<RemoteTaskReply> OpenPanelAsync(
        RemoteTaskCommand command,
        string panel,
        string label,
        CancellationToken cancellationToken)
    {
        if (IsBusy())
        {
            return new RemoteTaskReply($"有任务在跑，先「停止」再看{label}，否则会打断任务。");
        }

        var runtime = runtimeService.Current ?? await StartRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime == null)
        {
            return new RemoteTaskReply("游戏没开，或者截图器起不来，看不到" + label + "。");
        }

        if (!await ops.OpenPanelAsync(panel, cancellationToken).ConfigureAwait(false))
        {
            return new RemoteTaskReply($"打不开{label}。");
        }

        var image = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteTaskReply(
            $"{command.Say}\n已打开{label}，截图如下。要退出这个界面就按 esc（我这边不会替你关）。".Trim(),
            image);
    }

    /// <summary>
    /// 全局截图：抓整个桌面，含游戏窗口之外的内容。
    /// </summary>
    private async Task<RemoteTaskReply> FullscreenAsync(RemoteTaskCommand command, CancellationToken cancellationToken)
    {
        var config = configService.Get().RemoteTaskConfig;
        var image = await ops.CaptureDesktopAsync(config.ScreenshotMaxWidth, config.ScreenshotQuality, cancellationToken)
            .ConfigureAwait(false);
        return image == null
            ? new RemoteTaskReply("全屏截图失败，可能没有桌面访问权限。")
            : new RemoteTaskReply((command.Say ?? "整个屏幕如下。").Trim(), image);
    }

    /// <summary>
    /// 关闭游戏：先停任务，再结束进程。
    /// </summary>
    private async Task<RemoteTaskReply> CloseGameAsync(RemoteTaskCommand command, CancellationToken cancellationToken)
    {
        if (IsBusy())
        {
            await StopAsync().ConfigureAwait(false);
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        var closed = await ops.CloseGameAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("远程关闭游戏：{Result}", closed ? "成功" : "超时");
        return new RemoteTaskReply(closed
            ? $"{command.Say}\n游戏已关闭。".Trim()
            : "已经发了关闭指令，但进程还没退干净，可能卡在退出确认框上。");
    }

    /// <summary>
    /// 切换队伍中的第 N 位角色。
    /// </summary>
    private async Task<RemoteTaskReply> SwitchCharacterAsync(
        RemoteTaskCommand command,
        CancellationToken cancellationToken)
    {
        if (IsBusy())
        {
            return new RemoteTaskReply("有任务在跑，这时候切人会打乱战斗流程。");
        }

        var index = ParseIndex(command.Args);
        if (index == null)
        {
            return new RemoteTaskReply("要切到第几个角色？直接说「切人2」这样，队伍里第 1–5 位。");
        }

        var runtime = runtimeService.Current ?? await StartRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime == null)
        {
            return new RemoteTaskReply("游戏没开，切不了人。");
        }

        if (!await ops.SwitchCharacterAsync(index.Value, cancellationToken).ConfigureAwait(false))
        {
            return new RemoteTaskReply("切换失败。");
        }

        var image = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteTaskReply($"已切到第 {index} 位角色。", image);
    }

    /// <summary>
    /// 跑一个用户脚本，逻辑复用 <see cref="AgentTasks"/>，避免两套脚本查找规则。
    /// </summary>
    private async Task<RemoteTaskReply> ScriptAsync(RemoteTaskCommand command, CancellationToken cancellationToken)
    {
        var name = (command.Args ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            var list = await agentTasks.ExecuteAsync("list_scripts", new System.Text.Json.Nodes.JsonObject(), cancellationToken)
                .ConfigureAwait(false);
            return new RemoteTaskReply("要跑哪个脚本？\n\n" + list);
        }

        var args = new System.Text.Json.Nodes.JsonObject { ["name"] = name };
        var result = await agentTasks.ExecuteAsync("run_script", args, cancellationToken).ConfigureAwait(false);
        return new RemoteTaskReply(result);
    }

    /// <summary>
    /// 换号/扫码：把当前画面（通常是登录二维码）发回去。
    /// </summary>
    private async Task<RemoteTaskReply> LoginAsync(RemoteTaskCommand command, CancellationToken cancellationToken)
    {
        var runtime = runtimeService.Current;
        if (runtime == null)
        {
            return new RemoteTaskReply(
                "现在没有游戏画面。想换号的话：先发「关游戏」，再在电脑上启动原神，停在登录界面时发「切号」，我就把二维码截给你。");
        }

        var image = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteTaskReply(
            "这是当前画面。如果上面是登录二维码，用手机米游社扫一下就能换号；如果已经在游戏里，先「关游戏」再重新启动才会回到登录界面。",
            image);
    }

    /// <summary>
    /// 启动一条多步计划：一步跑完再跑下一步。
    /// </summary>
    private RemoteTaskReply StartPlan(RemoteTaskCommand command)
    {
        var steps = command.Steps;
        if (steps is not { Count: > 0 })
        {
            return new RemoteTaskReply("这条计划里没有能识别的任务。");
        }

        if (planRunner.IsRunning)
        {
            return new RemoteTaskReply("已经有一条计划在跑了，等它跑完或者先说「停止」。");
        }

        if (IsBusy())
        {
            return new RemoteTaskReply("有单个任务在跑，先「停止」再下发计划，免得抢任务锁。");
        }

        planRunner.Start(steps);
        var list = string.Join(" → ", steps.Select(step => step.TaskName));
        return new RemoteTaskReply(
            $"{command.Say}\n按顺序来：{list}\n共 {steps.Count} 步，每步跑完接着下一步，全部结束后我把汇总发给你。".Trim());
    }

    /// <summary>
    /// 交给模型自由操作：看画面、点按钮，直到做完或放弃。
    /// </summary>
    private async Task<RemoteTaskReply> PilotAsync(string goal, CancellationToken cancellationToken)
    {
        var config = configService.Get();
        if (!config.RemoteTaskConfig.SelfLearningEnabled)
        {
            return new RemoteTaskReply("这个事没有对应任务，而「自由操作」是关着的。\n\n" + BuildHelp());
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            return new RemoteTaskReply("要我在游戏里做什么？说具体点，比如「打开背包看看有多少摩拉」。");
        }

        var reason = kernel.UnavailableReason;
        if (reason != null)
        {
            return new RemoteTaskReply($"这个事没有现成任务，需要模型自己点，但{reason}。");
        }

        if (IsBusy())
        {
            return new RemoteTaskReply("有任务在跑，先「停止」再让我自由操作，不然两边会抢键鼠。");
        }

        var runtime = runtimeService.Current ?? await StartRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime == null)
        {
            return new RemoteTaskReply("游戏没开，或者截图器起不来，操作不了。");
        }

        logger.LogInformation("远程自由操作开始：{Goal}", goal);
        var pilot = new GuiPilot(runtime, config, kernel, traces, ops);
        var outcome = await pilot.RunAsync(goal, cancellationToken).ConfigureAwait(false);
        var image = await CaptureAsync(cancellationToken).ConfigureAwait(false);

        var head = outcome.Success ? "做到了。" : "没做成。";
        var text = $"{head}{outcome.Summary}\n（{outcome.Steps} 步，模型调用 {outcome.Calls} 次，轨迹已记到 log/ai-trace）";
        return new RemoteTaskReply(text, image);
    }

    /// <summary>
    /// 没认出来的话：回一句用法提示，并告诉用户怎么显式触发自由操作。
    /// <para>
    /// 这里刻意不自动开自由操作：闲聊、问知识、说错话都会落到这个分支，
    /// 一旦自动接管键鼠，人会看到鼠标自己乱点。要模型动手就明说「自由操作」。
    /// </para>
    /// </summary>
    private Task<RemoteTaskReply> HandleUnknownAsync(
        RemoteTaskCommand command,
        QqBotIncomingMessage message,
        CancellationToken cancellationToken)
    {
        var config = configService.Get();
        var text = $"没看懂「{message.Content}」。\n\n{BuildHelp()}";
        if (config.RemoteTaskConfig.SelfLearningEnabled && kernel.Available)
        {
            text += "\n\n如果这事确实要我在游戏里点，前面加「自由操作」再说一遍，我自己看着办。";
        }

        return Task.FromResult(new RemoteTaskReply(text));
    }

    /// <summary>
    /// 定时任务指令：查看、启用、停用、删除、新增。
    /// </summary>
    private string Schedule(RemoteTaskCommand command)
    {
        var config = configService.Get();
        var schedule = config.RemoteScheduleConfig;
        var args = (command.Args ?? string.Empty).Trim();
        if (args.Length == 0 || args is "列表" or "list" or "查看")
        {
            return BuildScheduleList(schedule);
        }

        // 两字词先判，否则「启用 3f2a」会被当成「启」+「用 3f2a」，规则 id 里带个「用」字
        foreach (var (prefix, enabled) in new[]
                 {
                     ("启用", true), ("打开", true), ("开启", true),
                     ("停用", false), ("关闭", false)
                 })
        {
            if (args.StartsWith(prefix, StringComparison.Ordinal))
            {
                return ToggleSchedule(schedule, args[prefix.Length..].Trim(), enabled);
            }
        }

        var head = args.Length >= 1 ? args[..1] : string.Empty;
        var rest = args.Length > 1 ? args[1..].Trim() : string.Empty;
        if (head is "开" or "启")
        {
            return ToggleSchedule(schedule, rest, true);
        }

        if (head is "关" or "停")
        {
            return ToggleSchedule(schedule, rest, false);
        }

        if (args.StartsWith("删", StringComparison.Ordinal))
        {
            return RemoveSchedule(schedule, rest);
        }

        return AddSchedule(schedule, args);
    }

    /// <summary>
    /// 列出全部规则。
    /// </summary>
    private static string BuildScheduleList(RemoteScheduleConfig schedule)
    {
        if (schedule.Rules.Count == 0)
        {
            return "还没有定时任务。可以这样说：\n" +
                   "· 定时每天 04:00 跑一条龙\n" +
                   "· 定时树脂满 160 清体力\n" +
                   "· 定时 列表 / 定时 开 3f2a1b0c / 定时 删 3f2a1b0c";
        }

        var builder = new StringBuilder("定时任务：\n");
        foreach (var rule in schedule.Rules)
        {
            var when = rule.Trigger == RemoteScheduleTrigger.Time
                ? $"每天 {rule.Time}"
                : $"树脂 ≥ {rule.ResinThreshold}";
            var action = rule.Action.StartsWith('@') ? rule.Action : "任务 " + rule.Action;
            var state = rule.Enabled ? "开" : "关";
            var last = rule.LastRunAt is { } time ? time.ToString("MM-dd HH:mm") : "从未";
            builder.Append(CultureInfo.InvariantCulture,
                $"[{state}] {rule.Id} · {when} → {action}{(string.IsNullOrEmpty(rule.ActionArgs) ? "" : " " + rule.ActionArgs)} · 上次 {last}\n");
        }

        builder.Append("（总开关：").Append(schedule.Enabled ? "开" : "关").Append("）");
        return builder.ToString();
    }

    /// <summary>
    /// 启用或停用一条规则。
    /// </summary>
    private string ToggleSchedule(RemoteScheduleConfig schedule, string id, bool enabled)
    {
        var rule = FindRule(schedule, id);
        if (rule == null)
        {
            return $"没找到规则「{id}」。发「定时 列表」看看现有规则。";
        }

        rule.Enabled = enabled;
        configService.Save();
        return $"已{(enabled ? "启用" : "停用")} {rule.Name}（{rule.Id}）。";
    }

    /// <summary>
    /// 删除一条规则。
    /// </summary>
    private string RemoveSchedule(RemoteScheduleConfig schedule, string id)
    {
        var rule = FindRule(schedule, id);
        if (rule == null)
        {
            return $"没找到规则「{id}」。";
        }

        schedule.Rules.Remove(rule);
        configService.Save();
        return $"已删除 {rule.Name}。";
    }

    /// <summary>
    /// 新增一条规则，支持两种句式：
    /// <c>每天 04:00 跑 一条龙</c> 与 <c>树脂满 160 清体力</c>。
    /// </summary>
    private string AddSchedule(RemoteScheduleConfig schedule, string text)
    {
        var rule = new RemoteScheduleRule();
        var body = text;

        if (body.StartsWith("每天", StringComparison.Ordinal) || body.StartsWith("每日", StringComparison.Ordinal))
        {
            body = body[2..].Trim();
            var time = ExtractTime(body);
            if (time == null)
            {
                return "时间没看懂。写成「定时每天 04:00 跑一条龙」这样。";
            }

            rule.Trigger = RemoteScheduleTrigger.Time;
            rule.Time = time;
            body = body.Replace(time, string.Empty, StringComparison.Ordinal).TrimStart("跑执行开始，, ".ToCharArray());
        }
        else if (body.StartsWith("树脂满", StringComparison.Ordinal) || body.StartsWith("树脂达到", StringComparison.Ordinal))
        {
            var digits = new string(body.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var threshold) || threshold is < 20 or > 200)
            {
                return "树脂阈值没看懂。写成「定时树脂满 160 清体力」这样。";
            }

            rule.Trigger = RemoteScheduleTrigger.Resin;
            rule.ResinThreshold = threshold;
            body = body[(body.IndexOf(digits, StringComparison.Ordinal) + digits.Length)..].TrimStart("跑执行，, ".ToCharArray());
        }
        else
        {
            return "定时规则只认两种写法：\n· 定时每天 04:00 跑一条龙\n· 定时树脂满 160 清体力";
        }

        var target = RemoteTaskCommandParser.Parse(body, Tasks);
        if (target.Kind is not (RemoteTaskCommandKind.RunTask or RemoteTaskCommandKind.Resin)
            && !body.StartsWith('@'))
        {
            return $"「{body}」没对上任何任务。发「任务」看看能跑什么。";
        }

        rule.Action = target.Kind == RemoteTaskCommandKind.RunTask ? target.TaskId! : "@" + body.TrimStart('@');
        rule.Name = rule.Trigger == RemoteScheduleTrigger.Time
            ? $"{rule.Time} {DisplayNameOf(rule.Action)}"
            : $"树脂{rule.ResinThreshold} {DisplayNameOf(rule.Action)}";
        schedule.Rules.Add(rule);
        configService.Save();
        logger.LogInformation("新增定时任务：{Id} {Trigger} {Action}", rule.Id, rule.Trigger, rule.Action);
        return $"已添加定时任务 {rule.Id}：{(rule.Trigger == RemoteScheduleTrigger.Time ? "每天 " + rule.Time : "树脂 ≥ " + rule.ResinThreshold)} → {rule.Action}。";
    }

    /// <summary>
    /// 从文本里取第一个 <c>HH:mm</c> 或 <c>H:mm</c>。
    /// </summary>
    private static string? ExtractTime(string text)
    {
        for (var i = 0; i + 3 < text.Length; i++)
        {
            if (!char.IsDigit(text[i]))
            {
                continue;
            }

            var colon = text.IndexOf(':', i);
            if (colon < 0 || colon == i || colon + 2 >= text.Length)
            {
                continue;
            }

            var hourText = text[i..colon];
            var minuteText = text[(colon + 1)..Math.Min(text.Length, colon + 3)];
            if (int.TryParse(hourText, out var hour) && int.TryParse(minuteText, out var minute)
                && hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
            {
                return $"{hour:D2}:{minute:D2}";
            }
        }

        return null;
    }

    /// <summary>
    /// 按标识或名称前缀找规则。
    /// </summary>
    private static RemoteScheduleRule? FindRule(RemoteScheduleConfig schedule, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return schedule.Rules.FirstOrDefault(rule =>
                   string.Equals(rule.Id, key, StringComparison.OrdinalIgnoreCase))
               ?? schedule.Rules.FirstOrDefault(rule =>
                   rule.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 生成状态描述。
    /// </summary>
    private string BuildStatus()
    {
        if (planRunner.IsRunning)
        {
            return planRunner.Describe();
        }

        var status = executionService.GetStatus();
        if (!status.IsActive)
        {
            return status.ErrorMessage is { Length: > 0 } error
                ? $"当前没有在跑的任务。\n上次结束原因：{error}"
                : "当前没有在跑的任务。";
        }

        var name = Tasks.FirstOrDefault(t => t.TaskId == status.TaskId).DisplayName ?? status.TaskId ?? "任务";
        var state = status.State switch
        {
            SoloTaskRunState.Starting => "正在启动",
            SoloTaskRunState.Running => "正在运行",
            SoloTaskRunState.Stopping => "正在停止",
            _ => "空闲"
        };
        return $"{state}：{name}";
    }

    /// <summary>
    /// 生成使用说明。
    /// </summary>
    private static string BuildHelp()
    {
        return """
               能做的事：
               · 跑任务：一条龙、清体力、秘境、打幽境危战、领邮件……
               · 查体力 —— 树脂还剩多少、多久回满
               · 包 / 抽卡 —— 打开界面截图给你
               · 截图 / 全屏截图
               · 切人2 —— 切到队伍第 2 位
               · 关游戏
               · 脚本 —— 列出或运行用户脚本
               · 定时 —— 定时任务管理
               · 状态 / 停止 / 任务
               没有专门任务的活（采集、翻某个界面、点某个按钮），说「自由操作」加要做什么，我自己看着画面点。
               换号：先「关游戏」，在电脑上重开原神，停在登录界面时发「切号」，我把二维码截给你。
               """;
    }

    /// <summary>
    /// 生成任务清单，按分组归并成紧凑文本。
    /// </summary>
    private string BuildTaskList()
    {
        if (Tasks.Count == 0)
        {
            return "当前版本没有可远程下发的任务。";
        }

        var lines = registry.All
            .Where(d => d.Scopes.HasFlag(SoloTaskScopes.Standalone))
            .GroupBy(d => d.Group)
            .Select(group => $"{group.Key}：{string.Join("、", group.Select(d => d.DisplayName))}");
        return "可下发的任务：\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 是否还有任务在跑。切人、开界面、自由操作之前都要问一次。
    /// </summary>
    private bool IsBusy() => executionService.GetStatus().IsActive || planRunner.IsRunning;

    /// <summary>
    /// 解析「切人」的位次参数，支持 2 / ２ / 二 / 二号 这几种写法。
    /// </summary>
    private static int? ParseIndex(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return null;
        }

        foreach (var c in args)
        {
            if (c is >= '1' and <= '5')
            {
                return c - '0';
            }

            if (c is >= '１' and <= '５')
            {
                return c - '０';
            }

            var chinese = c switch
            {
                '一' => 1,
                '二' => 2,
                '三' => 3,
                '四' => 4,
                '五' => 5,
                _ => 0
            };
            if (chinese > 0)
            {
                return chinese;
            }
        }

        return null;
    }

    /// <summary>
    /// 在 UI 线程上启动截图器；游戏未启动或窗口找不到时返回 null。
    /// </summary>
    private async Task<GameRuntime?> StartRuntimeAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("截图器未运行，尝试为远程操作启动截图器");
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return null;
            }

            return await dispatcher.InvokeAsync(
                () => runtimeService.StartAsync(cancellationToken),
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken).Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "启动截图器失败");
            return null;
        }
    }

    /// <summary>
    /// 缩放到目标宽度并编码为 JPEG。传 0 宽度时不缩放。
    /// </summary>
    private static byte[] EncodeJpeg(Mat frame, int maxWidth, int quality)
    {
        using var resized = Resize(frame, maxWidth);
        Cv2.ImEncode(".jpg", resized, out var buffer, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
        return buffer;
    }

    /// <summary>
    /// 按最长边限制等比缩放；已在范围内时返回副本，保证调用方总是可以释放结果。
    /// </summary>
    private static Mat Resize(Mat frame, int maxWidth)
    {
        var longest = Math.Max(frame.Cols, frame.Rows);
        if (maxWidth <= 0 || longest <= maxWidth)
        {
            return frame.Clone();
        }

        var scale = (double)maxWidth / longest;
        return frame.Resize(new Size(Math.Max(1, (int)Math.Round(frame.Cols * scale)), Math.Max(1, (int)Math.Round(frame.Rows * scale))));
    }
}
