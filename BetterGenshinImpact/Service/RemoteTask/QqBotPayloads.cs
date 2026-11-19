using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 消息来源场景。
/// </summary>
public enum QqBotMessageScope
{
    /// <summary>
    /// 单聊（C2C）。
    /// </summary>
    C2C,

    /// <summary>
    /// 群聊中 @机器人。
    /// </summary>
    Group
}

/// <summary>
/// 回复目标：一条消息只能回复到它来的地方。
/// </summary>
/// <param name="Scope">消息来源场景。</param>
/// <param name="Id">C2C 为用户 OpenID，群聊为群 OpenID。</param>
public sealed record QqBotTarget(QqBotMessageScope Scope, string Id)
{
    /// <summary>
    /// 拼出该目标的 REST 基地址，例如 https://api.sgroup.qq.com/v2/users/{openid}。
    /// </summary>
    public string BaseUrl => Scope == QqBotMessageScope.Group
        ? $"https://api.sgroup.qq.com/v2/groups/{Id}"
        : $"https://api.sgroup.qq.com/v2/users/{Id}";
}

/// <summary>
/// 网关推送的一条 @机器人 消息（C2C_MESSAGE_CREATE / GROUP_AT_MESSAGE_CREATE）。
/// </summary>
public sealed class QqBotIncomingMessage
{
    /// <summary>
    /// 消息 ID，被动回复时必须回传。
    /// </summary>
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    /// 去掉 @ 与首尾空白后的正文。
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 消息来源场景。
    /// </summary>
    public QqBotMessageScope Scope { get; init; }

    /// <summary>
    /// 发送者 OpenID。
    /// </summary>
    public string SenderOpenId { get; init; } = string.Empty;

    /// <summary>
    /// 群 OpenID，私聊时为空。
    /// </summary>
    public string GroupOpenId { get; init; } = string.Empty;

    /// <summary>
    /// 消息附带图片的下载地址，按原始顺序排列。
    /// </summary>
    public IReadOnlyList<string> ImageUrls { get; init; } = [];

    /// <summary>
    /// 回复目标。
    /// </summary>
    public QqBotTarget Target => Scope == QqBotMessageScope.Group
        ? new QqBotTarget(QqBotMessageScope.Group, GroupOpenId)
        : new QqBotTarget(QqBotMessageScope.C2C, SenderOpenId);
}

/// <summary>
/// 网关下发的原始帧。
/// </summary>
public sealed class QqBotGatewayFrame
{
    /// <summary>
    /// 操作码：0 事件、1 心跳、2 鉴权、6 恢复、7 重连、9 鉴权失败、10 Hello、11 心跳确认。
    /// </summary>
    [JsonPropertyName("op")]
    public int Op { get; set; }

    /// <summary>
    /// 序号，心跳需要回传最新值。
    /// </summary>
    [JsonPropertyName("s")]
    public long? Seq { get; set; }

    /// <summary>
    /// 事件类型，仅在 op=0 时有值。
    /// </summary>
    [JsonPropertyName("t")]
    public string? Type { get; set; }

    /// <summary>
    /// 事件或握手数据。
    /// </summary>
    [JsonPropertyName("d")]
    public JsonElement Data { get; set; }
}

/// <summary>
/// Hello 帧数据（op=10）。
/// </summary>
public sealed class QqBotHello
{
    /// <summary>
    /// 心跳间隔，单位毫秒。
    /// </summary>
    [JsonPropertyName("heartbeat_interval")]
    public int HeartbeatInterval { get; set; } = 30000;
}

/// <summary>
/// READY 帧数据（op=0, t=READY）。
/// </summary>
public sealed class QqBotReady
{
    /// <summary>
    /// 本次会话 ID，恢复连接时使用。
    /// </summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>
/// 消息事件原始载荷，字段与 QQ 开放平台文档一致。
/// </summary>
public sealed class QqBotMessagePayload
{
    /// <summary>
    /// 消息 ID。
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 消息正文，群聊里含 &lt;@!botid&gt; 形式的提及。
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 群 OpenID，仅群聊消息有值。
    /// </summary>
    [JsonPropertyName("group_openid")]
    public string? GroupOpenId { get; set; }

    /// <summary>
    /// 发送者信息。
    /// </summary>
    [JsonPropertyName("author")]
    public QqBotAuthor? Author { get; set; }

    /// <summary>
    /// 附件列表，图片消息在这里给出下载地址。
    /// </summary>
    [JsonPropertyName("attachments")]
    public List<QqBotAttachment>? Attachments { get; set; }
}

/// <summary>
/// 消息发送者。
/// </summary>
public sealed class QqBotAuthor
{
    /// <summary>
    /// C2C 为用户 OpenID，群聊为成员 OpenID。
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// 成员 OpenID，群聊消息优先使用该字段。
    /// </summary>
    [JsonPropertyName("member_openid")]
    public string? MemberOpenId { get; set; }

    /// <summary>
    /// 用户 Union OpenID。
    /// </summary>
    [JsonPropertyName("union_openid")]
    public string? UnionOpenId { get; set; }
}

/// <summary>
/// 消息附件。
/// </summary>
public sealed class QqBotAttachment
{
    /// <summary>
    /// 附件类型，图片为 image/png 等。
    /// </summary>
    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    /// <summary>
    /// 下载地址，带有效期。
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// 上传富媒体后返回的文件信息。
/// </summary>
public sealed class QqBotUploadResult
{
    /// <summary>
    /// 文件标识，发送 msg_type=7 消息时使用。
    /// </summary>
    [JsonPropertyName("file_info")]
    public string FileInfo { get; set; } = string.Empty;

    /// <summary>
    /// 文件 UUID。
    /// </summary>
    [JsonPropertyName("file_uuid")]
    public string FileUuid { get; set; } = string.Empty;

    /// <summary>
    /// 有效期，单位秒。
    /// </summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}
