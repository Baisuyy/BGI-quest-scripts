using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.GameTask.SoloTask.Execution;
using BetterGenshinImpact.Infrastructure.Network;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 远程任务宿主：维持 QQ 机器人网关会话，把聊天指令转成游戏内操作，并在任务结束后回传结果与截图。
/// <para>
/// 只允许主实例（Primary）运行，避免桌面分身多开时同一 AppID 出现多条会话互相顶号。
/// </para>
/// </summary>
public sealed class QqBotService : IHostedService
{
    /// <summary>
    /// 配置读取服务。
    /// </summary>
    private readonly IConfigService _configService;

    /// <summary>
    /// 指令执行器。
    /// </summary>
    private readonly RemoteTaskCommandHandler _handler;

    /// <summary>
    /// 指令解析器：规则优先，不认识的交给大模型。
    /// </summary>
    private readonly RemoteTaskIntentResolver _resolver;

    /// <summary>
    /// 独立任务状态机，用于订阅任务结束事件。
    /// </summary>
    private readonly SoloTaskExecutionService _executionService;

    /// <summary>
    /// 命名 HttpClient 工厂。
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// 多实例上下文，用于只在主实例启动机器人。
    /// </summary>
    private readonly InstanceService _instanceService;

    /// <summary>
    /// 日志。
    /// </summary>
    private readonly ILogger<QqBotService> _logger;

    /// <summary>
    /// 保证同一时刻只有一条指令在解析与回复，避免多条消息交叉修改任务状态。
    /// </summary>
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>
    /// 当前活跃的网关客户端。
    /// </summary>
    private QqBotGatewayClient? _gateway;

    /// <summary>
    /// 最近一次由聊天指令发起的运行，用于在结束事件里决定是否回传。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, PendingRun> _pending = new();

    /// <summary>
    /// 最近一次收到消息的会话。多步计划不是单个 RunId，结束时要靠它把汇总发回原会话。
    /// </summary>
    private (QqBotTarget Target, string MessageId)? _lastChat;

    /// <summary>
    /// 多步计划执行器，用于订阅计划结束并主动回报。
    /// </summary>
    private readonly RemoteTaskPlanRunner _planRunner;

    /// <summary>
    /// 创建远程任务宿主。
    /// </summary>
    public QqBotService(
        IConfigService configService,
        RemoteTaskCommandHandler handler,
        RemoteTaskIntentResolver resolver,
        SoloTaskExecutionService executionService,
        RemoteTaskPlanRunner planRunner,
        IHttpClientFactory httpClientFactory,
        InstanceService instanceService,
        ILogger<QqBotService> logger)
    {
        _configService = configService;
        _handler = handler;
        _resolver = resolver;
        _executionService = executionService;
        _planRunner = planRunner;
        _httpClientFactory = httpClientFactory;
        _instanceService = instanceService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_instanceService.Context.IsRoot)
        {
            _logger.LogInformation("非主实例，跳过 QQ 机器人远程任务");
            return Task.CompletedTask;
        }

        var config = _configService.Get();
        var remote = config.RemoteTaskConfig;
        if (!remote.QqBotEnabled)
        {
            _logger.LogInformation("QQ 机器人远程任务未启用");
            return Task.CompletedTask;
        }

        var appId = FirstNonEmpty(remote.QqBotAppId, config.NotificationConfig.QqAppId);
        var appSecret = FirstNonEmpty(remote.QqBotAppSecret, config.NotificationConfig.QqClientSecret);
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            _logger.LogWarning("QQ 机器人远程任务缺少 AppID 或 AppSecret，已跳过启动");
            return Task.CompletedTask;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Notification);
            var api = new QqBotApiClient(httpClient, appId, appSecret, _logger);
            // 跟随「网络与代理」设置：ClientWebSocket 默认不看 HttpClient.DefaultProxy。
            var gateway = new QqBotGatewayClient(api, _logger, HttpClient.DefaultProxy);
            gateway.MessageReceived += OnMessageReceivedAsync;
            _executionService.RunCompleted += OnRunCompleted;
            _planRunner.Finished += OnPlanFinished;
            _gateway = gateway;
            // 连接循环自己管理生命周期，只由 StopAsync 结束，避免宿主启动令牌提前回收。
            gateway.Start(CancellationToken.None);
            _logger.LogInformation("QQ 机器人远程任务已启动，AppID={AppId}", appId);
        }
        catch (Exception exception)
        {
            // 机器人启动失败不应影响主程序启动。
            _logger.LogError(exception, "QQ 机器人远程任务启动失败");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _executionService.RunCompleted -= OnRunCompleted;
        _planRunner.Finished -= OnPlanFinished;
        var gateway = _gateway;
        _gateway = null;
        if (gateway is not null)
        {
            gateway.MessageReceived -= OnMessageReceivedAsync;
            await gateway.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 处理一条入站消息：校验发送者、执行指令、立即回复。
    /// </summary>
    private async Task OnMessageReceivedAsync(QqBotIncomingMessage message)
    {
        if (!IsAllowed(message))
        {
            _logger.LogWarning("拒绝来自未授权 OpenID 的远程指令：{OpenId}", message.SenderOpenId);
            await ReplyAsync(message.Target, "这个账号没有远程控制权限。\n请把 OpenID 填进「设置 → 远程任务」的白名单：\n" + message.SenderOpenId, null, message.MessageId)
                .ConfigureAwait(false);
            return;
        }

        var config = _configService.Get().RemoteTaskConfig;
        if (message.Scope == QqBotMessageScope.Group && !config.QqBotAllowGroup)
        {
            await ReplyAsync(message.Target, "群聊下发已关闭，请私聊机器人。", null, message.MessageId).ConfigureAwait(false);
            return;
        }

        _lastChat = (message.Target, message.MessageId);
        PruneStalePending();

        RemoteTaskReply reply;
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var command = await _resolver.ResolveAsync(message.Content, CancellationToken.None).ConfigureAwait(false);
            // 启动前登记：任务可能几十毫秒就失败退出，等 ExecuteAsync 返回再登记就收不到结束事件了
            reply = await _handler.ExecuteAsync(
                    command,
                    message,
                    CancellationToken.None,
                    runId => _pending[runId] = new PendingRun(runId, message.Target, message.MessageId, message.Content))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "处理远程指令失败：{Content}", message.Content);
            reply = new RemoteTaskReply($"执行失败：{exception.Message}");
        }
        finally
        {
            _commandGate.Release();
        }

        // 兜底：万一某条路径没走启动回调，这里仍然登记一次（已在字典里就不重复覆盖）
        if (reply.RunId is { } runId)
        {
            _pending.TryAdd(runId, new PendingRun(runId, message.Target, message.MessageId, message.Content));
        }

        await ReplyAsync(message.Target, reply.Text, reply.Image, message.MessageId).ConfigureAwait(false);
    }

    /// <summary>
    /// 清掉一小时前登记的待回传记录。
    /// <para>
    /// 任务被拒绝（比如另一个任务在跑）时不会触发结束事件，记录会留在字典里；
    /// 数量很少，但没必要一直攒着。
    /// </para>
    /// </summary>
    private void PruneStalePending()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var pair in _pending)
        {
            if (pair.Value.StartedAt < cutoff)
            {
                _pending.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// 独立任务结束时回传结果与截图；只有由聊天指令发起的运行才会回传。
    /// </summary>
    private void OnRunCompleted(object? sender, SoloTaskRunCompletedEventArgs e)
    {
        // 按 RunId 取，而不是「最近一条」：连着下发两个任务时，前一个的回传不能丢
        if (!_pending.TryRemove(e.RunId, out var pending))
        {
            return;
        }

        _ = NotifyCompletionAsync(pending, e);
    }

    /// <summary>
    /// 多步计划跑完：把每一步的结果汇总发回最近一次发消息的会话。
    /// <para>
    /// 计划里的每一步都是独立的 RunId，走不了单任务的回传通道，所以单独订阅计划结束事件。
    /// </para>
    /// </summary>
    private void OnPlanFinished(string summary)
    {
        if (_lastChat is not { } chat)
        {
            return;
        }

        _ = SendPlanSummaryAsync(chat, summary);
    }

    /// <summary>
    /// 发送计划汇总。结束时间可能已超出被动回复窗口，先试被动回复再退回主动消息。
    /// </summary>
    private async Task SendPlanSummaryAsync((QqBotTarget Target, string MessageId) chat, string summary)
    {
        try
        {
            var config = _configService.Get().RemoteTaskConfig;
            byte[]? image = null;
            if (config.ScreenshotOnFinish)
            {
                image = await _handler.CaptureAsync(CancellationToken.None, allowStartCapture: false).ConfigureAwait(false);
            }

            var text = "计划跑完了：\n" + summary;
            try
            {
                await ReplyAsync(chat.Target, text, image, chat.MessageId).ConfigureAwait(false);
            }
            catch (QqBotApiException exception)
            {
                _logger.LogInformation("被动回复计划汇总失败，改用主动消息：{Message}", exception.Message);
                await ReplyAsync(chat.Target, text, image, null).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "发送计划汇总失败");
        }
    }

    /// <summary>
    /// 组装结束通知并发送。失败只记录日志，不影响主程序。
    /// </summary>
    private async Task NotifyCompletionAsync(PendingRun pending, SoloTaskRunCompletedEventArgs e)
    {
        try
        {
            var config = _configService.Get().RemoteTaskConfig;
            var elapsed = DateTimeOffset.UtcNow - pending.StartedAt;
            var duration = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分"
                : elapsed.TotalMinutes >= 1
                    ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
                    : $"{Math.Max(1, elapsed.Seconds)} 秒";

            var name = _handler.DisplayNameOf(e.TaskId);
            var headline = e.Result.Outcome switch
            {
                SoloTaskRunOutcome.Completed => $"「{name}」已结束，用时 {duration}。",
                SoloTaskRunOutcome.Cancelled => $"「{name}」已取消，用时 {duration}。",
                SoloTaskRunOutcome.Rejected => $"「{name}」未执行：{e.Result.ErrorMessage}",
                _ => $"「{name}」执行失败，用时 {duration}。原因：{e.Result.ErrorMessage}"
            };
            var detail = e.Result.TaskResult?.Message;
            var text = string.IsNullOrWhiteSpace(detail) ? headline : $"{headline}\n{detail}";

            byte[]? image = null;
            if (config.ScreenshotOnFinish && e.Result.Outcome != SoloTaskRunOutcome.Rejected)
            {
                image = await _handler.CaptureAsync(CancellationToken.None, allowStartCapture: false).ConfigureAwait(false);
            }

            // 结束时间可能已超出被动回复窗口，先试被动回复，失败再退回主动消息。
            try
            {
                await ReplyAsync(pending.Target, text, image, pending.MessageId).ConfigureAwait(false);
            }
            catch (QqBotApiException exception)
            {
                _logger.LogInformation("被动回复结束通知失败，改用主动消息：{Message}", exception.Message);
                await ReplyAsync(pending.Target, text, image, null).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "回传任务结束通知失败");
        }
    }

    /// <summary>
    /// 发送文本与可选截图。图片发送失败时降级为纯文本。
    /// </summary>
    private async Task ReplyAsync(QqBotTarget target, string text, byte[]? image, string? messageId)
    {
        var gateway = _gateway;
        if (gateway is null)
        {
            return;
        }

        if (image is { Length: > 0 })
        {
            try
            {
                await SendAsync(target, text, image, messageId).ConfigureAwait(false);
                return;
            }
            catch (QqBotApiException exception)
            {
                _logger.LogWarning("回传截图失败，降级为纯文本：{Message}", exception.Message);
                text += "\n（截图回传失败）";
            }
        }

        await SendAsync(target, text, null, messageId).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 REST 客户端发送。
    /// </summary>
    private async Task SendAsync(QqBotTarget target, string text, byte[]? image, string? messageId)
    {
        var api = GetApi();
        if (api is null)
        {
            return;
        }

        var config = _configService.Get().RemoteTaskConfig;
        var trimmed = text.Length > config.ReplyMaxChars
            ? text[..config.ReplyMaxChars] + "\n…（内容过长已截断）"
            : text;
        await api.SendTextAsync(target, trimmed, messageId).ConfigureAwait(false);
        _logger.LogInformation(
            "已回传消息：{Length} 字，{Image}，目标 {Target}",
            trimmed.Length,
            image is { Length: > 0 } ? $"含图 {image.Length / 1024} KB" : "纯文本",
            $"{target.Scope}/{target.Id}");

        if (image is { Length: > 0 })
        {
            await api.SendImageAsync(target, image, messageId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 取当前 REST 客户端；网关未启动时为 null。
    /// </summary>
    private QqBotApiClient? GetApi()
    {
        return _gateway?.Api;
    }

    /// <summary>
    /// 判断发送者是否在白名单内。白名单为空时把第一个发消息的人绑定为所有者。
    /// </summary>
    private bool IsAllowed(QqBotIncomingMessage message)
    {
        var config = _configService.Get();
        var allowed = ParseOpenIds(config.RemoteTaskConfig.QqBotAllowedOpenIds);
        if (allowed.Count == 0)
        {
            config.RemoteTaskConfig.QqBotAllowedOpenIds = message.SenderOpenId;
            _configService.Save();
            _logger.LogInformation("QQ 机器人首次绑定远程控制所有者：{OpenId}", message.SenderOpenId);
            return true;
        }

        if (allowed.Contains(message.SenderOpenId))
        {
            return true;
        }

        // 群里同一个人的 member_openid 与私聊 openid 不同，允许群主在自己群里下发。
        if (message.Scope == QqBotMessageScope.Group
            && config.RemoteTaskConfig.QqBotAllowedOpenIds.Contains(message.GroupOpenId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析逗号、分号或换行分隔的 OpenID 列表。
    /// </summary>
    private static HashSet<string> ParseOpenIds(string? raw)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }

        foreach (var item in raw.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(item.Trim());
        }

        return set;
    }

    /// <summary>
    /// 取第一个非空字符串，用于配置回落。
    /// </summary>
    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 一次由聊天指令发起的运行上下文。
    /// </summary>
    /// <param name="RunId">运行标识。</param>
    /// <param name="Target">回复目标。</param>
    /// <param name="MessageId">触发指令的消息 ID。</param>
    /// <param name="Text">触发指令原文。</param>
    private sealed record PendingRun(Guid RunId, QqBotTarget Target, string MessageId, string Text)
    {
        /// <summary>
        /// 发起时间，用于计算耗时。
        /// </summary>
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }
}
