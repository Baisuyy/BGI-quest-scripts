using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel.DataAnnotations;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 远程任务配置：通过聊天软件把任务下发给 BetterGI，并回传执行截图。
/// <para>
/// 当前只接入 QQ 官方机器人（WebSocket 事件订阅 + C2C/群被动回复）。
/// 机器人凭证留空时回退到「通知设置」里已绑定的 QQ 凭证，避免重复配置。
/// </para>
/// </summary>
[Serializable]
public partial class RemoteTaskConfig : ObservableValidator
{
    /// <summary>
    /// 是否启用 QQ 机器人远程任务。
    /// </summary>
    [ObservableProperty]
    private bool _qqBotEnabled;

    /// <summary>
    /// 是否允许群聊 @机器人 下发任务。关闭时只接受私聊。
    /// </summary>
    [ObservableProperty]
    private bool _qqBotAllowGroup = true;

    /// <summary>
    /// QQ 开放平台 AppID，留空时使用通知设置里的 AppID。
    /// </summary>
    [ObservableProperty]
    private string _qqBotAppId = string.Empty;

    /// <summary>
    /// QQ 开放平台 AppSecret，留空时使用通知设置里的 AppSecret。
    /// </summary>
    [ObservableProperty]
    private string _qqBotAppSecret = string.Empty;

    /// <summary>
    /// 允许下发任务的 OpenID 白名单，逗号、分号或换行分隔；留空表示不限制。
    /// <para>
    /// C2C 的 OpenID 与群成员 OpenID 不同，群聊场景可填群主或任意成员的 OpenID。
    /// 空名单配合群聊使用时风险较高，建议至少填上自己的 OpenID。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _qqBotAllowedOpenIds = string.Empty;

    /// <summary>
    /// 任务结束后是否自动回传一张游戏截图。
    /// </summary>
    [ObservableProperty]
    private bool _screenshotOnFinish = true;

    /// <summary>
    /// 回传截图的最长边像素，0 表示不缩放。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 3840, ErrorMessage = "截图最长边必须在 0 到 3840 之间。")]
    private int _screenshotMaxWidth = 1280;

    /// <summary>
    /// 回传截图 JPEG 质量（1-100）。数值越低，上传越快，QQ 侧限流风险越小。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 100, ErrorMessage = "截图质量必须在 1 到 100 之间。")]
    private int _screenshotQuality = 80;

    /// <summary>
    /// 单条回复的最大字符数，超出时截断并在末尾提示。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(100, 4000, ErrorMessage = "回复长度上限必须在 100 到 4000 之间。")]
    private int _replyMaxChars = 1200;
}
