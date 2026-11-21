using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Mys;
using BetterGenshinImpact.Core.Special;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.GameTask.SoloTask.Execution;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 一条回复：文本必填，图片可选。
/// </summary>
/// <param name="Text">回复正文。</param>
/// <param name="Image">要一并回传的 JPEG 截图。</param>
/// <param name="RunId">本次指令启动的独立任务运行标识，未启动任务时为 null。</param>
/// <param name="RestartAfterSend">发送这条回复后保存配置并退出进程，让更新脚本接手替换文件。</param>
public sealed record RemoteTaskReply(
    string Text,
    byte[]? Image = null,
    Guid? RunId = null,
    bool RestartAfterSend = false);

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
    GenshinWikiClient wiki,
    MysBindingService mysBinding,
    IHttpClientFactory httpClientFactory,
    ILogger<RemoteTaskCommandHandler> logger)
{
    /// <summary>
    /// 兑换码任务的标识，参数需要单独拼。
    /// </summary>
    private const string RedeemCodeTaskId = "redeem_code";

    /// <summary>
    /// 一条龙任务的标识，启动前要先确认配置存在。
    /// </summary>
    private const string OneDragonTaskId = "one_dragon";

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
    /// <param name="onRunStarted">
    /// 任务真正开始前回调一次，交给调用方登记回传目标。
    /// <para>
    /// 必须在 StartAsync 之前回调：像兑换码这种任务几十毫秒就失败退出，
    /// 等 ExecuteAsync 返回再登记，结束事件早就发过了，用户什么也收不到。
    /// </para>
    /// </param>
    public async Task<RemoteTaskReply> ExecuteAsync(
        RemoteTaskCommand command,
        QqBotIncomingMessage message,
        CancellationToken cancellationToken,
        Action<Guid>? onRunStarted = null)
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
                    command.TaskId!, command.TaskName ?? command.TaskId!, command.Say, command.Args,
                    onRunStarted, cancellationToken)
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
            RemoteTaskCommandKind.Update => await UpdateAsync(command).ConfigureAwait(false),
            RemoteTaskCommandKind.ApplyUpdate => ApplyUpdate(command),
            RemoteTaskCommandKind.BindMys => await BindMysAsync(command, message, cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.Path => await PathingAsync(command, onRunStarted, cancellationToken)
                .ConfigureAwait(false),
            RemoteTaskCommandKind.Pilot => await PilotAsync(
                    command.Args ?? message.Content, onRunStarted, cancellationToken)
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
        string? args,
        Action<Guid>? onRunStarted,
        CancellationToken cancellationToken)
    {
        // 带参数的任务先把参数备好；备不出来就直接回原因，不要启动一个必然失败的任务
        var built = PrepareParameters(taskId, args);

        // 模型给的可能是角色名（「米提亚」），而盘上只有采集物名；翻一次再试，
        // 免得把「本地没有这条路线」直接甩给用户。
        if (built.BlockedReason is { Length: > 0 }
            && string.Equals(taskId, AutoPathingPathTask.TaskId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args)
            && kernel.Available)
        {
            var material = await TranslateMaterialAsync(args, cancellationToken).ConfigureAwait(false);
            if (material.Length > 0 && !string.Equals(material, args, StringComparison.Ordinal))
            {
                var retried = PrepareParameters(taskId, material);
                if (retried.BlockedReason is null)
                {
                    logger.LogInformation("「{Args}」按「{Material}」找到路线", args, material);
                    built = retried;
                }
            }
        }

        if (built.BlockedReason is { Length: > 0 } reason)
        {
            return new RemoteTaskReply($"没能启动「{taskName}」：{reason}");
        }

        var request = new SoloTaskStartRequest
        {
            RunId = Guid.NewGuid(),
            TaskId = taskId,
            Parameters = built.Parameters
        };
        // 先登记再启动，否则瞬间失败的任务其结束事件会在登记之前发出
        onRunStarted?.Invoke(request.RunId);
        var response = await executionService.StartAsync(request).ConfigureAwait(false);
        if (!response.Accepted)
        {
            return new RemoteTaskReply($"没能启动「{taskName}」：{response.ErrorMessage}");
        }

        logger.LogInformation("远程下发任务：{TaskId}（{RunId}）", taskId, request.RunId);

        // 记住「用户说法 → 实际采集物」，下次同一个说法不用再问模型
        if (string.Equals(taskId, AutoPathingPathTask.TaskId, StringComparison.OrdinalIgnoreCase)
            && built.Parameters?["file"]?.Value<string>() is { Length: > 0 } file
            && PathingRouteLibrary.All.FirstOrDefault(r => r.Relative == file) is { } started)
        {
            MaterialAliasStore.Remember(args, started.Material);
        }

        var head = string.IsNullOrWhiteSpace(say) ? $"已开始「{taskName}」。" : say.Trim();
        if (built.Note is { Length: > 0 } note)
        {
            head = $"{head}\n{note}";
        }

        return new RemoteTaskReply(
            $"{head}\n发送「状态」查看进度，发送「停止」可中断，跑完会把结果发回来，出问题才附画面。",
            RunId: request.RunId);
    }

    /// <summary>
    /// 组装启动参数的结果。
    /// </summary>
    /// <param name="Parameters">要传给任务的参数；为空时使用任务默认值。</param>
    /// <param name="Note">需要额外告知用户的话，比如自动换了配置。</param>
    /// <param name="BlockedReason">非空表示不该启动，内容是要回复的原因。</param>
    private sealed record PreparedParameters(JObject? Parameters, string? Note, string? BlockedReason);

    /// <summary>
    /// 按任务组装启动参数。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="args">聊天里解析出的参数，可能是兑换码或一条龙配置名。</param>
    private PreparedParameters PrepareParameters(string taskId, string? args)
    {
        var text = (args ?? string.Empty).Trim();

        if (string.Equals(taskId, RedeemCodeTaskId, StringComparison.OrdinalIgnoreCase))
        {
            var code = ExtractRedeemCode(text);
            if (code is null)
            {
                return new PreparedParameters(null, null, "没看到兑换码。直接把码发过来就行，例如 8HZH86K6E2PW。");
            }

            // 参数按 SoloTaskJson 的 SnakeCaseLower 命名策略落键，写成 Codes 会反序列化不到
            var payload = new JObject { ["codes"] = new JArray(code) };
            return new PreparedParameters(payload, null, null);
        }

        if (string.Equals(taskId, OneDragonTaskId, StringComparison.OrdinalIgnoreCase))
        {
            return EnsureOneDragonConfig(text);
        }

        if (string.Equals(taskId, AutoPathingPathTask.TaskId, StringComparison.OrdinalIgnoreCase))
        {
            // 这里收到的是素材名，要换成本地实际存在的路线文件
            var routes = PathingRouteLibrary.Find(text);
            if (routes.Count == 0)
            {
                return new PreparedParameters(null, null,
                    string.IsNullOrWhiteSpace(text)
                        ? "没说采什么。例如「采清心」「采胡桃」。"
                        : $"本地没有「{text}」的路线。{BuildPathingHelp()}");
            }

            return new PreparedParameters(
                new JObject { ["file"] = routes[0].Relative },
                routes.Count > 1 ? $"这个素材有 {routes.Count} 条路线，先跑「{routes[0].Name}」。" : null,
                null);
        }

        return new PreparedParameters(null, null, null);
    }

    /// <summary>
    /// 从文本里挑出兑换码：最长的一段 8–20 位字母数字，且含数字。
    /// </summary>
    private static string? ExtractRedeemCode(string text)
    {
        string? best = null;
        foreach (Match match in Regex.Matches(text, "[A-Za-z0-9]{8,20}"))
        {
            var code = match.Value;
            if (!code.Any(char.IsAsciiDigit) || (best is not null && code.Length <= best.Length))
            {
                continue;
            }

            best = code;
        }

        return best;
    }

    /// <summary>
    /// 一条龙要先有配置才能跑。返回 null 表示配置可用；否则返回给用户的说明。
    /// <para>
    /// 配置名默认取「当前选中的一条龙配置」，但全新安装时这个名字可能指向一个不存在的文件，
    /// 于是直接报「未找到配置」。这里改成先看盘上到底有哪些配置：只有一个就直接用，
    /// 一个都没有就叫用户去界面建，别让一句「没找到」把路堵死。
    /// </para>
    /// </summary>
    private PreparedParameters EnsureOneDragonConfig(string requestedName)
    {
        var config = configService.Get();
        var name = string.IsNullOrWhiteSpace(requestedName) ? config.SelectedOneDragonFlowConfigName : requestedName;
        if (OneDragonExists(name))
        {
            return new PreparedParameters(null, null, null);
        }

        var available = ListOneDragonConfigs();
        if (available.Count == 0)
        {
            return new PreparedParameters(null, null,
                "还没有一条龙配置。先到 BGI 界面 →「一条龙」→ 新建一个配置，再让我跑。");
        }

        if (available.Count == 1)
        {
            // 只有一个配置时没得选，直接用它；顺手把它设成当前选中的，界面上也一致
            var only = available[0];
            config.SelectedOneDragonFlowConfigName = only;
            configService.Save();
            return new PreparedParameters(
                new JObject { ["config_name"] = only },
                $"当前选中的配置「{name}」不存在，已改用唯一可用的「{only}」。",
                null);
        }

        return new PreparedParameters(null, null,
            $"「{name}」这个配置不存在。现有的配置有：{string.Join("、", available)}。说「一条龙 配置名」就能指定。");
    }

    /// <summary>
    /// 一条龙配置文件是否存在。
    /// </summary>
    private static bool OneDragonExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var path = OneDragonFlowConfigStore.GetFilePath(name);
        return File.Exists(path);
    }

    /// <summary>
    /// 列出磁盘上已有的一条龙配置名。
    /// </summary>
    private static List<string> ListOneDragonConfigs()
    {
        try
        {
            var folder = OneDragonFlowConfigStore.Folder;
            if (!Directory.Exists(folder))
            {
                return [];
            }

            return Directory.EnumerateFiles(folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
            var reason = ops.LastResinError is { Length: > 0 } detail ? $"\n原因：{detail}" : string.Empty;
            return new RemoteTaskReply(
                $"{command.Say}\n读不到树脂。{reason}\n\n两个办法：\n" +
                "· 私聊发「绑定米游社」扫一次码，之后不开游戏也能查\n"
                + "· 或者把游戏开在主界面/战斗界面再问我一次（我识别画面顶栏）");
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
    /// 检查并下载特别版更新。人发一句话触发，机器人只负责执行。
    /// <para>
    /// 不在这里直接重启：重启会把机器人自己一起关掉，这条回复还没发出去用户就断线了。
    /// 所以拆成两句话——这一句只报结果，用户回「重启更新」再动手。
    /// </para>
    /// </summary>
    private async Task<RemoteTaskReply> UpdateAsync(RemoteTaskCommand command)
    {
        string Body(string text) => string.IsNullOrWhiteSpace(command.Say) ? text : $"{command.Say}\n{text}";

        var check = await SpecialUpdater.CheckAsync(httpClientFactory, logger).ConfigureAwait(false);
        if (!check.Reachable || check.LatestVersion is null)
        {
            return new RemoteTaskReply(Body(check.Message));
        }

        // 没有增量包时只说清楚原因，不替用户打开浏览器：机器人看不见那边的屏幕
        if (check.Patch is null)
        {
            return new RemoteTaskReply(Body(
                check.Message + "\n\n" +
                "只能下载完整包覆盖安装（不会动 User 目录）：\n" +
                $"https://github.com/{SpecialUpdater.Repo}/releases\n\n" +
                "装好之后再发「更新」，以后就能走增量。"));
        }

        logger.LogInformation("远程更新：开始下载 {Version} 的增量包", check.LatestVersion);
        var (ok, message) = await SpecialUpdater.DownloadPatchAsync(check.Patch, check.LatestVersion, logger)
            .ConfigureAwait(false);
        if (!ok)
        {
            return new RemoteTaskReply(Body(
                $"下载或校验没通过：{message}\n\n" +
                "可以直接下完整包覆盖安装：\n" +
                $"https://github.com/{SpecialUpdater.Repo}/releases"));
        }

        return new RemoteTaskReply(Body(
            $"{check.Message}\n{message}\n\n" +
            "文件已经备好，回「重启更新」就开始替换。重启期间我会离线十几秒，不用管我。"));
    }

    /// <summary>
    /// 应用已经备好的更新：启动独立脚本、退出进程，由脚本在进程结束后替换文件并拉起新版。
    /// </summary>
    private RemoteTaskReply ApplyUpdate(RemoteTaskCommand command)
    {
        if (!SpecialPatch.IsPending)
        {
            return new RemoteTaskReply(
                "还没有下载好的更新。先发一句「更新」，等我下完、校验过，再发「重启更新」。");
        }

        var pending = SpecialPatch.ReadPending();
        if (!SpecialPatch.StartApplyAndRestart(logger))
        {
            return new RemoteTaskReply(
                "没能启动更新脚本，多半被安全软件拦了。\n" +
                "更新文件已经解压在 update\\staging，可以手动覆盖，或者直接下完整包。");
        }

        string range = pending is null ? string.Empty : $"（{pending.From} → {pending.To}）";
        return new RemoteTaskReply(
            $"开始替换文件并重启{range}。\n" +
            "重启期间机器人会离线十几秒，之后自己回来，不用管。",
            RestartAfterSend: true);
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
    /// 扫码绑定米游社：第一次发二维码，扫完再发一次就落库。
    /// <para>
    /// 只在私聊里做：二维码谁扫到都算数，发在群里等于把账号递给别人；
    /// 而且登录态是全局配置，群里的第二个人再绑一次会把前一个人的覆盖掉。
    /// </para>
    /// </summary>
    private async Task<RemoteTaskReply> BindMysAsync(
        RemoteTaskCommand command,
        QqBotIncomingMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Scope != QqBotMessageScope.C2C)
        {
            return new RemoteTaskReply(
                "绑定米游社要在私聊里做——二维码谁扫到都算数，发在群里就等于把账号交给别人。\n私聊我发一句「绑定米游社」就行。");
        }

        var key = message.SenderOpenId;
        if (ParseIndex(command.Args) is { } index)
        {
            // 多角色时回「绑定米游社 2」：这一步只是选角色，不再去轮询
            return new RemoteTaskReply(mysBinding.TrySelectRole(key, index, out var chosen)
                ? chosen
                : "现在没有待选的角色。先发「绑定米游社」扫码，扫完我再把角色列出来。");
        }

        if (mysBinding.HasPending(key))
        {
            var outcome = await mysBinding.PollAsync(key, cancellationToken).ConfigureAwait(false);
            return new RemoteTaskReply(outcome.Text);
        }

        try
        {
            var start = await mysBinding.StartAsync(key, cancellationToken).ConfigureAwait(false);
            return new RemoteTaskReply(start.Text, start.QrImage);
        }
        catch (MysLoginException e)
        {
            logger.LogWarning("米游社扫码登录发起失败：{Message}", e.Message);
            return new RemoteTaskReply($"米游社扫码没能发起：{e.Message}\n过一会儿再发「绑定米游社」试试。");
        }
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
    /// 跑一条地图追踪路线采集素材。
    /// <para>
    /// 「采清心」直接用素材名找路线；「采胡桃突破材料」这种说法先用模型翻成素材名再找。
    /// 找不到路线时不做自由操作——采集靠路线走，让模型临场找路只会更难走完。
    /// </para>
    /// </summary>
    private async Task<RemoteTaskReply> PathingAsync(
        RemoteTaskCommand command,
        Action<Guid>? onRunStarted,
        CancellationToken cancellationToken)
    {
        var goal = (command.Args ?? string.Empty).Trim();
        if (goal.Length == 0)
        {
            return new RemoteTaskReply(BuildPathingHelp());
        }

        return await TryStartPathingAsync(goal, command.Say, onRunStarted, cancellationToken).ConfigureAwait(false)
               ?? new RemoteTaskReply(BuildPathingHelp(goal));
    }

    /// <summary>
    /// 盘上真有这条路线就把它跑起来；没有则返回 null，由调用方决定怎么兜底。
    /// <para>
    /// 抽出来是因为「自由操作」也需要先过一遍：采集请求经常被说成「你自己去捡一下」，
    /// 而模型看不到本地文件，让它临场找路只会把步数烧在乱点上。
    /// </para>
    /// </summary>
    /// <param name="goal">素材名、角色名或路线名。</param>
    /// <param name="say">要放在回复开头的话术，可为空。</param>
    /// <param name="onRunStarted">任务被受理后回调，用于登记待回传，避免秒失败的任务漏回。</param>
    /// <returns>已启动的回复；没找到路线时返回 null。</returns>
    private async Task<RemoteTaskReply?> TryStartPathingAsync(
        string goal,
        string? say,
        Action<Guid>? onRunStarted,
        CancellationToken cancellationToken)
    {
        var raw = (goal ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        // 「捡材料析晶褶伞」这类句子，动作词已被指令表吃掉，剩下的「材料」会挡住目录匹配
        var text = RemoteTaskCommandParser.StripMaterialNoise(raw);
        if (text.Length == 0)
        {
            text = raw;
        }

        var note = string.Empty;
        var routes = PathingRouteLibrary.Find(text);

        // 1. 记过的说法：本地经验最快，也不花模型调用
        if (routes.Count == 0 && MaterialAliasStore.TryGet(text) is { Length: > 0 } remembered)
        {
            routes = PathingRouteLibrary.Find(remembered);
            if (routes.Count > 0)
            {
                note = $"「{text}」按「{remembered}」找路线。";
            }
        }

        // 2. 角色名：先查图鉴拿真实突破材料，再对本地路线。
        //    模型不认识新角色，问它只会得到一个编出来的名字，还得再校验一轮。
        if (routes.Count == 0)
        {
            foreach (var material in await wiki
                         .GetAscensionMaterialsAsync(text, cancellationToken)
                         .ConfigureAwait(false))
            {
                var hit = PathingRouteLibrary.Find(material);
                if (hit.Count == 0)
                {
                    continue;
                }

                routes = hit;
                note = $"「{text}」的突破材料是「{material}」。";
                break;
            }
        }

        // 3. 图鉴也没查到（多半是这条路线上没有的采集物），最后让模型从本地清单里挑
        if (routes.Count == 0 && kernel.Available)
        {
            if (await TranslateMaterialAsync(raw, cancellationToken).ConfigureAwait(false) is { Length: > 0 } material)
            {
                routes = PathingRouteLibrary.Find(material);
                if (routes.Count > 0)
                {
                    note = $"「{text}」按「{material}」找路线。";
                }
            }
        }

        if (routes.Count == 0)
        {
            return null;
        }

        var route = routes[0];
        var request = new SoloTaskStartRequest
        {
            RunId = Guid.NewGuid(),
            TaskId = AutoPathingPathTask.TaskId,
            Parameters = new JObject { ["file"] = route.Relative }
        };

        onRunStarted?.Invoke(request.RunId);
        var response = await executionService.StartAsync(request).ConfigureAwait(false);
        if (!response.Accepted)
        {
            return new RemoteTaskReply($"没能开始采集：{response.ErrorMessage}");
        }

        logger.LogInformation("远程采集：{Material} → {Route}（{RunId}）", route.Material, route.Relative, request.RunId);
        MaterialAliasStore.Remember(raw, route.Material);

        var head = string.IsNullOrWhiteSpace(say) ? $"开始采「{route.Material}」。" : say.Trim();
        if (note.Length > 0)
        {
            head = $"{head}\n{note}";
        }

        if (routes.Count > 1)
        {
            head += $"\n这个素材有 {routes.Count} 条路线，先跑「{route.Name}」。";
        }

        return new RemoteTaskReply($"{head}\n跑完会把结果和截图发回来，发「停止」可以中断。", RunId: request.RunId);
    }

    /// <summary>
    /// 把「胡桃突破材料」翻成「霓裳花」这类素材名。模型不可用或答非所问时返回空串。
    /// </summary>
    private async Task<string> TranslateMaterialAsync(string goal, CancellationToken cancellationToken)
    {
        // 盘上一条路线都没有时，问模型也凑不出可执行的东西，白花一次调用
        var materials = PathingRouteLibrary.Materials();
        if (materials.Count == 0)
        {
            return string.Empty;
        }

        // 把本地清单塞进提示词：模型不知道你装了什么路线，不告诉它就只能瞎猜
        var list = string.Join("、", materials.Take(80));
        var prompt = $"""
                      你是原神素材名转换器。用户想采集某种东西，但它可能是角色名、角色突破材料或模糊说法。
                      请从下面的「本地已有路线的采集物」里，挑出这次真正要去采的那一种，只输出它的名字。

                      本地已有路线的采集物：
                      {list}

                      规则：
                      - 只能从上面的清单里选，清单之外的名字一律不要输出。
                      - 一个都对不上就输出「不确定」，不要硬猜。
                      - 只输出素材名本身，不要解释、不要标点、不要引号。
                      """;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var answer = (await kernel.AskAsync("采集素材识别", goal, prompt, cts.Token).ConfigureAwait(false)).Trim();
            answer = answer.Trim('「', '」', '"', '。', '，', ' ');

            // 模型爱加解释，取第一行并限制长度，避免把一整段话当素材名去匹配
            var firstLine = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
            if (firstLine.Length is 0 or > 12 || firstLine.Contains("不确定", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return firstLine;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("识别采集素材超时，按原词找路线");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "识别采集素材失败");
        }

        return string.Empty;
    }

    /// <summary>
    /// 采集的用法说明，顺带列出本地已有的素材路线。
    /// </summary>
    private static string BuildPathingHelp(string? goal = null)
    {
        var materials = PathingRouteLibrary.Materials();
        var lines = new StringBuilder();
        if (goal is { Length: > 0 })
        {
            lines.Append($"没找到「{goal}」对应的路线。\n");
        }

        lines.Append("采集靠地图追踪路线走，得先有路线文件。\n");
        lines.Append("· 说素材名：「采清心」「采集霓裳花」\n");
        lines.Append("· 说角色名也行，我会自己换成突破材料：「采胡桃」\n");

        if (materials.Count == 0)
        {
            lines.Append("\n本地还没有任何路线：到「地图追踪」页下载，或在脚本仓库里订阅路线包。");
        }
        else
        {
            var preview = materials.Count > 24 ? materials.Take(24) : materials;
            lines.Append($"\n本地已有 {materials.Count} 种素材的路线：{string.Join("、", preview)}");
            if (materials.Count > 24)
            {
                lines.Append("…");
            }
        }

        return lines.ToString();
    }

    /// <summary>
    /// 交给模型自由操作：看画面、点按钮，直到做完或放弃。
    /// </summary>
    /// <summary>
    /// 显式要求 AI 接管：用户用 <c>/</c> 开头说话时走这条。
    /// <para>
    /// 与普通自由操作的区别是不做任何本地预判——不查路线、不看开关，
    /// 直接把话和整套工具交给模型，由它自己决定下发任务还是操作界面。
    /// </para>
    /// </summary>
    /// <param name="goal">用户要做什么，已经去掉前缀。</param>
    /// <param name="onRunStarted">任务被受理后回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<RemoteTaskReply> ExecuteAgentAsync(
        string goal,
        Action<Guid>? onRunStarted,
        CancellationToken cancellationToken)
    {
        return PilotAsync(goal, onRunStarted, cancellationToken, explicitRequest: true);
    }

    private async Task<RemoteTaskReply> PilotAsync(
        string goal,
        Action<Guid>? onRunStarted,
        CancellationToken cancellationToken,
        bool explicitRequest = false)
    {
        // 采集请求经常被说成「你自己去捡一下」：先看盘上有没有路线，有就直接按路线跑。
        // 这条分支不花模型调用，也不要求开自由操作——采集本来就是跑图，不是临场点。
        // 但用户用 / 明确要求 AI 接管时跳过：他要的是模型自己判断，不是一个固定答案。
        if (!explicitRequest
            && await TryStartPathingAsync(goal, null, onRunStarted, cancellationToken).ConfigureAwait(false)
            is { } started)
        {
            logger.LogInformation("自由操作改走地图追踪：{Goal}", goal);
            return started;
        }

        var config = configService.Get();
        if (!explicitRequest && !config.RemoteTaskConfig.SelfLearningEnabled)
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
        var pilot = new GuiPilot(runtime, config, kernel, traces, ops, agentTasks);
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
        if (message.Content.Contains("脚本", StringComparison.Ordinal))
        {
            text += "\n\n想不起脚本叫什么，发「脚本列表」，我把装了的都列出来。";
        }

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
               · 绑定米游社 —— 私聊扫码，之后不开游戏也能查
               · 包 / 抽卡 —— 打开界面截图给你
               · 截图 / 全屏截图
               · 切人2 —— 切到队伍第 2 位
               · 关游戏
               · 脚本 —— 列出或运行用户脚本
               · 定时 —— 定时任务管理
               · 状态 / 停止 / 任务
               · 更新 —— 检查并下载新版，再发「重启更新」换上
               采素材走地图追踪路线：「采清心」「采胡桃」都行，角色名我会自己换成突破材料。
               没有专门任务的活（翻某个界面、点某个按钮），说「自由操作」加要做什么，我自己看着画面点。
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
