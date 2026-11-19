using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// QQ 官方机器人网关的常驻客户端：维持 WebSocket 会话、按需重连，并把 @机器人 消息还原成
/// <see cref="QqBotIncomingMessage"/> 交给上层处理。
/// <para>
/// 只订阅群聊与单聊事件（intents = 1 &lt;&lt; 25）。频道 @ 消息需要平台额外开通 GUILD_MESSAGES，
/// 未开通时一旦加入 intents 会整体鉴权失败（op=9 / 4014），因此这里不订阅。
/// </para>
/// </summary>
public sealed partial class QqBotGatewayClient : IAsyncDisposable
{
    /// <summary>
    /// 群聊与单聊事件。
    /// </summary>
    private const int Intents = 1 << 25;

    /// <summary>
    /// 鉴权失败的重试上限，超过后放弃本次运行，避免无效重连刷日志。
    /// </summary>
    private const int MaxAuthFailures = 3;

    /// <summary>
    /// 网络类失败的重试间隔上限。代理配置不对时连接永远不会成功，如果一直按 60 秒重试，
    /// 一天就是 1440 条同文日志，真正的故障反而被淹掉。
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 连续失败超过这个次数后不再逐次记录，只在 2 的幂次上汇总一条。
    /// </summary>
    private const int QuietAfterFailures = 3;

    /// <summary>
    /// REST 客户端，用于取令牌和网关地址。
    /// </summary>
    private readonly QqBotApiClient _api;

    /// <summary>
    /// 日志。
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// 连接使用的代理，为空时直连。
    /// </summary>
    private readonly IWebProxy? _proxy;

    /// <summary>
    /// 停止信号。
    /// </summary>
    private readonly CancellationTokenSource _stopCts = new();

    /// <summary>
    /// 后台连接循环。
    /// </summary>
    private Task? _loopTask;

    /// <summary>
    /// 当前会话已收到的最新序号。
    /// </summary>
    private long _seq;

    /// <summary>
    /// 收到一条 @机器人 消息时触发。处理是串行 await 的，实现方不应长时间阻塞。
    /// </summary>
    public event Func<QqBotIncomingMessage, Task>? MessageReceived;

    /// <summary>
    /// 创建网关客户端。
    /// </summary>
    /// <param name="api">REST 客户端。</param>
    /// <param name="logger">日志。</param>
    /// <param name="proxy">可选代理。</param>
    public QqBotGatewayClient(QqBotApiClient api, ILogger logger, IWebProxy? proxy = null)
    {
        _api = api;
        _logger = logger;
        _proxy = proxy;
    }

    /// <summary>
    /// REST 客户端，供上层在回复时复用同一份令牌缓存。
    /// </summary>
    public QqBotApiClient Api => _api;

    /// <summary>
    /// 是否已建立会话（收到 READY）。
    /// </summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// 启动后台连接循环，立即返回。
    /// </summary>
    /// <param name="cancellationToken">随宿主生命周期取消的令牌。</param>
    public void Start(CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// 停止连接循环并等待退出。
    /// </summary>
    public async Task StopAsync()
    {
        await _stopCts.CancelAsync().ConfigureAwait(false);
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止时取消是预期行为。
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }

    /// <summary>
    /// 连接循环：断线后按指数退避重连，鉴权连续失败到上限时退出。
    /// </summary>
    private async Task RunAsync(CancellationToken outerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken, _stopCts.Token);
        var token = linked.Token;
        var backoff = TimeSpan.FromSeconds(2);
        var authFailures = 0;
        var connectFailures = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(token).ConfigureAwait(false);
                authFailures = 0;
                if (connectFailures > 0)
                {
                    _logger.LogInformation("QQ 机器人网关已恢复，此前连续失败 {Count} 次", connectFailures);
                }

                connectFailures = 0;
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (QqBotAuthException exception)
            {
                authFailures++;
                _logger.LogError("QQ 机器人鉴权失败（第 {Count} 次）：{Message}", authFailures, exception.Message);
                if (authFailures >= MaxAuthFailures)
                {
                    _logger.LogError("QQ 机器人鉴权连续失败，已停止重连；请检查 AppID/AppSecret 与开放平台的事件订阅权限");
                    break;
                }
            }
            catch (Exception exception)
            {
                connectFailures++;
                // 前几次把原因说清楚，之后按 2 的幂汇总：一次代理配错能刷出上千行同文，
                // 用户去翻日志时反而找不到真正的报错。
                if (connectFailures <= QuietAfterFailures || IsPowerOfTwo(connectFailures))
                {
                    LogConnectFailure(exception, connectFailures, backoff);
                }
            }
            finally
            {
                IsReady = false;
            }

            try
            {
                await Task.Delay(backoff, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }

        _logger.LogInformation("QQ 机器人网关已停止");
    }

    /// <summary>
    /// 是否 2 的幂：用来把「第 8 次、第 16 次、第 32 次……」挑出来做汇总。
    /// </summary>
    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    /// <summary>
    /// 记录一次网络类失败。不输出堆栈——这里的调用链对用户没有意义，
    /// 需要堆栈时把日志级别调到 Debug，BgiHttpDefaultsHandler 会带异常另记一条。
    /// </summary>
    private void LogConnectFailure(Exception exception, int failures, TimeSpan retry)
    {
        var (summary, hint) = DescribeFailure(exception);
        var retryText = retry >= TimeSpan.FromMinutes(1)
            ? $"{retry.TotalMinutes:0.#} 分钟后重试"
            : $"{retry.TotalSeconds:0} 秒后重试";

        if (failures == 1)
        {
            _logger.LogWarning("QQ 机器人网关连不上：{Summary}{Hint}，{Retry}", summary, hint, retryText);
        }
        else
        {
            _logger.LogWarning("QQ 机器人网关仍连不上（已连续失败 {Count} 次）：{Summary}，{Retry}",
                failures, summary, retryText);
        }
    }

    /// <summary>
    /// 把异常翻译成一句人话。TLS 握手失败有几种完全不同的成因，笼统说「证书校验未通过」
    /// 会把「代理规则 REJECT」误导成「证书有问题」，用户按错的方向改配置。
    /// </summary>
    private static (string Summary, string Hint) DescribeFailure(Exception exception)
    {
        var certificate = false;
        var handshakeCut = false;
        var reset = false;
        var timeout = false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;

            switch (current)
            {
                case System.Security.Authentication.AuthenticationException:
                    certificate = true;
                    break;
                case System.Net.Sockets.SocketException socket
                    when socket.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionReset
                        or System.Net.Sockets.SocketError.ConnectionAborted:
                    reset = true;
                    break;
                case TimeoutException:
                    timeout = true;
                    break;
            }

            if (message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                certificate = true;
            }
            else if (message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
                     || message.Contains("0 bytes from the transport", StringComparison.OrdinalIgnoreCase))
            {
                handshakeCut = true;
            }
            else if (message.Contains("远程主机强迫关闭", StringComparison.Ordinal))
            {
                reset = true;
            }
        }

        // 证书问题最具体，优先于泛化的「握手失败」
        if (certificate)
        {
            return ("api.sgroup.qq.com 的证书校验未通过",
                "：多半是代理或抓包工具替换了证书，把该域名加进直连规则后会自动恢复");
        }

        if (handshakeCut)
        {
            return ("与 api.sgroup.qq.com 的 TLS 握手被中途掐断",
                "：代理规则可能把该域名设成了 REJECT，或网络对握手做了阻断，改直连再看");
        }

        if (reset)
        {
            return ("与 api.sgroup.qq.com 的连接被对端重置", "：网络或代理中途断开，稍后自动重连");
        }

        if (timeout)
        {
            return ("连接 api.sgroup.qq.com 超时", "：当前网络到该域名不通，检查代理与 DNS");
        }

        return (exception.GetType().Name, string.Empty);
    }

    /// <summary>
    /// 一次完整的会话：取地址、连接、鉴权、收发。
    /// </summary>
    private async Task RunSessionAsync(CancellationToken token)
    {
        var gatewayUrl = await _api.GetGatewayUrlAsync(token).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        if (_proxy is not null)
        {
            socket.Options.Proxy = _proxy;
        }

        await socket.ConnectAsync(new Uri(gatewayUrl), token).ConfigureAwait(false);
        _logger.LogInformation("QQ 机器人已连接网关 {Url}", gatewayUrl);

        var heartbeatInterval = TimeSpan.FromSeconds(30);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? heartbeatTask = null;

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var frame = await ReceiveFrameAsync(socket, token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (frame.Seq is { } seq)
                {
                    Interlocked.Exchange(ref _seq, seq);
                }

                switch (frame.Op)
                {
                    case 10:
                        var hello = Deserialize<QqBotHello>(frame.Data) ?? new QqBotHello();
                        heartbeatInterval = TimeSpan.FromMilliseconds(Math.Max(5000, hello.HeartbeatInterval));
                        await SendIdentifyAsync(socket, token).ConfigureAwait(false);
                        heartbeatTask = RunHeartbeatAsync(socket, heartbeatInterval, token);
                        break;
                    case 0:
                        await DispatchEventAsync(frame, token).ConfigureAwait(false);
                        break;
                    case 7:
                        _logger.LogInformation("QQ 机器人收到重连指令");
                        return;
                    case 9:
                        throw new QqBotAuthException("网关拒绝了 intents 或令牌无效（op=9）");
                    case 11:
                        // 心跳确认，无需处理。
                        break;
                }
            }
        }
        catch (WebSocketException exception)
        {
            _logger.LogWarning("QQ 机器人 WebSocket 异常：{Message}", exception.Message);
        }
        finally
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            if (heartbeatTask is not null)
            {
                try
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 会话结束时心跳取消是预期行为。
                }
            }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // 关闭握手失败不影响后续重连。
                }
            }
        }
    }

    /// <summary>
    /// 周期发送心跳，携带会话序号。
    /// </summary>
    private async Task RunHeartbeatAsync(ClientWebSocket socket, TimeSpan interval, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                var payload = new StringBuilder()
                    .Append("{\"op\":1,\"d\":")
                    .Append(Interlocked.Read(ref _seq))
                    .Append('}')
                    .ToString();
                await SendTextAsync(socket, payload, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 会话结束时正常退出。
        }
        catch (WebSocketException exception)
        {
            _logger.LogDebug("QQ 机器人心跳发送失败：{Message}", exception.Message);
        }
    }

    /// <summary>
    /// 发送鉴权帧，声明 intents 与分片。
    /// </summary>
    private async Task SendIdentifyAsync(ClientWebSocket socket, CancellationToken token)
    {
        var accessToken = await _api.GetAccessTokenAsync(token).ConfigureAwait(false);
        var payload = new StringBuilder()
            .Append("{\"op\":2,\"d\":{\"token\":\"QQBot ")
            .Append(accessToken)
            .Append("\",\"intents\":")
            .Append(Intents)
            .Append(",\"shard\":[0,1],\"properties\":{\"$os\":\"windows\",\"$browser\":\"bettergi\",\"$device\":\"bettergi\"}}}")
            .ToString();
        await SendTextAsync(socket, payload, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 分发 op=0 事件。
    /// </summary>
    private async Task DispatchEventAsync(QqBotGatewayFrame frame, CancellationToken token)
    {
        switch (frame.Type)
        {
            case "READY":
                var ready = Deserialize<QqBotReady>(frame.Data);
                IsReady = true;
                _logger.LogInformation("QQ 机器人会话已就绪，session={Session}", ready?.SessionId);
                return;
            case "C2C_MESSAGE_CREATE":
                await RaiseAsync(frame.Data, QqBotMessageScope.C2C, token).ConfigureAwait(false);
                return;
            case "GROUP_AT_MESSAGE_CREATE":
                await RaiseAsync(frame.Data, QqBotMessageScope.Group, token).ConfigureAwait(false);
                return;
            case "RESUMED":
                IsReady = true;
                return;
            default:
                _logger.LogDebug("QQ 机器人忽略事件 {Type}", frame.Type);
                return;
        }
    }

    /// <summary>
    /// 把消息事件转换成入站消息并交给上层。
    /// </summary>
    private async Task RaiseAsync(JsonElement data, QqBotMessageScope scope, CancellationToken token)
    {
        var payload = Deserialize<QqBotMessagePayload>(data);
        if (payload is null || string.IsNullOrEmpty(payload.Id))
        {
            return;
        }

        var handler = MessageReceived;
        if (handler is null)
        {
            return;
        }

        var senderOpenId = scope == QqBotMessageScope.Group
            ? payload.Author?.MemberOpenId ?? payload.Author?.Id ?? string.Empty
            : payload.Author?.Id ?? string.Empty;
        var groupOpenId = payload.GroupOpenId ?? string.Empty;
        if (string.IsNullOrEmpty(senderOpenId) || scope == QqBotMessageScope.Group && string.IsNullOrEmpty(groupOpenId))
        {
            _logger.LogWarning("QQ 机器人消息缺少 OpenID，已忽略：{Id}", payload.Id);
            return;
        }

        var message = new QqBotIncomingMessage
        {
            MessageId = payload.Id,
            Content = MentionPattern().Replace(payload.Content ?? string.Empty, " ").Trim(),
            Scope = scope,
            SenderOpenId = senderOpenId,
            GroupOpenId = groupOpenId,
            ImageUrls = payload.Attachments is null
                ? []
                : payload.Attachments
                    .Where(a => a.Url is { Length: > 0 })
                    .Select(a => a.Url!)
                    .ToList()
        };

        try
        {
            await handler(message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 宿主停止。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "处理 QQ 机器人消息 {Id} 失败", message.MessageId);
        }
    }

    /// <summary>
    /// 接收一帧；服务端关闭时返回 null。
    /// </summary>
    private static async Task<QqBotGatewayFrame?> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Write(chunk, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var bytes = buffer.ToArray();
        // 部分大事件会以 zlib 压缩帧下发，解压后再交给 JSON 解析。
        if (result.MessageType == WebSocketMessageType.Binary && bytes.Length > 2 && bytes[0] == 0x78)
        {
            using var input = new MemoryStream(bytes);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await zlib.CopyToAsync(output, token).ConfigureAwait(false);
            bytes = output.ToArray();
        }

        return JsonSerializer.Deserialize<QqBotGatewayFrame>(bytes);
    }

    /// <summary>
    /// 发送一个文本帧。
    /// </summary>
    private static Task SendTextAsync(ClientWebSocket socket, string payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    /// <summary>
    /// 从 JSON 元素反序列化，字段缺失时返回 null。
    /// </summary>
    private static T? Deserialize<T>(JsonElement element)
    {
        return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : element.Deserialize<T>();
    }

    /// <summary>
    /// 匹配群聊正文里的 @机器人 片段。
    /// </summary>
    [GeneratedRegex("<@!?\\d+>")]
    private static partial Regex MentionPattern();
}

/// <summary>
/// 网关鉴权失败，需要人工处理配置，不参与常规重连退避。
/// </summary>
public sealed class QqBotAuthException(string message) : Exception(message);
