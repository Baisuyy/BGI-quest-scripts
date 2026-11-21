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
    /// 同一种失败最多按这个间隔记一条。
    /// <para>
    /// 以前按「第 2/4/8/16 次」记，失败停在 5 次时反而不吵；但真实日志里连续失败上千次，
    /// 2 的幂一样会刷屏（10:20 到 15:00 记了 1600 多条），而且完全看不出「已经坏了多久」。
    /// 改成按时间窗：首次立刻记，之后每 30 分钟一条带累计时长的汇总。
    /// </para>
    /// </summary>
    private static readonly TimeSpan FaultLogInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 当前故障的摘要文本，用于判断「还是同一个毛病」。
    /// </summary>
    private string? _faultSummary;

    /// <summary>
    /// 本次故障第一次发生的时刻。
    /// </summary>
    private DateTime _faultSince;

    /// <summary>
    /// 本次故障内累计的失败次数。
    /// </summary>
    private int _faultCount;

    /// <summary>
    /// 上一次写故障日志的时刻。
    /// </summary>
    private DateTime _lastFaultLogAt = DateTime.MinValue;

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
    /// 是否跳过 TLS 证书校验，仅给「代理做了中间人解密」的场景用。
    /// </summary>
    private readonly bool _allowInsecureTls;

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
    /// <param name="allowInsecureTls">
    ///     跳过 TLS 证书校验。只在代理做了中间人解密、且没法把
    ///     <c>api.sgroup.qq.com</c> 加进直连时使用。
    /// </param>
    public QqBotGatewayClient(QqBotApiClient api, ILogger logger, IWebProxy? proxy = null, bool allowInsecureTls = false)
    {
        _api = api;
        _logger = logger;
        _proxy = proxy;
        _allowInsecureTls = allowInsecureTls;
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

        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(token).ConfigureAwait(false);
                authFailures = 0;
                // 会话正常结束（到点被腾讯收线）也算恢复：说明网络和证书这一层是通的。
                var recovered = ResetFaultTracking();
                if (!string.IsNullOrEmpty(recovered))
                {
                    _logger.LogInformation("QQ 机器人网关已恢复，{Detail}", recovered);
                }

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
                // 一句话说清楚「什么毛病、坏了多久」，同类失败 30 分钟内只记一条。
                LogConnectFailure(exception, backoff);
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
    /// 记录一次网络类失败。不输出堆栈——这里的调用链对用户没有意义，
    /// 需要堆栈时把日志级别调到 Debug，BgiHttpDefaultsHandler 会带异常另记一条。
    /// <para>
    /// 节流按「同一种原因 + 30 分钟」：首次立刻记，之后每个窗口一条，并带上「已持续多久、失败多少次」。
    /// 这样一份日志里既能看见故障发生过，也能看见它持续了多久，而不是被同一个字符串刷满。
    /// </para>
    /// </summary>
    private void LogConnectFailure(Exception exception, TimeSpan retry)
    {
        var (summary, hint) = DescribeFailure(exception);
        var now = DateTime.Now;

        // 换了原因就重开一轮统计：代理断开和 DNS 失败混在一起会让「已持续」失去意义。
        if (!string.Equals(_faultSummary, summary, StringComparison.Ordinal))
        {
            _faultSummary = summary;
            _faultSince = now;
            _faultCount = 0;
            _lastFaultLogAt = DateTime.MinValue;
        }

        _faultCount++;

        if (_lastFaultLogAt != DateTime.MinValue && now - _lastFaultLogAt < FaultLogInterval)
        {
            return;
        }

        _lastFaultLogAt = now;
        var retryText = retry >= TimeSpan.FromMinutes(1)
            ? $"{retry.TotalMinutes:0.#} 分钟后重试"
            : $"{retry.TotalSeconds:0} 秒后重试";

        if (_faultCount == 1)
        {
            _logger.LogWarning("QQ 机器人网关连不上：{Summary}{Hint}，{Retry}", summary, hint, retryText);
            return;
        }

        _logger.LogWarning("QQ 机器人网关仍连不上（已持续 {Minutes} 分钟，失败 {Count} 次）：{Summary}，{Retry}",
            Math.Max(1, (int)(now - _faultSince).TotalMinutes), _faultCount, summary, retryText);
    }

    /// <summary>
    /// 连接恢复后重置故障统计，供下一次故障重新计时。
    /// </summary>
    private string ResetFaultTracking()
    {
        if (_faultCount == 0)
        {
            return string.Empty;
        }

        var text = $"此前连续失败 {_faultCount} 次，持续 {(int)Math.Max(0, (DateTime.Now - _faultSince).TotalMinutes)} 分钟";
        _faultSummary = null;
        _faultCount = 0;
        _faultSince = default;
        _lastFaultLogAt = DateTime.MinValue;
        return text;
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
        var dnsFailure = false;

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
                case System.Net.Sockets.SocketException socket
                    when socket.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                        or System.Net.Sockets.SocketError.NoData
                        or System.Net.Sockets.SocketError.TryAgain:
                    // 11001「不知道这样的主机」是 DNS 挂了，不是对端拒绝。
                    // 之前落到默认分支只显示 HttpRequestException 类型名，用户根本不知道该查什么。
                    dnsFailure = true;
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
            else if (message.Contains("不知道这样的主机", StringComparison.Ordinal))
            {
                dnsFailure = true;
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
                "：多半是代理或抓包工具替换了证书。把该域名加进直连规则最干净；"
                + "代理无法按域名分流时，可在「远程任务」里打开「忽略证书校验」（会重启连接）");
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

        // DNS 失败要排在超时前面：某些网络下解析失败会先表现为 TimeoutException，
        // 说成「连接超时」会把人引到防火墙上去查，其实是域名没解析出来。
        if (dnsFailure)
        {
            return ("解析不到 api.sgroup.qq.com（DNS 失败）",
                "：网络本身可能没通，或代理的 DNS 没接住这个域名；换成系统 DNS / 直连再试");
        }

        if (timeout)
        {
            return ("连接 api.sgroup.qq.com 超时", "：当前网络到该域名不通，检查代理与 DNS");
        }

        return (exception.GetType().Name, string.Empty);
    }

    /// <summary>
    /// 是不是「对端正常收线」。腾讯网关到点就会断开，且经常不走完整关闭握手，
    /// 表现为 1006 或 "without completing the close handshake"——这类断连不影响任务，
    /// 重连后会话序号还能续上。
    /// </summary>
    private static bool IsPeerClosed(WebSocketException exception)
    {
        if (exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("without completing the close handshake", StringComparison.OrdinalIgnoreCase)
               || message.Contains("The remote party closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("已中止", StringComparison.Ordinal)
               || message.Contains("远程方", StringComparison.Ordinal);
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

        if (_allowInsecureTls)
        {
            // ClientWebSocket 的证书回调是 per-socket 的，不会污染其它连接。
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        await socket.ConnectAsync(new Uri(gatewayUrl), token).ConfigureAwait(false);
        _logger.LogInformation("QQ 机器人已连接网关 {Url}", gatewayUrl);
        // TLS 和 DNS 都过了：把之前的故障统计归零，下一次坏掉重新计时。
        ResetFaultTracking();

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
            if (IsPeerClosed(exception))
            {
                // 腾讯网关按会话时长主动踢线，一天十几次是常态，重连后一切照旧。
                // 记 WRN 只会让人以为网络有毛病，也会把真正的握手/证书错误埋掉。
                _logger.LogInformation("QQ 机器人网关会话被对端关闭，准备重连");
            }
            else
            {
                _logger.LogWarning("QQ 机器人 WebSocket 异常：{Message}", exception.Message);
            }
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
