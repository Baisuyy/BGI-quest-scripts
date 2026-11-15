using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// QQ 开放平台 REST 客户端：负责取访问令牌、发文本与发图片。
/// <para>
/// 被动回复要求携带收到消息的 msg_id，同一 msg_id 下 msg_seq 必须唯一，否则会被平台判重丢弃。
/// </para>
/// </summary>
/// <param name="httpClient">外部注入的 HttpClient，可携带代理等既定网络配置。</param>
/// <param name="appId">QQ 开放平台 AppID。</param>
/// <param name="clientSecret">QQ 开放平台 AppSecret。</param>
/// <param name="logger">日志。</param>
public sealed class QqBotApiClient(
    HttpClient httpClient,
    string appId,
    string clientSecret,
    ILogger? logger = null)
{
    /// <summary>
    /// 获取应用访问令牌的地址。
    /// </summary>
    private const string TokenUrl = "https://bots.qq.com/app/getAppAccessToken";

    /// <summary>
    /// 单条文本消息的长度上限，超出部分分条发送。
    /// </summary>
    private const int TextChunkSize = 900;

    /// <summary>
    /// 访问令牌缓存。
    /// </summary>
    private string? _cachedToken;

    /// <summary>
    /// 访问令牌过期时间（UTC）。
    /// </summary>
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    /// <summary>
    /// 保护令牌刷新的信号量，避免并发重复刷新。
    /// </summary>
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>
    /// 回复序号，保证同一 msg_id 下唯一。
    /// </summary>
    private long _messageSeq;

    /// <summary>
    /// 当前使用的 AppID。
    /// </summary>
    public string AppId { get; } = appId;

    /// <summary>
    /// 取访问令牌：命中缓存直接返回，否则刷新。提前 60 秒视为过期。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可用的访问令牌。</returns>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedToken is { Length: > 0 } cached && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return cached;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is { Length: > 0 } current && DateTimeOffset.UtcNow < _tokenExpiry)
            {
                return current;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(
                    new JsonObject
                    {
                        ["appId"] = AppId,
                        ["clientSecret"] = clientSecret
                    }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new QqBotApiException($"获取 access_token 失败：HTTP {(int)response.StatusCode} {body}");
            }

            using var document = JsonDocument.Parse(body);
            var token = document.RootElement.TryGetProperty("access_token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            if (string.IsNullOrEmpty(token))
            {
                throw new QqBotApiException($"获取 access_token 失败：响应缺少 access_token，{body}");
            }

            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expiresElement)
                && int.TryParse(expiresElement.GetString() ?? expiresElement.ToString(), out var seconds)
                    ? seconds
                    : 7200;
            _cachedToken = token;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(120, expiresIn - 60));
            logger?.LogDebug("QQ 机器人访问令牌已刷新，有效期 {Seconds} 秒", expiresIn);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// 向消息来源回一条文本，超长时自动分段。
    /// </summary>
    /// <param name="target">回复目标。</param>
    /// <param name="text">正文，允许为空（此时不发送）。</param>
    /// <param name="messageId">被动回复所对应的消息 ID；为空时使用主动消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SendTextAsync(
        QqBotTarget target,
        string text,
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var chunk in SplitText(Normalize(text), TextChunkSize))
        {
            var payload = new JsonObject
            {
                ["content"] = chunk,
                ["msg_type"] = 0
            };
            AppendReply(payload, messageId);
            await PostAsync($"{target.BaseUrl}/messages", payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 上传图片并以富媒体消息（msg_type=7）回传。
    /// </summary>
    /// <param name="target">回复目标。</param>
    /// <param name="imageBytes">图片字节，建议为压缩后的 JPEG。</param>
    /// <param name="messageId">被动回复所对应的消息 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SendImageAsync(
        QqBotTarget target,
        byte[] imageBytes,
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            return;
        }

        // 图片走 file_data 直传：截图压缩后只有几十到几百 KB，无需分片。
        var upload = new JsonObject
        {
            ["file_type"] = 1,
            ["file_data"] = Convert.ToBase64String(imageBytes),
            ["srv_send_msg"] = false
        };
        var uploadBody = await PostAsync($"{target.BaseUrl}/files", upload, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(uploadBody);
        var fileInfo = document.RootElement.TryGetProperty("file_info", out var info)
            ? info.GetString()
            : null;
        if (string.IsNullOrEmpty(fileInfo))
        {
            throw new QqBotApiException($"上传图片未返回 file_info：{uploadBody}");
        }

        var message = new JsonObject
        {
            ["content"] = string.Empty,
            ["msg_type"] = 7,
            ["media"] = new JsonObject { ["file_info"] = fileInfo }
        };
        AppendReply(message, messageId);
        await PostAsync($"{target.BaseUrl}/messages", message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取网关地址。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>WebSocket 网关 URL。</returns>
    public async Task<string> GetGatewayUrlAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.sgroup.qq.com/gateway");
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new QqBotApiException($"获取网关地址失败：HTTP {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        var url = document.RootElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        return string.IsNullOrEmpty(url)
            ? throw new QqBotApiException($"获取网关地址失败：响应缺少 url，{body}")
            : url;
    }

    /// <summary>
    /// 追加被动回复所需的 msg_id 与自增的 msg_seq。
    /// </summary>
    private void AppendReply(JsonObject payload, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            return;
        }

        payload["msg_id"] = messageId;
        payload["msg_seq"] = Interlocked.Increment(ref _messageSeq) % int.MaxValue;
    }

    /// <summary>
    /// 发送带鉴权的 JSON 请求并返回响应体，失败时抛出带响应体的异常。
    /// </summary>
    private async Task<string> PostAsync(string url, JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // 401 多为令牌失效，清掉缓存让下一次请求重新获取。
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _cachedToken = null;
                _tokenExpiry = DateTimeOffset.MinValue;
            }

            throw new QqBotApiException($"请求 {url} 失败：HTTP {(int)response.StatusCode} {body}");
        }

        return body;
    }

    /// <summary>
    /// 统一换行符并去掉首尾空白，避免 Windows 换行在 QQ 侧显示异常。
    /// </summary>
    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    /// <summary>
    /// 按长度切分文本，尽量在换行处断开。
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string> SplitText(string text, int chunkSize)
    {
        if (text.Length <= chunkSize)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(chunkSize, text.Length - start);
            if (start + length < text.Length)
            {
                var lastBreak = text.LastIndexOf('\n', start + length - 1, length);
                if (lastBreak > start + chunkSize / 3)
                {
                    length = lastBreak - start + 1;
                }
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }
}

/// <summary>
/// QQ 机器人接口调用失败。
/// </summary>
public sealed class QqBotApiException(string message) : Exception(message);
